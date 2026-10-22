using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiSeoPackageRequestBusiness
{
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId, RequestSeoPackageViewModel model, string idempotencyKey, CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid recipeId, Guid requestId, CancellationToken cancellationToken);

    /// <summary>The same request with an accepted long-form draft as its subject (LF.5.3).</summary>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestForLongFormAsync(
        Guid draftId, RequestLongFormSeoPackageViewModel model, string idempotencyKey, CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetForLongFormAsync(
        Guid draftId, Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// Queues an SEO package for one approved recipe version (RCPUB-002), through the same operation
/// lifecycle as every other AI task.
/// </summary>
/// <remarks>
/// Reads the recipe through <see cref="IRecipeFacade"/> and the brand profile through
/// <see cref="IBrandProfileFacade"/>, never their repositories. The brand facts are optional — a workspace
/// with no profile still gets a package — and are pinned by value into the operation's inputs. The style guide
/// (Phase 11A) does not exist yet, so the package has no voice pin; <c>ContentSourcePins</c> already treats a
/// voice appearing later as a change.
/// <para>
/// <strong>One capability, two subjects (LF.5.3).</strong> A package may instead be about an accepted long-form
/// draft, read through <see cref="ILongFormDocumentFacade"/>. It is the same task, the same rules and the same
/// quota; what differs is what must be true of the subject — accepted, at the revision named — and where the
/// subject is recorded. A draft is not a recipe, so the operation's recipe columns stay null and the draft and
/// its revision travel in the inputs. A recipe request stores exactly what it always has.
/// </para>
/// </remarks>
internal sealed class AiSeoPackageRequestBusiness(
    IAiOperationDataLayer operations,
    IAiRequestQuotaGate quota,
    IRecipeFacade recipes,
    IBrandProfileFacade brand,
    ILongFormDocumentFacade documents,
    SeoRules rules,
    IWorkspaceContext workspace,
    IAiTaskGate tasks,
    IClock clock) : IAiSeoPackageRequestBusiness
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId, RequestSeoPackageViewModel model, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!await tasks.IsEnabledAsync(AiTaskCatalog.SeoPackage, cancellationToken))
        {
            return Refuse(
                AiSeoPackageRequestErrors.TaskNotEnabled,
                "SEO packages are not enabled for this workspace's deployment.");
        }

        if (await quota.RefuseIfUnaffordableAsync(AiTaskType.SeoPackage, cancellationToken, idempotencyKey) is { } spent)
        {
            return spent;
        }

        var recipe = await recipes.GetDetailAsync(recipeId, cancellationToken);

        if (!recipe.Succeeded)
        {
            return Refuse(AiSeoPackageRequestErrors.RecipeNotFound, "That recipe does not exist.");
        }

        if (recipe.Value!.CurrentVersion?.Id != model.SourceVersionId)
        {
            return Refuse(
                AiSeoPackageRequestErrors.SourceVersionInvalid,
                "That version is not the current version of this recipe.");
        }

        // Approved, because the package is written about words the creator has cleared, and a package about a
        // draft would be stale the moment the draft changed.
        if (recipe.Value.Status is not RecipeStatus.Approved)
        {
            return Refuse(
                AiSeoPackageRequestErrors.RecipeNotApproved,
                "An SEO package is written about an approved recipe. Approve the recipe first.");
        }

        var profile = await brand.GetAsync(cancellationToken);
        var now = clock.UtcNow;

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.SeoPackage,

                // Not taken from the request. Advisory is what makes "proposes no change to the recipe" a
                // property of the stored row.
                Scope = AiOperationScope.Advisory,
                Status = AiOperationStatus.Requested,
                RecipeId = recipeId,
                RecipeVersionId = model.SourceVersionId,
                TaskInputsJson = SerializeInputs(
                    AiSeoSubject.Recipe, model.Sections, rules, profile.Succeeded ? profile.Value : null),
                IdempotencyKey = idempotencyKey,
                RequestedByMembershipId = workspace.MembershipId,
                RequestedAt = now,
                StatusChangedAt = now,
                AvailableAt = now,
            },
            cancellationToken);

        if (requested.Outcome is AiOperationRequestOutcome.KeyReusedForDifferentRequest)
        {
            return Refuse(IdempotencyPolicy.KeyReusedCode, "That idempotency key was already used for a different request.");
        }

        return new IdempotentOutcome<AiProposalStatusServiceModel>(
            OperationResult<AiProposalStatusServiceModel>.Success(AiOperationDescription.Describe(requested.Operation!, null)),
            Replayed: requested.Outcome is AiOperationRequestOutcome.Replayed);
    }

    public async Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid recipeId, Guid requestId, CancellationToken cancellationToken)
    {
        var operation = await operations.GetWithProposalAsync(requestId, cancellationToken);

        // One absence for four conditions: no such request, one in another workspace, one belonging to a
        // different recipe, and one that ran a different task.
        if (operation is null
            || operation.Operation.TaskType != AiTaskType.SeoPackage
            || operation.Operation.RecipeId != recipeId)
        {
            return Failure(AiSeoPackageRequestErrors.RequestNotFound, "That SEO package request does not exist.");
        }

        return OperationResult<AiProposalStatusServiceModel>.Success(
            AiOperationDescription.Describe(operation.Operation, operation.Proposal));
    }

    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestForLongFormAsync(
        Guid draftId, RequestLongFormSeoPackageViewModel model, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!await tasks.IsEnabledAsync(AiTaskCatalog.SeoPackage, cancellationToken))
        {
            return Refuse(
                AiSeoPackageRequestErrors.TaskNotEnabled,
                "SEO packages are not enabled for this workspace's deployment.");
        }

        if (await quota.RefuseIfUnaffordableAsync(AiTaskType.SeoPackage, cancellationToken, idempotencyKey) is { } spent)
        {
            return spent;
        }

        var read = await documents.GetDocumentAsync(draftId, cancellationToken);

        if (!read.Succeeded)
        {
            return Refuse(AiSeoPackageRequestErrors.DraftNotFound, "That long-form draft does not exist.");
        }

        var document = read.Value!;

        // Accepted, for the reason a recipe must be approved: the package is written about words the creator
        // has cleared. A piece whose sources have moved since is not claimed to be current, so neither would a
        // package about it be.
        if (document.Status is not LongFormDraftStatus.Accepted)
        {
            return Refuse(
                AiSeoPackageRequestErrors.DraftNotAccepted,
                document.Status is LongFormDraftStatus.NeedsReview
                    ? "Something this piece was written from has changed since it was accepted. Review it first."
                    : "An SEO package is written about an accepted piece. Accept it first.");
        }

        if (document.AcceptedRevisionNumber != model.RevisionNumber)
        {
            return Refuse(
                AiSeoPackageRequestErrors.DraftRevisionInvalid,
                "That revision is not the accepted revision of this piece.");
        }

        var profile = await brand.GetAsync(cancellationToken);
        var now = clock.UtcNow;

        var inputs = SerializeInputs(
            AiSeoSubject.Article,
            model.Sections,
            rules,
            profile.Succeeded ? profile.Value : null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AiSeoPackageInputs.LongFormDraftId] = draftId.ToString("D"),
                [AiSeoPackageInputs.LongFormRevisionNumber] =
                    model.RevisionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.SeoPackage,

                // Not taken from the request, and NotApplicable: the operation names no recipe, and the scope
                // is what makes "no stored row here has a path to a recipe edit" a property of the row.
                Scope = AiOperationScope.NotApplicable,
                Status = AiOperationStatus.Requested,
                RecipeId = null,
                RecipeVersionId = null,
                TaskInputsJson = inputs,
                IdempotencyKey = idempotencyKey,
                RequestedByMembershipId = workspace.MembershipId,
                RequestedAt = now,
                StatusChangedAt = now,
                AvailableAt = now,
            },
            cancellationToken);

        if (requested.Outcome is AiOperationRequestOutcome.KeyReusedForDifferentRequest)
        {
            return Refuse(IdempotencyPolicy.KeyReusedCode, "That idempotency key was already used for a different request.");
        }

        return new IdempotentOutcome<AiProposalStatusServiceModel>(
            OperationResult<AiProposalStatusServiceModel>.Success(AiOperationDescription.Describe(requested.Operation!, null)),
            Replayed: requested.Outcome is AiOperationRequestOutcome.Replayed);
    }

    public async Task<OperationResult<AiProposalStatusServiceModel>> GetForLongFormAsync(
        Guid draftId, Guid requestId, CancellationToken cancellationToken)
    {
        var operation = await operations.GetWithProposalAsync(requestId, cancellationToken);

        // One absence, as for a recipe: no such request, one in another workspace, one about a different
        // draft or about a recipe, and one that ran a different task.
        if (operation is null
            || operation.Operation.TaskType != AiTaskType.SeoPackage
            || AiSeoPackageInputs.ReadLongFormDraftId(ReadInputs(operation.Operation.TaskInputsJson)) != draftId)
        {
            return Failure(AiSeoPackageRequestErrors.RequestNotFound, "That SEO package request does not exist.");
        }

        return OperationResult<AiProposalStatusServiceModel>.Success(
            AiOperationDescription.Describe(operation.Operation, operation.Proposal));
    }

    private static Dictionary<string, string>? ReadInputs(string? inputsJson)
    {
        try
        {
            return inputsJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(inputsJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <param name="subjectInputs">
    /// What names an article subject. Null for a recipe, whose inputs must stay exactly what they were before
    /// there was a second subject: they are what a replayed key is compared with, and what a stored operation
    /// already holds.
    /// </param>
    private static string SerializeInputs(
        AiSeoSubject subject,
        List<string>? requestedSections,
        SeoRules rules,
        Brand.Managers.BrandProfileServiceModel? profile,
        Dictionary<string, string>? subjectInputs = null)
    {
        var sections = requestedSections is { Count: > 0 }
            ? requestedSections.Select(section => AiSeoSectionCatalog.Parse(section)!.Value).Distinct().Order().ToList()
            : [.. AiSeoSubjects.SectionsFor(subject)];

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AiSeoPackageInputs.Sections] = string.Join(',', sections.Select(AiSeoSectionCatalog.ToWire)),

            // The rules this request is judged by. The handler refuses to run against any other version.
            [AiSeoPackageInputs.RuleSet] = rules.Version,
        };

        if (profile is not null)
        {
            values[AiSeoPackageInputs.BrandProfileRevision] = profile.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            values[AiSeoPackageInputs.BrandName] = profile.BrandName;

            if (!string.IsNullOrWhiteSpace(profile.DefaultAudience)) values[AiSeoPackageInputs.Audience] = profile.DefaultAudience;
            if (!string.IsNullOrWhiteSpace(profile.Locale)) values[AiSeoPackageInputs.Locale] = profile.Locale;
        }

        foreach (var (key, value) in subjectInputs ?? [])
        {
            values[key] = value;
        }

        return JsonSerializer.Serialize(values);
    }

    private static IdempotentOutcome<AiProposalStatusServiceModel> Refuse(string code, string message) =>
        new(Failure(code, message), Replayed: false);

    private static OperationResult<AiProposalStatusServiceModel> Failure(string code, string message) =>
        OperationResult<AiProposalStatusServiceModel>.Failure(new OperationError(code, message, new Dictionary<string, string[]>()));
}
