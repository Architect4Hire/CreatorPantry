using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>
/// The boundary for a long-form draft and its plan (LF.2.2): what the draft routes call, and what the AI
/// module calls to start a draft and to land a generated plan on it.
/// </summary>
/// <remarks>
/// <strong>There is no workspace parameter anywhere.</strong> The workspace is the resolved context's — on a
/// route, the one the caller's membership was checked against; in the worker, the one the operation was
/// validated against before its handler ran.
/// </remarks>
public interface ILongFormDraftFacade
{
    /// <inheritdoc cref="ILongFormDraftBusiness.EnsureStartedAsync"/>
    Task<OperationResult<LongFormDraftServiceModel>> EnsureStartedAsync(
        Guid draftId, Guid creativeContextId, LongFormKind kind, CancellationToken cancellationToken);

    Task<OperationResult<LongFormDraftServiceModel>> GetAsync(Guid draftId, CancellationToken cancellationToken);

    Task<OperationResult<CursorPageServiceModel<LongFormDraftSummaryServiceModel>>> ListRecentAsync(
        LongFormDraftListViewModel model, CancellationToken cancellationToken);

    /// <inheritdoc cref="ILongFormDraftBusiness.RecordGeneratedPlanAsync"/>
    Task<OperationResult<LongFormDraftServiceModel>> RecordGeneratedPlanAsync(
        LongFormGeneratedPlanInput input, CancellationToken cancellationToken);

    /// <inheritdoc cref="ILongFormDraftBusiness.PatchPlanAsync"/>
    Task<OperationResult<LongFormDraftServiceModel>> PatchPlanAsync(
        string actorUserId, Guid draftId, PatchLongFormPlanViewModel model, CancellationToken cancellationToken);

    /// <inheritdoc cref="ILongFormDraftBusiness.ApprovePlanAsync"/>
    Task<OperationResult<LongFormDraftServiceModel>> ApprovePlanAsync(
        string actorUserId, Guid draftId, ApproveLongFormPlanViewModel model, CancellationToken cancellationToken);

    /// <inheritdoc cref="ILongFormDraftBusiness.ReorderItemsAsync"/>
    Task<OperationResult<LongFormDraftServiceModel>> ReorderItemsAsync(
        string actorUserId, Guid draftId, ReorderLongFormItemsViewModel model, CancellationToken cancellationToken);

    /// <inheritdoc cref="ILongFormDraftBusiness.PutFramingAsync"/>
    Task<OperationResult<LongFormDraftServiceModel>> PutFramingAsync(
        string actorUserId, Guid draftId, PutLongFormFramingViewModel model, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILongFormDraftFacade"/>
internal sealed class LongFormDraftFacade(
    ILongFormDraftBusiness business,
    IWorkspaceContext workspace,
    IValidator<PatchLongFormPlanViewModel> patchValidator,
    IValidator<ApproveLongFormPlanViewModel> approveValidator,
    IValidator<LongFormDraftListViewModel> listValidator,
    IValidator<ReorderLongFormItemsViewModel> reorderValidator,
    IValidator<PutLongFormFramingViewModel> framingValidator) : ILongFormDraftFacade
{
    public Task<OperationResult<LongFormDraftServiceModel>> EnsureStartedAsync(
        Guid draftId, Guid creativeContextId, LongFormKind kind, CancellationToken cancellationToken) =>
        business.EnsureStartedAsync(draftId, creativeContextId, kind, cancellationToken);

    public Task<OperationResult<LongFormDraftServiceModel>> GetAsync(Guid draftId, CancellationToken cancellationToken) =>
        business.GetAsync(draftId, cancellationToken);

    public async Task<OperationResult<CursorPageServiceModel<LongFormDraftSummaryServiceModel>>> ListRecentAsync(
        LongFormDraftListViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await listValidator.ValidateAsync(model, cancellationToken);

        // The workspace comes from the resolved context, never from the model — which has no field for it. It
        // is bound into the cursor's scope with the filters, so a cursor cannot be replayed across workspaces
        // or across two differently filtered lists.
        var scope = LongFormDraftListCriteria.ScopeFor(workspace.WorkspaceId, model.Kind, model.CreativeContextId);

        CreativeContextListPosition? position = null;

        if (!validation.IsValid
            || !ReferenceCursor.TryResolve(model.Cursor, scope, out var cursor)
            || (cursor is not null && !CreativeContextListPosition.TryCreate(cursor, out position)))
        {
            return OperationResult<CursorPageServiceModel<LongFormDraftSummaryServiceModel>>.Failure(
                OperationError.Validation(
                    ContentErrorCodes.LongFormCursorInvalid,
                    "This list could not be read as asked. Start again without a cursor.",
                    validation.IsValid
                        ? [("cursor", "Start the list again without a cursor.")]
                        : validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
        }

        return OperationResult<CursorPageServiceModel<LongFormDraftSummaryServiceModel>>.Success(
            await business.ListRecentAsync(
                new LongFormDraftListCriteria(scope, model.Kind, model.CreativeContextId, position, model.Limit),
                cancellationToken));
    }

    public Task<OperationResult<LongFormDraftServiceModel>> RecordGeneratedPlanAsync(
        LongFormGeneratedPlanInput input, CancellationToken cancellationToken) =>
        business.RecordGeneratedPlanAsync(input, cancellationToken);

    public async Task<OperationResult<LongFormDraftServiceModel>> PatchPlanAsync(
        string actorUserId, Guid draftId, PatchLongFormPlanViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await patchValidator.ValidateAsync(model, cancellationToken);

        return validation.IsValid
            ? await business.PatchPlanAsync(actorUserId, draftId, model, cancellationToken)
            : Invalid(validation);
    }

    public async Task<OperationResult<LongFormDraftServiceModel>> ApprovePlanAsync(
        string actorUserId, Guid draftId, ApproveLongFormPlanViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await approveValidator.ValidateAsync(model, cancellationToken);

        return validation.IsValid
            ? await business.ApprovePlanAsync(actorUserId, draftId, model, cancellationToken)
            : Invalid(validation);
    }

    public async Task<OperationResult<LongFormDraftServiceModel>> ReorderItemsAsync(
        string actorUserId, Guid draftId, ReorderLongFormItemsViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await reorderValidator.ValidateAsync(model, cancellationToken);

        return validation.IsValid
            ? await business.ReorderItemsAsync(actorUserId, draftId, model, cancellationToken)
            : Invalid(validation, ContentErrorCodes.LongFormItemOrderInvalid);
    }

    public async Task<OperationResult<LongFormDraftServiceModel>> PutFramingAsync(
        string actorUserId, Guid draftId, PutLongFormFramingViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await framingValidator.ValidateAsync(model, cancellationToken);

        return validation.IsValid
            ? await business.PutFramingAsync(actorUserId, draftId, model, cancellationToken)
            : Invalid(validation, ContentErrorCodes.LongFormFramingInvalid);
    }

    private static OperationResult<LongFormDraftServiceModel> Invalid(
        FluentValidation.Results.ValidationResult validation, string code = ContentErrorCodes.LongFormPlanInvalid) =>
        OperationResult<LongFormDraftServiceModel>.Failure(OperationError.Validation(
            code,
            "The request is not valid.",
            validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
}
