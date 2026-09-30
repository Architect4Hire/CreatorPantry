namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// A recipe's accepted revision of one package kind as the data layer finds it, before anyone decides whether
/// it is usable.
/// </summary>
/// <param name="Status">The proposal's status. A stale acceptance is <c>NeedsReview</c>.</param>
/// <param name="PinnedVersionId">The recipe version the accepted revision was written against.</param>
/// <param name="Content">The revision's stored document, verbatim.</param>
public sealed record AcceptedContentRecord(
    Guid RevisionId,
    int RevisionNumber,
    ContentProposalStatus Status,
    Guid PinnedVersionId,
    string Content);

/// <summary>
/// The currency rule every accepted package shares. A later rejection leaves the earlier acceptance in force,
/// so only a stale acceptance — marked for review, or written against a different version — is withheld. The
/// pin, not the status, says which version the words were accepted for.
/// </summary>
internal static class AcceptedContentCurrency
{
    public static bool IsCurrent(AcceptedContentRecord accepted, Guid recipeVersionId) =>
        accepted.Status != ContentProposalStatus.NeedsReview && accepted.PinnedVersionId == recipeVersionId;
}

/// <summary>
/// An accepted SEO revision, read for a structured-data export. Recommendations only: nothing here is a
/// keyword metric, and nothing is invented when the stored document lacks a part.
/// </summary>
/// <param name="RevisionNumber">The revision's own number, as its proposal's history lists it.</param>
/// <param name="MetaDescription">The accepted meta description, or <c>null</c>.</param>
/// <param name="KeyPhrases">The accepted key phrases, in the order accepted.</param>
/// <param name="IsCurrent">
/// Whether the revision was written against the recipe version asked about and has not since been marked
/// for review. A caller must not use the words of a revision that is not current.
/// </param>
public sealed record AcceptedSeoServiceModel(
    int RevisionNumber,
    string? MetaDescription,
    IReadOnlyList<string> KeyPhrases,
    bool IsCurrent);
