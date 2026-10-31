using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiImagePromptCompactionRequestBusiness
{
    /// <inheritdoc cref="IAiProposalBusiness.RequestAsync"/>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestImagePromptCompactionViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

/// <summary>
/// The rules around asking for a shorter image prompt (AP.3.2): that image prompts are enabled, that the prompt
/// really does not fit the chosen quality's model, and that a shorter one could.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Measured by Media, through its facade, with the same arithmetic the picture request is refused
/// on</strong> — so a creator is never offered a shorter prompt for a request that would have gone through, and
/// the budget the model is given is exactly the room the picture request leaves. The figures are stored with the
/// operation rather than measured again in the worker: what the creator was told is what the model is held to.
/// </para>
/// <para>
/// <strong>A replayed key is never measured.</strong> Its answer is the operation it already bought, whatever the
/// limit has become since, so the measurement — which reads configuration that may have changed — is skipped for
/// it, and the stored limit and budget are left out of what makes two requests the same.
/// </para>
/// </remarks>
internal sealed class AiImagePromptCompactionRequestBusiness(
    IAiOperationDataLayer operations,
    IAiRequestQuotaGate quota,
    IGeneratedImageGenerationFacade images,
    IWorkspaceContext workspace,
    IAiTaskGate tasks,
    IClock clock) : IAiImagePromptCompactionRequestBusiness
{
    private static readonly JsonSerializerOptions InputsJson = new()
    {
        // The prompt is stored in full, and the default encoder would spell every accent and apostrophe as six
        // characters against the stored-inputs ceiling.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestImagePromptCompactionViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The same switch as composing one: a shorter prompt is the image-prompt task, with another template.
        if (!await tasks.IsEnabledAsync(AiTaskCatalog.ImagePrompt, cancellationToken))
        {
            return Refuse(
                AiImagePromptRequestErrors.TaskNotEnabled,
                "Image prompts are not enabled for this workspace's deployment.");
        }

        // Trimmed as the picture request trims them, so what is measured, shortened and later sent is one text.
        var prompt = model.PromptText!.Trim();
        var avoid = string.IsNullOrWhiteSpace(model.AvoidText) ? null : model.AvoidText.Trim();

        var inputs = new Dictionary<string, string>(5, StringComparer.Ordinal)
        {
            [ImagePromptCompactionInputs.SourcePrompt] = prompt,
        };

        if (avoid is not null)
        {
            inputs[ImagePromptCompactionInputs.Avoid] = avoid;
        }

        if (model.QualityClass is { } qualityClass)
        {
            inputs[ImagePromptCompactionInputs.QualityClass] = qualityClass.ToString();
        }

        if (!await operations.IsReplayAsync(idempotencyKey, cancellationToken))
        {
            var measured = images.MeasurePrompt(prompt, avoid, model.QualityClass);

            // A class this host does not offer, or a member who may not ask for pictures: Media's own refusal, in
            // its own words, because it is the same refusal the picture request would give.
            if (!measured.Succeeded)
            {
                return new IdempotentOutcome<AiProposalStatusServiceModel>(
                    OperationResult<AiProposalStatusServiceModel>.Failure(measured.Error!), Replayed: false);
            }

            if (measured.Value is not { } fit)
            {
                return Refuse(
                    AiImagePromptCompactionErrors.LimitUnknown,
                    "This deployment cannot say how long a prompt its pictures take, so there is no length to "
                        + "shorten to.");
            }

            if (fit.Outcome is not GeneratedImagePromptFitOutcome.NeedsShortening)
            {
                return new IdempotentOutcome<AiProposalStatusServiceModel>(
                    OperationResult<AiProposalStatusServiceModel>.Failure(DoesNotNeedShortening(fit)),
                    Replayed: false);
            }

            inputs[ImagePromptCompactionInputs.CharacterLimit] = fit.Limit.ToString(CultureInfo.InvariantCulture);
            inputs[ImagePromptCompactionInputs.PromptBudget] = fit.PromptBudget.ToString(CultureInfo.InvariantCulture);
        }

        var serialized = JsonSerializer.Serialize(inputs, InputsJson);

        if (serialized.Length > AiPolicy.TaskInputsJsonMaxLength)
        {
            return Refuse(
                AiImagePromptCompactionErrors.RequestTooLarge,
                "The prompt and avoid list are too long together to shorten in one request. Shorten the avoid list.");
        }

        if (await quota.RefuseIfUnaffordableAsync(AiTaskType.ImagePrompt, cancellationToken, idempotencyKey) is { } spent)
        {
            return spent;
        }

        var now = clock.UtcNow;

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.ImagePrompt,

                // Fixed: a shorter prompt names no recipe and changes none.
                Scope = AiOperationScope.NotApplicable,
                Status = AiOperationStatus.Requested,
                IdempotencyKey = idempotencyKey,
                TaskInputsJson = serialized,
                RequestedByMembershipId = workspace.MembershipId,
                RequestedAt = now,
                StatusChangedAt = now,
                AvailableAt = now,
            },
            cancellationToken);

        if (requested.Outcome is AiOperationRequestOutcome.KeyReusedForDifferentRequest)
        {
            return Refuse(
                IdempotencyPolicy.KeyReusedCode,
                "That idempotency key was already used for a different request.");
        }

        return new IdempotentOutcome<AiProposalStatusServiceModel>(
            OperationResult<AiProposalStatusServiceModel>.Success(
                AiOperationDescription.Describe(requested.Operation!, null)),
            Replayed: requested.Outcome is AiOperationRequestOutcome.Replayed);
    }

    /// <summary>
    /// The refusal for a prompt that fits already, or whose avoid list leaves no room — each naming the field to
    /// change and carrying the figures the picture request's own refusal carries.
    /// </summary>
    private static OperationError DoesNotNeedShortening(GeneratedImagePromptFitServiceModel fit)
    {
        var refusal = fit.Fits
            ? OperationError.Validation(
                AiImagePromptCompactionErrors.AlreadyFits,
                "The prompt does not need shortening.",
                [("promptText", string.Format(
                    CultureInfo.InvariantCulture,
                    "This prompt and its avoid list are {0:N0} characters, within the {1:N0} pictures at this "
                        + "quality take. Make the pictures with it as it is.",
                    fit.Length,
                    fit.Limit))])
            : OperationError.Validation(
                AiImagePromptCompactionErrors.AvoidTooLong,
                "The prompt cannot be shortened enough.",
                [("avoidText", string.Format(
                    CultureInfo.InvariantCulture,
                    "Pictures at this quality take {0:N0} characters, and the avoid list alone leaves room for "
                        + "fewer than {1} characters of prompt. Shorten the avoid list.",
                    fit.Limit,
                    AiPolicy.ImagePromptMinLength))]);

        return refusal with
        {
            Extensions = new Dictionary<string, object?>
            {
                ["characterLimit"] = fit.Limit,
                ["characterCount"] = fit.Length,
                ["promptBudget"] = fit.PromptBudget,
            },
        };
    }

    private static IdempotentOutcome<AiProposalStatusServiceModel> Refuse(string code, string message) =>
        new(
            OperationResult<AiProposalStatusServiceModel>.Failure(
                new OperationError(code, message, new Dictionary<string, string[]>())),
            Replayed: false);
}
