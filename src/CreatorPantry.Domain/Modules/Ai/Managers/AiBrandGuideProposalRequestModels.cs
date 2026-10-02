using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What a client sends to ask for a brand-guide proposal about one of the workspace's style guides (11A.17).
/// </summary>
/// <remarks>
/// <para>
/// There is no scope, model, prompt, template or workspace field: the scope is fixed server-side, the model is
/// the deployment's, and the workspace is the route's.
/// </para>
/// <para>
/// <strong>No questionnaire answers either.</strong> The creator's answers already live on the guide, written
/// when it was created or last edited. Re-sending them here would be a second contract for the same text, and a
/// second chance for the two to disagree about what the creator said.
/// </para>
/// <para>
/// <strong>Documents, not document versions.</strong> A creator selects a document; the server pins its current
/// version number into the operation, which is the version the proposal is then explained by. Only a current
/// version has indexed passages, so naming an older one would ask to be grounded in text the document has moved
/// past.
/// </para>
/// </remarks>
public sealed class RequestBrandGuideProposalViewModel
{
    /// <summary>The style guide to propose guidance for. Its working version supplies the creator's answers.</summary>
    public Guid GuideId { get; set; }

    /// <summary>
    /// The dimensions to propose, by name (<c>voice</c>, <c>tone</c>, <c>tenor</c>, <c>style</c>,
    /// <c>language</c>, <c>channel</c>, <c>blog</c>, <c>social</c>, <c>visual</c>). Omitted or empty means every
    /// dimension except <c>channel</c>, which needs channels named.
    /// </summary>
    public List<string>? Dimensions { get; set; }

    /// <summary>
    /// The channels to write guidance for. Required when <c>channel</c> is asked for, and refused otherwise.
    /// </summary>
    /// <remarks>
    /// Keys from the content channel catalogue. Checked against it server-side, so a key the product does not
    /// know is refused rather than passed to a model that would invent a channel's conventions.
    /// </remarks>
    public List<string>? ChannelKeys { get; set; }

    /// <summary>
    /// The source documents to ground the proposal in. Optional: a proposal from the creator's own answers alone
    /// is legitimate, and says so in its findings.
    /// </summary>
    public List<Guid>? SourceDocumentIds { get; set; }
}

public sealed class RequestBrandGuideProposalViewModelValidator : AbstractValidator<RequestBrandGuideProposalViewModel>
{
    public RequestBrandGuideProposalViewModelValidator()
    {
        RuleFor(model => model.GuideId)
            .NotEmpty().WithMessage("Name the brand style guide to propose guidance for.");

        RuleFor(model => model.Dimensions)
            .Must(dimensions => dimensions is null
                || dimensions.All(dimension => AiBrandGuideDimensionCatalog.Parse(dimension) is not null))
            .WithMessage($"Each dimension must be one of {string.Join(", ", AiBrandGuideDimensionCatalog.Names)}.")
            .Must(dimensions => dimensions is null
                || dimensions.Select(AiBrandGuideDimensionCatalog.Parse).Distinct().Count() == dimensions.Count)
            .WithMessage("Name each dimension once.");

        // Shape only: whether the keys exist is a fact about the channel catalogue, which Business consults.
        RuleFor(model => model.ChannelKeys)
            .Must(keys => keys is null || keys.All(key => !string.IsNullOrWhiteSpace(key)))
            .WithMessage("A channel key cannot be blank.")
            .Must(keys => keys is null || keys.Distinct(StringComparer.Ordinal).Count() == keys.Count)
            .WithMessage("Name each channel once.");

        RuleFor(model => model.SourceDocumentIds)
            .Must(ids => ids is null || ids.All(id => id != Guid.Empty))
            .WithMessage("A source document id cannot be empty.")
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("Name each source document once.")
            .Must(ids => ids is null || ids.Count <= AiPolicy.BrandGuideMaxSourceDocuments)
            .WithMessage($"At most {AiPolicy.BrandGuideMaxSourceDocuments} source documents.");
    }
}

public static class AiBrandGuideProposalRequestErrors
{
    public const string RequestInvalid = "ai.brandGuideProposal.invalid_request";

    public const string TaskNotEnabled = "ai.brandGuideProposal.not_enabled";

    /// <summary>
    /// No such style guide in the resolved workspace. Maps to 404.
    /// </summary>
    /// <remarks>
    /// One code for an id never issued and for another workspace's guide, so a request cannot be used to learn
    /// that a guide exists somewhere the caller cannot see it (tenancy.md).
    /// </remarks>
    public const string GuideNotFound = "ai.brandGuideProposal.guide.not_found";

    /// <summary>
    /// A selected source document cannot be used. Maps to 422.
    /// </summary>
    /// <remarks>
    /// One code for every way a selection can fail to resolve — never issued, another workspace's, removed — so
    /// a caller cannot use the refusal to learn that a document exists somewhere it cannot see it. A document
    /// that resolves but has no indexed text yet is <em>not</em> this: that is reported in the proposal's own
    /// findings, because it is a fact about the evidence rather than about the request.
    /// </remarks>
    public const string SourceUnprocessable = "ai.brandGuideProposalSource.unprocessable";

    /// <summary>A channel was asked for without naming one, or a key the catalogue does not know. Maps to 400.</summary>
    public const string ChannelInvalid = "ai.brandGuideProposalChannel.invalid_request";

    public const string RequestNotFound = "ai.brandGuideProposalRequest.not_found";
}
