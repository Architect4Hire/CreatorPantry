using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>The only thing a creator names: which existing proposal to explain.</summary>
public sealed class RequestProposalExplanationViewModel
{
    /// <summary>The request id of an already-completed proposal — the same id its own status route returned.</summary>
    public Guid SourceRequestId { get; set; }
}

public sealed class RequestProposalExplanationViewModelValidator
    : AbstractValidator<RequestProposalExplanationViewModel>
{
    public RequestProposalExplanationViewModelValidator()
    {
        RuleFor(model => model.SourceRequestId).NotEmpty();
    }
}

public static class AiProposalExplanationRequestErrors
{
    public const string RequestInvalid = "ai.proposalExplanation.invalid_request";

    public const string TaskNotEnabled = "ai.proposalExplanation.not_enabled";

    public const string RequestNotFound = "ai.proposalExplanationRequest.not_found";

    /// <summary>
    /// One code for "does not exist", "belongs to another workspace", "has no proposal yet", and "is itself an
    /// explanation". None should disclose which is true (tenancy.md).
    /// </summary>
    public const string SourceNotReady = "ai.proposalExplanation.source_not_ready";
}

/// <summary>The one declared field this task reads from <c>AiOperation.TaskInputsJson</c>.</summary>
internal static class AiProposalExplanationInputs
{
    public const string SourceRequestId = "sourceRequestId";
}
