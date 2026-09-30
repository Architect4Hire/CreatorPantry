using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiEditorialPackageRequestBusiness
{
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId, RequestEditorialPackageViewModel model, string idempotencyKey, CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid recipeId, Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// Queues an editorial package for one approved recipe version (RCPUB-001), through the same operation
/// lifecycle as every other AI task.
/// </summary>
/// <remarks>
/// Reads the recipe through <see cref="IRecipeFacade"/> and the brand profile through
/// <see cref="IBrandProfileFacade"/>, never their repositories. The brand facts are optional — a workspace
/// with no profile still gets a package — and are pinned by value into the operation's inputs. The style guide
/// (Phase 11A) does not exist yet, so the package has no voice pin; <c>ContentSourcePins</c> already treats a
/// voice appearing later as a change.
/// </remarks>
internal sealed class AiEditorialPackageRequestBusiness(
    IAiOperationDataLayer operations,
    IAiRequestQuotaGate quota,
    IRecipeFacade recipes,
    IBrandProfileFacade brand,
    IWorkspaceContext workspace,
    AiTaskOptions tasks,
    IClock clock) : IAiEditorialPackageRequestBusiness
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId, RequestEditorialPackageViewModel model, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!AiTaskCatalog.IsEnabled(tasks, AiTaskCatalog.EditorialPackage))
        {
            return Refuse(
                AiEditorialPackageRequestErrors.TaskNotEnabled,
                "Editorial packages are not enabled for this workspace's deployment.");
        }

        if (await quota.RefuseIfUnaffordableAsync(AiTaskType.EditorialPackage, cancellationToken) is { } spent)
        {
            return spent;
        }

        var recipe = await recipes.GetDetailAsync(recipeId, cancellationToken);

        if (!recipe.Succeeded)
        {
            return Refuse(AiEditorialPackageRequestErrors.RecipeNotFound, "That recipe does not exist.");
        }

        if (recipe.Value!.CurrentVersion?.Id != model.SourceVersionId)
        {
            return Refuse(
                AiEditorialPackageRequestErrors.SourceVersionInvalid,
                "That version is not the current version of this recipe.");
        }

        // Approved, because the package is written about words the creator has cleared, and a package about a
        // draft would be stale the moment the draft changed.
        if (recipe.Value.Status is not RecipeStatus.Approved)
        {
            return Refuse(
                AiEditorialPackageRequestErrors.RecipeNotApproved,
                "An editorial package is written about an approved recipe. Approve the recipe first.");
        }

        var profile = await brand.GetAsync(cancellationToken);
        var now = clock.UtcNow;

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.EditorialPackage,

                // Not taken from the request. Advisory is what makes "proposes no change to the recipe" a
                // property of the stored row.
                Scope = AiOperationScope.Advisory,
                Status = AiOperationStatus.Requested,
                RecipeId = recipeId,
                RecipeVersionId = model.SourceVersionId,
                TaskInputsJson = SerializeInputs(model, profile.Succeeded ? profile.Value : null),
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
            || operation.Operation.TaskType != AiTaskType.EditorialPackage
            || operation.Operation.RecipeId != recipeId)
        {
            return Failure(AiEditorialPackageRequestErrors.RequestNotFound, "That editorial package request does not exist.");
        }

        return OperationResult<AiProposalStatusServiceModel>.Success(
            AiOperationDescription.Describe(operation.Operation, operation.Proposal));
    }

    private static string SerializeInputs(
        RequestEditorialPackageViewModel model, Brand.Managers.BrandProfileServiceModel? profile)
    {
        var sections = model.Sections is { Count: > 0 }
            ? model.Sections.Select(section => AiEditorialSectionCatalog.Parse(section)!.Value).Distinct().Order().ToList()
            : [.. AiEditorialSectionCatalog.All];

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AiEditorialPackageInputs.Sections] = string.Join(',', sections.Select(EditorialPackageAiTaskHandler.ToWire)),
        };

        if (profile is not null)
        {
            values[AiEditorialPackageInputs.BrandProfileRevision] = profile.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            values[AiEditorialPackageInputs.BrandName] = profile.BrandName;

            if (!string.IsNullOrWhiteSpace(profile.DefaultAudience)) values[AiEditorialPackageInputs.Audience] = profile.DefaultAudience;
            if (!string.IsNullOrWhiteSpace(profile.Locale)) values[AiEditorialPackageInputs.Locale] = profile.Locale;
        }

        return JsonSerializer.Serialize(values);
    }

    private static IdempotentOutcome<AiProposalStatusServiceModel> Refuse(string code, string message) =>
        new(Failure(code, message), Replayed: false);

    private static OperationResult<AiProposalStatusServiceModel> Failure(string code, string message) =>
        OperationResult<AiProposalStatusServiceModel>.Failure(new OperationError(code, message, new Dictionary<string, string[]>()));
}
