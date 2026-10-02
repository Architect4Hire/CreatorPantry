using CreatorPantry.Domain.Modules.Ai.Data.Entities;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Turns an assembled <see cref="BrandContextPackage"/> into the rows recorded beside a proposal (11A.20).
/// </summary>
/// <remarks>
/// <para>
/// <strong>One translation, used by every writing handler.</strong> Four capabilities ground in brand context and
/// each records the same facts; a handler writing its own rows is how two of them end up recording different
/// subsets, and then no reader can rely on any of it.
/// </para>
/// <para>
/// <strong>It records, it does not decide.</strong> Nothing here consults the guide, re-reads a profile, or
/// chooses a version — the assembler did all of that and the package is its answer. The checksum is copied
/// verbatim for the same reason: recomputing it here would mean two implementations of the one value that proves
/// which words a generation saw.
/// </para>
/// </remarks>
public static class BrandContextProvenance
{
    /// <summary>
    /// The provenance rows for one assembled package.
    /// </summary>
    /// <param name="workspaceId">The resolved workspace. Every row carries it, including the interior ones.</param>
    /// <param name="package">The package the generation was grounded on, empty or not.</param>
    /// <remarks>
    /// An empty package still produces a row. A workspace with no profile and no active guide is a fact worth
    /// keeping — it is what later explains a piece that reads in no particular voice — and the omissions the
    /// package carries have already been said out loud as warnings by <see cref="BrandContextNotices"/>.
    /// </remarks>
    public static AiProposalBrandContext Record(Guid workspaceId, BrandContextPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var record = new AiProposalBrandContext
        {
            WorkspaceId = workspaceId,
            ChannelKey = package.ChannelKey,
            Audience = package.Audience,
            AudienceOrigin = package.AudienceOrigin,
            BrandProfileRevision = package.Profile?.Revision,
            BrandGuideId = package.GuideId,
            BrandGuideVersionId = package.GuideVersionId,
            BrandGuideVersionNumber = package.GuideVersionNumber,

            // Never true without a guide: the check constraint refuses that row, and the package cannot produce
            // it, but the pairing is stated here too so the two cannot drift apart silently.
            GuideWasActiveVersion = package.GuideId is not null && package.GuideIsActiveVersion,
            Checksum = package.Checksum,
            EstimatedTokens = package.EstimatedTokens,
            GuidanceSectionCount = package.Guidance.Count,
            RuleCount = package.Rules.Count,
            AssembledAt = package.AssembledAt,
        };

        // The prompt order, not the document order. SortOrder is what the prompt carried and Ordinal is where the
        // passage sits in its own document; a reader reconstructing what the model was shown needs the first, and
        // one going back to the source needs the second.
        for (var index = 0; index < package.Excerpts.Count; index++)
        {
            var excerpt = package.Excerpts[index];

            record.Sources.Add(new AiProposalBrandSource
            {
                WorkspaceId = workspaceId,
                BrandSourceDocumentId = excerpt.DocumentId,
                DocumentVersionNumber = excerpt.VersionNumber,
                BrandSourcePassageId = excerpt.PassageId,
                Ordinal = excerpt.Ordinal,
                SortOrder = index,
            });
        }

        return record;
    }
}
