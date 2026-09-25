using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>What a client may ask for. Deliberately four fields, three of which are ids or enums.</summary>
/// <remarks>
/// <para>
/// <strong>What is absent is the contract.</strong> There is no prompt, no template id or path, no model, no
/// provider parameter, no tool list, and no workspace — a client cannot express any of those because the type
/// has nowhere to put them. <c>RequestAiProposalViewModelTests</c> asserts this property list, so adding one
/// is a visible change rather than a quiet widening.
/// </para>
/// <para>
/// <see cref="Task"/> is a discriminator the server resolves through <see cref="AiTaskCatalog"/>, not a
/// free-text instruction. The recipe comes from the route.
/// </para>
/// </remarks>
public sealed class RequestAiProposalViewModel
{
    /// <summary>An allow-listed task discriminator, e.g. <c>diagnostic</c>.</summary>
    public string? Task { get; set; }

    /// <summary>Which parts of the recipe the proposal may touch.</summary>
    public AiOperationScope Scope { get; set; }

    /// <summary>
    /// The exact recipe version to work from. Required: a proposal computed against "latest" cannot be
    /// checked for staleness later, which is the whole basis of accepting one safely.
    /// </summary>
    public Guid SourceVersionId { get; set; }
}

public sealed class RequestAiProposalViewModelValidator : AbstractValidator<RequestAiProposalViewModel>
{
    public RequestAiProposalViewModelValidator()
    {
        RuleFor(model => model.Task)
            .NotEmpty().WithMessage("Name the task to run.")
            .Must(task => AiTaskCatalog.Resolve(task) is not null)
            .WithMessage($"Unknown task. Known tasks: {string.Join(", ", AiTaskCatalog.Known)}.");

        RuleFor(model => model.Scope)
            .NotEqual(AiOperationScope.Unspecified)
            .WithMessage("Say which parts of the recipe the proposal may change.");

        RuleFor(model => model.SourceVersionId)
            .NotEmpty().WithMessage("Name the recipe version to work from.");
    }
}

/// <summary>Where a requested proposal has got to, and the proposal itself once there is one.</summary>
/// <param name="AiProposalRequestId">
/// The id returned when the request was accepted, and the one to poll. It is the operation's id: a request
/// exists from the moment it is accepted, and the proposal it eventually produces carries its own identity
/// inside <see cref="Proposal"/> for acting on.
/// </param>
public sealed record AiProposalStatusServiceModel(
    Guid AiProposalRequestId,
    AiOperationStatus Status,
    AiTaskType TaskType,
    AiOperationScope Scope,
    Guid? SourceVersionId,
    DateTimeOffset RequestedAt,
    DateTimeOffset StatusChangedAt,
    AiFailureCategory? FailureCategory,
    AiProposalDetailServiceModel? Proposal);

/// <param name="ProposalId">
/// The proposal's own identity, for correlating what a creator reviewed with the version it wrote.
/// </param>
/// <remarks>
/// <para>
/// <strong>Not the id a disposition is addressed to.</strong> That is the request id, the same one this reply
/// arrived under — an operation has at most one proposal, so the request identifies it unambiguously, and one
/// route segment meaning two different identities would be a trap rather than a nicety.
/// </para>
/// <para>
/// Carries provenance — which template, which body, which model — because a creator reviewing generated
/// content is entitled to know what produced it. It carries no execution telemetry: latency, tokens and cost
/// are operational, and putting them in a creator-facing reply would be answering a question nobody asked.
/// </para>
/// </remarks>
public sealed record AiProposalDetailServiceModel(
    Guid ProposalId,
    string OutputSchemaVersion,
    string PromptTemplateId,
    string PromptTemplateVersion,
    string PromptTemplateBodyChecksum,
    string ProviderName,
    string ModelName,
    DateTimeOffset CreatedAt,
    IReadOnlyList<AiProposedChangeServiceModel> Changes,
    IReadOnlyList<AiProposalWarningServiceModel> Warnings);

/// <param name="BeforeValue">Computed by the server from the pinned version, never supplied by the model.</param>
public sealed record AiProposedChangeServiceModel(
    Guid ChangeId,
    AiChangeKind ChangeKind,
    AiChangeTargetKind TargetKind,
    Guid? TargetId,
    string? FieldName,
    string? BeforeValue,
    string? AfterValue,
    int? ProposedPosition,
    AiChangeDisposition Disposition);

public sealed record AiProposalWarningServiceModel(
    AiWarningKind Kind,
    string Message,
    Guid? ChangeId);

/// <summary>
/// Stable error codes this seam introduces. Renaming one is a breaking API change.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The dotted suffix is load-bearing, not styling.</strong> <c>ProblemResults.StatusFor</c> reads the
/// segment after the last dot to choose a status, so <c>ai.recipe.not_found</c> answers 404 and
/// <c>ai_recipe_not_found</c> would answer 400. These codes were first written with underscores throughout,
/// which meant the route advertised 404 for an unknown recipe and returned 400 — a client following the
/// contract would have treated a missing recipe as its own bad request.
/// </para>
/// <para>
/// The shape mirrors the recipe module's: module, resource, condition.
/// </para>
/// </remarks>
public static class AiProposalErrors
{
    /// <summary>A real task, switched off for this deployment. Retrying will not help; enabling it will.</summary>
    public const string TaskNotEnabled = "ai.task.not_enabled";

    /// <summary>No such task. A different answer from <see cref="TaskNotEnabled"/>, and 400 either way.</summary>
    public const string TaskUnknown = "ai.task.unknown";

    /// <summary>The pinned source version is not the recipe's current version.</summary>
    public const string SourceVersionInvalid = "ai.sourceVersion.invalid_request";

    /// <summary>
    /// Unknown recipe, or one belonging to another workspace: deliberately indistinguishable (tenancy.md).
    /// </summary>
    public const string RecipeNotFound = "ai.recipe.not_found";

    /// <summary>No such proposal request on this recipe, in this workspace.</summary>
    public const string RequestNotFound = "ai.request.not_found";

    /// <summary>
    /// The caller belongs to the workspace but their role does not permit deciding a proposal's fate.
    /// </summary>
    /// <remarks>
    /// Safe to disclose, for the reason <c>RecipeErrorCodes.RecipeForbidden</c> gives: someone already known to
    /// belong here learns nothing about what exists by being told their role is too low.
    /// </remarks>
    public const string DispositionForbidden = "ai.disposition.forbidden";

    /// <summary>
    /// The request exists but has produced no proposal to decide about — it is still queued, still running, or
    /// it failed. Not the same as a missing request, because the remedy is to wait or to look at why it failed.
    /// </summary>
    public const string ProposalNotFound = "ai.proposal.not_found";

    /// <summary>
    /// The proposal has already been decided, or never reached a state where it could be. Terminal states do
    /// not reopen, so this is 409 rather than a validation failure: the request is well formed and would have
    /// been accepted a moment earlier.
    /// </summary>
    public const string ProposalDecided = "ai.proposal.conflict";

    /// <summary>
    /// The confirmation named a change this proposal does not contain, or an accept-all did not name every
    /// change. Nothing is written — an invalid selection must not apply the part of itself that was valid.
    /// </summary>
    public const string SelectionInvalid = "ai.selection.invalid_request";
}
