using System.Globalization;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Managers;

namespace CreatorPantry.Domain.Modules.Media.Facade;

/// <summary>
/// The application boundary for image generation (IMG-003): requesting one, and running a claimed one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No controller calls this yet.</strong> 12.7 adds the gateway and the job; the HTTP route arrives
/// with the Image Studio screen that needs it. The seam is a facade rather than something the worker calls
/// directly because backend.md allows a worker exactly one entry point into a module, and because the
/// request path and the run path have to agree about what an operation is.
/// </para>
/// <para>
/// <strong>Both halves resolve the workspace before they are reached.</strong>
/// <see cref="RequestAsync"/> runs under a creator's resolved membership;
/// <see cref="ExecuteAsync"/> runs under the service resolution the worker performed after claiming. Neither
/// accepts a workspace identifier, and no model-supplied one exists anywhere on this path (ai.md).
/// </para>
/// </remarks>
public interface IGeneratedImageGenerationFacade
{
    /// <summary>
    /// Records a request for the resolved workspace, or returns the one its idempotency key already bought.
    /// </summary>
    /// <remarks>
    /// A replay is a success, not a conflict: the caller asked for images and there are images coming. The
    /// returned model is the first operation, so a client that lost its answer finds the same one.
    /// </remarks>
    Task<OperationResult<GeneratedImageOperationServiceModel>> RequestAsync(
        GeneratedImageRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Runs one claimed operation. The Worker only, and that is checked rather than trusted.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="GeneratedImageRunOutcome.Skipped"/> for a caller that is not the service
    /// identity. The lease token already bounds the damage a stray caller could do — it would have to
    /// guess a live one — but "never routed" was enforced by a comment, and a comment is not a check.
    /// A creator retrying a failed generation queues a new request; nothing a person does reaches here.
    /// </remarks>
    Task<GeneratedImageRunOutcome> ExecuteAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken);

    /// <summary>
    /// One operation of the resolved workspace, with the images it has produced so far.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Any member may read, which is why it is here and not behind the Contributor gate above.</strong>
    /// Asking for images spends the workspace's budget; looking at what came back does not, and the preview and
    /// download routes this read feeds are themselves Viewer. A Viewer who can see an image but not learn that it
    /// exists would be a contract at odds with itself.
    /// </para>
    /// <para>
    /// An unknown operation and another workspace's are one answer, so neither discloses the other (tenancy.md).
    /// </para>
    /// </remarks>
    Task<OperationResult<GeneratedImageOperationDetailServiceModel>> GetOperationAsync(
        Guid operationId, CancellationToken cancellationToken);

    /// <summary>
    /// What asking for <paramref name="variants"/> pictures would cost the caller and leave them, before they
    /// ask (PRICE-004, AP.2.6). Read-only: nothing is held or written.
    /// </summary>
    /// <remarks>
    /// Contributor, the same as the request it prices: a Viewer cannot ask for pictures, so has no price to see.
    /// The count is checked as the request checks it, so a quote can never be for a request that would be refused
    /// as invalid.
    /// </remarks>
    /// <param name="variants">How many pictures to price.</param>
    /// <param name="qualityClass">The class to price; null for the host's default (AP.3.1).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<OperationResult<GeneratedImageQuoteServiceModel>> QuoteAsync(
        int? variants, GeneratedImageQualityClass? qualityClass, CancellationToken cancellationToken);

    /// <summary>
    /// The quality classes a creator may ask for here, each with what one picture of it costs (AP.3.1, B-55).
    /// </summary>
    /// <remarks>
    /// Contributor, like the quote and the request: the list exists to choose between, and a Viewer cannot ask.
    /// Read-only and the same for every member — a price list, not the asker's balance.
    /// </remarks>
    Task<OperationResult<GeneratedImageQualityClassesServiceModel>> ListQualityClassesAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// How a prompt and its avoid list measure against the model a quality class sends to (AP.3.2) — the same
    /// measure a request for pictures is refused on. Reads configuration only.
    /// </summary>
    /// <remarks>
    /// Contributor, like the request it measures. Null as a success when there is nothing to measure against:
    /// no image deployment, or one that will not say its limit. A class this host does not offer is the
    /// request's own refusal.
    /// </remarks>
    /// <param name="promptText">The prompt, as it would be sent.</param>
    /// <param name="avoidText">The avoid list, as it would be sent, or null for none.</param>
    /// <param name="qualityClass">The class to measure against; null for the host's default.</param>
    OperationResult<GeneratedImagePromptFitServiceModel?> MeasurePrompt(
        string promptText, string? avoidText, GeneratedImageQualityClass? qualityClass);
}

/// <inheritdoc cref="IGeneratedImageGenerationFacade"/>
internal sealed class GeneratedImageGenerationFacade(
    IGeneratedImageGenerationBusiness business,
    IAiProposalLookupFacade proposals,
    IRecipeFacade recipes,
    ICreativeContextLookupFacade contexts,
    IWorkspaceContext workspace) : IGeneratedImageGenerationFacade
{
    public async Task<OperationResult<GeneratedImageOperationDetailServiceModel>> GetOperationAsync(
        Guid operationId, CancellationToken cancellationToken)
    {
        // No role gate: a resolved workspace context is the authorization for the lowest role, and the routes
        // this feeds are Viewer too.
        var detail = await business.FindDetailAsync(operationId, cancellationToken);

        return detail is null
            ? OperationResult<GeneratedImageOperationDetailServiceModel>.Failure(new OperationError(
                MediaErrorCodes.GenerationOperationNotFound,
                "That image request could not be found in this workspace.",
                new Dictionary<string, string[]>()))
            : OperationResult<GeneratedImageOperationDetailServiceModel>.Success(detail);
    }

    /// <summary>
    /// The refusal an unaffordable generation is answered with (AP.2.5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shipped quota codes, not Media's own: an exhausted allowance is one fact however the account tried
    /// to spend it, and a second pair of codes would make a client learn two ways to read "the month is spent".
    /// <c>ProblemResults</c> already maps them to 429 and 403 with a <c>Retry-After</c>.
    /// </para>
    /// <para>
    /// <strong>Unrated reads as exhausted-shaped but is not.</strong> A deployment with no rate for the
    /// picture it would make cannot price the request, which is this host's misconfiguration rather than the
    /// creator's balance — startup refuses such a host, so reaching it means a model nothing profiled. It is
    /// answered as a provider problem, with no figures, because quoting an allowance would tell a creator to
    /// wait for a reset that will not help.
    /// </para>
    /// </remarks>
    private static OperationError QuotaRefusal(AiQuotaAdmissionServiceModel refusal) => refusal.Outcome switch
    {
        AiQuotaAdmissionOutcome.Suspended => new OperationError(
            AiQuotaErrors.Suspended,
            "AI assistance is switched off for your account.",
            new Dictionary<string, string[]>(),
            new Dictionary<string, object?> { ["unit"] = refusal.Unit.ToString() }),

        AiQuotaAdmissionOutcome.Unrated => new OperationError(
            MediaErrorCodes.GenerationUnpriceable,
            "Images cannot be generated on this host right now.",
            new Dictionary<string, string[]>()),

        // The platform's daily image spend breaker (AP.2.7). Deliberately no extensions at all: no unit, no
        // remaining figure, no reset. The account may have plenty left, and every figure behind this answer is
        // other people's usage. The request the creator filled in is untouched; nothing was written.
        AiQuotaAdmissionOutcome.PlatformCeiling => new OperationError(
            AiQuotaErrors.CapacityUnavailable,
            "Pictures can't be made right now. Please try again later.",
            new Dictionary<string, string[]>()),

        // The plan's daily picture cap (AP.4.2): the account's own day across every workspace, said as every other
        // daily limit is — a 429 with the cap and the reset, and no balance, which is not why.
        AiQuotaAdmissionOutcome.DailyImageCap => DailyCapRefusal(refusal),

        // Exhausted, and Busy with it: a period too contended to admit against is not a statement about the
        // balance, but a creator cannot act on "try again, we could not read your allowance" any differently.
        _ => new OperationError(
            AiQuotaErrors.Exhausted,
            "Your AI allowance for this period is spent. It comes back when the period resets.",
            new Dictionary<string, string[]>(),
            new Dictionary<string, object?>
            {
                ["unit"] = refusal.Unit.ToString(),
                ["remaining"] = refusal.Remaining,
                ["required"] = refusal.Required,
                ["resetsAt"] = refusal.PeriodEndsAt,
            }),
    };

    private static OperationError DailyCapRefusal(AiQuotaAdmissionServiceModel refusal)
    {
        LimitsTelemetry.Refused(LimitedOperation.ImageGeneration, "budget", LimitScope.User);

        // Both are set on every refusal of this kind; admission is the only thing that makes one.
        return LimitRefusal.Budget(
            LimitedOperation.ImageGeneration,
            refusal.DailyImageCap!.Value,
            refusal.DailyImageCapResetsAt!.Value,
            LimitScope.User);
    }

    public Task<OperationResult<GeneratedImageQualityClassesServiceModel>> ListQualityClassesAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(workspace.Role < WorkspaceRole.Contributor
            ? OperationResult<GeneratedImageQualityClassesServiceModel>.Failure(new OperationError(
                MediaErrorCodes.GenerationForbidden,
                "You do not have permission to generate images in this workspace.",
                new Dictionary<string, string[]>()))
            : OperationResult<GeneratedImageQualityClassesServiceModel>.Success(business.ListQualityClasses()));

    /// <summary>
    /// Whether a replayed key asked for the same quality as the request that took it (AP.3.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A retry that names no class is the request it retries, whatever class that was made at.</strong>
    /// A lost answer is retried with the body it was sent with, and the default it resolved to may have changed
    /// in between; refusing it then would strand a creator on a key that can never succeed. What a key must not
    /// do is buy a different class by naming one, so a retry that names a class is compared with the class
    /// stored — which makes naming the default the same request as naming none, while the default stands.
    /// </para>
    /// <para>
    /// An operation with no class was made where no class applied — before this column existed, or on a host
    /// offering none — and only a retry that names none is that request.
    /// </para>
    /// </remarks>
    private static bool SameQuality(
        GeneratedImageQualityClass? stored, GeneratedImageQualityClass? requested, GeneratedImageQualityClass? chosen) =>
        requested is null || (stored is not null && stored == chosen);

    /// <summary>
    /// The refusal for a class this host does not offer, naming the field, in the same words for a quote and a
    /// request so a client reads one case.
    /// </summary>
    private static OperationError QualityClassNotOffered(string message) =>
        OperationError.Validation(
            MediaErrorCodes.GenerationQualityClassUnprocessable,
            message,
            [("qualityClass", "That picture quality is not offered here.")]);

    /// <summary>
    /// The refusal for a prompt the chosen class's model cannot take, naming the part that has to change and
    /// carrying the figures a client acts on (AP.3.2).
    /// </summary>
    private static OperationError PromptDoesNotFit(GeneratedImagePromptFitServiceModel fit, string message)
    {
        var refusal = fit.Outcome is GeneratedImagePromptFitOutcome.AvoidTooLong
            ? OperationError.Validation(
                MediaErrorCodes.GenerationAvoidUnprocessable,
                message,
                [("avoidText", string.Format(
                    CultureInfo.InvariantCulture,
                    "Pictures at this quality take {0:N0} characters, and the avoid list alone leaves room for "
                        + "fewer than {1} characters of prompt. Shorten the avoid list.",
                    fit.Limit,
                    MediaPolicy.ShortestPromptBudget))])
            : OperationError.Validation(
                MediaErrorCodes.GenerationPromptUnprocessable,
                message,
                [("promptText", string.Format(
                    CultureInfo.InvariantCulture,
                    "Pictures at this quality take {0:N0} characters, and this prompt with its avoid list is "
                        + "{1:N0}. Keep the prompt to {2:N0} characters, or ask for a shorter version of it.",
                    fit.Limit,
                    fit.Length,
                    fit.PromptBudget))]);

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

    public OperationResult<GeneratedImagePromptFitServiceModel?> MeasurePrompt(
        string promptText, string? avoidText, GeneratedImageQualityClass? qualityClass)
    {
        ArgumentNullException.ThrowIfNull(promptText);

        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return OperationResult<GeneratedImagePromptFitServiceModel?>.Failure(new OperationError(
                MediaErrorCodes.GenerationForbidden,
                "You do not have permission to generate images in this workspace.",
                new Dictionary<string, string[]>()));
        }

        if (!business.TryChooseQualityClass(qualityClass, out var chosen))
        {
            return OperationResult<GeneratedImagePromptFitServiceModel?>.Failure(
                QualityClassNotOffered("The prompt could not be measured."));
        }

        return OperationResult<GeneratedImagePromptFitServiceModel?>.Success(business.MeasurePrompt(
            promptText.Trim(),
            string.IsNullOrWhiteSpace(avoidText) ? null : avoidText.Trim(),
            chosen));
    }

    public async Task<OperationResult<GeneratedImageQuoteServiceModel>> QuoteAsync(
        int? variants, GeneratedImageQualityClass? qualityClass, CancellationToken cancellationToken)
    {
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return OperationResult<GeneratedImageQuoteServiceModel>.Failure(new OperationError(
                MediaErrorCodes.GenerationForbidden,
                "You do not have permission to generate images in this workspace.",
                new Dictionary<string, string[]>()));
        }

        if (variants is not { } count || count < 1 || count > MediaPolicy.MaxVariantsPerOperation)
        {
            return OperationResult<GeneratedImageQuoteServiceModel>.Failure(OperationError.Validation(
                MediaErrorCodes.GenerationInvalidRequest,
                "The images could not be priced.",
                [("variants", $"Ask about between 1 and {MediaPolicy.MaxVariantsPerOperation} images.")]));
        }

        if (!business.TryChooseQualityClass(qualityClass, out var chosen))
        {
            return OperationResult<GeneratedImageQuoteServiceModel>.Failure(
                QualityClassNotOffered("The images could not be priced."));
        }

        return OperationResult<GeneratedImageQuoteServiceModel>.Success(
            await business.QuoteAsync(count, chosen, cancellationToken));
    }

    public async Task<OperationResult<GeneratedImageOperationServiceModel>> RequestAsync(
        GeneratedImageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Contributor, not Editor. Generating images produces candidates a creator then chooses between —
        // nothing a generation does is canonical, and nothing it does is published. Keeping it above Viewer
        // is what matters: every variant is a charge against the workspace.
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return OperationResult<GeneratedImageOperationServiceModel>.Failure(new OperationError(
                MediaErrorCodes.GenerationForbidden,
                "You do not have permission to generate images in this workspace.",
                new Dictionary<string, string[]>()));
        }

        var failures = GeneratedImageInputChecks.Request(request).ToList();

        if (failures.Count > 0)
        {
            return OperationResult<GeneratedImageOperationServiceModel>.Failure(OperationError.Validation(
                MediaErrorCodes.GenerationInvalidRequest, "The images could not be requested.", failures));
        }

        // The class, decided before anything else about the request is looked up: a request at a quality this
        // host cannot make is refused in words, and nothing is held or queued for it (AP.3.1). A replayed key is
        // the exception, because a replay is never refused: its answer is the operation it already bought, even
        // when the class it was bought at has since been taken off this host — and comparing that operation with
        // what is asked now is the key check's job below, not this one's.
        if (!business.TryChooseQualityClass(request.QualityClass, out var qualityClass))
        {
            if (!await business.IsReplayAsync(request.IdempotencyKey.Trim(), cancellationToken))
            {
                return OperationResult<GeneratedImageOperationServiceModel>.Failure(
                    QualityClassNotOffered("The images could not be requested."));
            }

            qualityClass = request.QualityClass;
        }

        var promptText = request.PromptText.Trim();
        var avoidText = string.IsNullOrWhiteSpace(request.AvoidText) ? null : request.AvoidText.Trim();

        // What would be sent has to fit the model the class sends to (AP.3.2). The generator refuses an over-long
        // prompt rather than cutting it, but only in the worker, after the pictures were held for — so the same
        // measure is asked here, before anything is held or queued. A replay is still never refused.
        if (business.MeasurePrompt(promptText, avoidText, qualityClass) is { Fits: false } fit
            && !await business.IsReplayAsync(request.IdempotencyKey.Trim(), cancellationToken))
        {
            return OperationResult<GeneratedImageOperationServiceModel>.Failure(
                PromptDoesNotFit(fit, "The images could not be requested."));
        }

        // The proposal the prompt came from, when one is named, has to be one of this workspace's (12.10l).
        // The composite key on the operation already refuses anything else, but as an exception nobody reads:
        // asked here, through the Ai module's own facade, it is a refusal in words. An id that names nothing
        // and another workspace's proposal are one answer, so this cannot be used to ask what a neighbour has
        // generated (tenancy.md).
        if (request.AiProposalId is { } proposalId && !await proposals.ExistsAsync(proposalId, cancellationToken))
        {
            return OperationResult<GeneratedImageOperationServiceModel>.Failure(OperationError.Validation(
                MediaErrorCodes.GenerationProposalUnprocessable,
                "The images could not be requested.",
                [(nameof(request.AiProposalId), "That prompt proposal is not in this workspace.")]));
        }

        var subject = request.Subject ?? GeneratedImageSubject.None;

        // What the run is for has to be this workspace's too, and for the same reason and in the same way:
        // each is asked through its own module's facade under the workspace filter, so an id that names
        // nothing and a neighbour's are one answer, and the composite keys on the operation never get to
        // refuse it as an exception. A version is asked for through its recipe, so a version of some other
        // recipe is that same answer.
        if (subject.RecipeId is { } recipeId)
        {
            var known = subject.RecipeVersionId is { } versionId
                ? (await recipes.GetSnapshotAsync(recipeId, versionId, cancellationToken)).Succeeded
                : (await recipes.GetDetailAsync(recipeId, cancellationToken)).Succeeded;

            if (!known)
            {
                return OperationResult<GeneratedImageOperationServiceModel>.Failure(OperationError.Validation(
                    MediaErrorCodes.GenerationRecipeUnprocessable,
                    "The images could not be requested.",
                    [(subject.RecipeVersionId is null ? nameof(subject.RecipeId) : nameof(subject.RecipeVersionId),
                        "That recipe is not in this workspace.")]));
            }
        }

        if (subject.CreativeContextId is { } contextId && !await contexts.ExistsAsync(contextId, cancellationToken))
        {
            return OperationResult<GeneratedImageOperationServiceModel>.Failure(OperationError.Validation(
                MediaErrorCodes.GenerationContextUnprocessable,
                "The images could not be requested.",
                [(nameof(subject.CreativeContextId), "That piece of work is not in this workspace.")]));
        }

        // After every check that is about the request and before anything is written (15.3): a refused request
        // leaves no operation behind, and a replay of one already bought is never refused.
        if (await business.RefuseIfOverLimitsAsync(request.IdempotencyKey.Trim(), request.VariantCount, cancellationToken)
            is { } overLimit)
        {
            return OperationResult<GeneratedImageOperationServiceModel>.Failure(overLimit);
        }

        var result = await business.RequestAsync(
            promptText,
            avoidText,
            request.AiProposalId,
            request.VariantCount,
            workspace.MembershipId,
            request.IdempotencyKey.Trim(),
            subject,
            qualityClass,
            cancellationToken);

        // The account could not pay for the pictures (PRICE-001, USAGE-006/007). Nothing was written and
        // nothing is queued, and the refusal carries this account's own figures and names no workspace —
        // the same problem family a completion is refused with, because it is the same allowance.
        if (result.Operation is null)
        {
            return OperationResult<GeneratedImageOperationServiceModel>.Failure(
                QuotaRefusal(result.Refusal!));
        }

        // A key names one request. Found again with anything about the request different — the words, the
        // number of images, the quality, the proposal, or who is asking — it is a second request wearing the
        // first one's key, and answering it with the first one's operation would hand back images of something
        // else (12.10l). Refused as every other idempotent command refuses it, and nothing is queued.
        if (result.Replayed
            && !(string.Equals(result.Operation.PromptText, promptText, StringComparison.Ordinal)
                && string.Equals(result.Operation.AvoidText, avoidText, StringComparison.Ordinal)
                && result.Operation.AiProposalId == request.AiProposalId
                && result.Operation.RecipeId == subject.RecipeId
                && result.Operation.RecipeVersionId == subject.RecipeVersionId
                && result.Operation.CreativeContextId == subject.CreativeContextId
                && result.Operation.VariantCount == request.VariantCount
                && SameQuality(result.Operation.QualityClass, request.QualityClass, qualityClass)
                && result.Operation.RequestedByMembershipId == workspace.MembershipId))
        {
            return OperationResult<GeneratedImageOperationServiceModel>.Failure(new OperationError(
                IdempotencyPolicy.KeyReusedCode,
                "That idempotency key was already used for a different image request.",
                new Dictionary<string, string[]>()));
        }

        // Zero on a fresh request without a round trip; the real count only matters for a replay, where the
        // caller is asking what became of the operation it already has.
        var staged = result.Replayed
            ? await business.StagedCountAsync(result.Operation.Id, cancellationToken)
            : 0;

        return OperationResult<GeneratedImageOperationServiceModel>.Success(
            new GeneratedImageOperationServiceModel(
                result.Operation.Id,
                result.Operation.Status,
                result.Operation.VariantCount,
                staged,
                result.Operation.ProviderName,
                result.Operation.ModelName,
                result.Operation.FailureCategory,
                result.Operation.FailureSummary,
                result.Operation.RequestedAt,
                result.Operation.CompletedAt,
                result.Operation.RecipeId,
                result.Operation.RecipeVersionId,
                result.Operation.CreativeContextId,
                result.Operation.QualityClass));
    }

    public Task<GeneratedImageRunOutcome> ExecuteAsync(
        Guid operationId, Guid leaseToken, CancellationToken cancellationToken) =>
        workspace.MembershipId == WorkspaceServiceIdentity.MembershipId
            ? business.ExecuteAsync(operationId, leaseToken, cancellationToken)
            : Task.FromResult(GeneratedImageRunOutcome.Skipped);
}
