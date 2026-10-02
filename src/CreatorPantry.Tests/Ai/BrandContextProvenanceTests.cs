using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// What a generation records about the brand context it was grounded on (11A.20): the guide version, the profile
/// revision, the cited passages, and the checksum that proves which words travelled.
/// </summary>
public sealed class BrandContextProvenanceTests
{
    private static readonly Guid Workspace = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid GuideId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GuideVersionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Assembled = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_grounded_package_records_the_guide_version_and_the_profile_revision()
    {
        var record = BrandContextProvenance.Record(Workspace, Package());

        Assert.Equal(Workspace, record.WorkspaceId);
        Assert.Equal(GuideId, record.BrandGuideId);
        Assert.Equal(GuideVersionId, record.BrandGuideVersionId);
        Assert.Equal(4, record.BrandGuideVersionNumber);
        Assert.True(record.GuideWasActiveVersion);
        Assert.Equal(7, record.BrandProfileRevision);
        Assert.Equal(Assembled, record.AssembledAt);
    }

    /// <summary>
    /// Copied verbatim, never recomputed. A second implementation of the one value that proves which words a
    /// generation saw is a second value that can disagree with the first.
    /// </summary>
    [Fact]
    public void The_checksum_is_the_one_the_assembler_computed()
    {
        var package = Package();

        Assert.Equal(package.Checksum, BrandContextProvenance.Record(Workspace, package).Checksum);
    }

    /// <summary>
    /// The bodies and rule texts are not stored, so the counts are the only record of how much travelled. The
    /// excerpt count is deliberately absent: the rows are the count, and two numbers for one fact can disagree.
    /// </summary>
    [Fact]
    public void Content_is_counted_rather_than_copied()
    {
        var record = BrandContextProvenance.Record(Workspace, Package());

        Assert.Equal(2, record.GuidanceSectionCount);
        Assert.Equal(1, record.RuleCount);
        Assert.Equal(2, record.Sources.Count);
        Assert.Equal(410, record.EstimatedTokens);
    }

    /// <summary>
    /// SortOrder is the order the prompt carried; Ordinal is where the passage sits in its own document. A reader
    /// reconstructing what the model saw needs the first and one going back to the source needs the second, so
    /// neither is derived from the other.
    /// </summary>
    [Fact]
    public void A_cited_passage_records_both_its_prompt_position_and_its_place_in_its_document()
    {
        var record = BrandContextProvenance.Record(Workspace, Package());

        var sources = record.Sources.OrderBy(source => source.SortOrder).ToList();

        Assert.Equal(0, sources[0].SortOrder);
        Assert.Equal(12, sources[0].Ordinal);
        Assert.Equal(3, sources[0].DocumentVersionNumber);
        Assert.Equal(1, sources[1].SortOrder);
        Assert.Equal(4, sources[1].Ordinal);
        Assert.All(sources, source => Assert.Equal(Workspace, source.WorkspaceId));
    }

    /// <summary>
    /// A workspace with no profile and no active guide still produces a row. It is what later explains a piece
    /// that reads in no particular voice — and the alternative, recording nothing, is indistinguishable from a
    /// creator who turned brand voice off.
    /// </summary>
    [Fact]
    public void A_package_that_found_nothing_still_records_a_row()
    {
        var record = BrandContextProvenance.Record(Workspace, Empty());

        Assert.Null(record.BrandGuideId);
        Assert.Null(record.BrandGuideVersionId);
        Assert.Null(record.BrandGuideVersionNumber);
        Assert.Null(record.BrandProfileRevision);
        Assert.Null(record.Audience);
        Assert.Null(record.AudienceOrigin);
        Assert.Empty(record.Sources);
        Assert.Equal(0, record.GuidanceSectionCount);
        Assert.NotEqual(string.Empty, record.Checksum);
    }

    /// <summary>
    /// No guide means no claim about which version was active, which is a different fact from "an older version
    /// was used". The check constraint refuses the row too; this is the same pairing stated in code, so the two
    /// cannot drift apart.
    /// </summary>
    [Fact]
    public void Without_a_guide_nothing_claims_a_version_was_active()
    {
        var package = Empty() with { GuideIsActiveVersion = true };

        Assert.False(BrandContextProvenance.Record(Workspace, package).GuideWasActiveVersion);
    }

    /// <summary>
    /// A pinned version that is no longer active is recorded as the version it was, with the fact that it was not
    /// active beside it. Recording the active one instead would make the provenance describe a generation that
    /// never happened.
    /// </summary>
    [Fact]
    public void A_superseded_version_is_recorded_as_the_version_that_was_used()
    {
        var package = Package() with { GuideIsActiveVersion = false, GuideVersionNumber = 2 };

        var record = BrandContextProvenance.Record(Workspace, package);

        Assert.Equal(2, record.BrandGuideVersionNumber);
        Assert.False(record.GuideWasActiveVersion);
    }

    /// <summary>
    /// An audience and where it came from travel together, because a creator reading this afterwards cannot tell
    /// an audience they typed from one their brand profile supplied.
    /// </summary>
    [Fact]
    public void The_audience_records_where_it_came_from()
    {
        var record = BrandContextProvenance.Record(Workspace, Package());

        Assert.Equal("weeknight cooks", record.Audience);
        Assert.Equal(BrandContextOrigin.Request, record.AudienceOrigin);
    }

    private static BrandContextPackage Package() => new(
        AiTaskType.EditorialPackage,
        ChannelKey: null,
        "weeknight cooks",
        BrandContextOrigin.Request,
        new BrandContextProfile("Sam's Kitchen", "Fast food, slowly explained", "home cooks", "en-GB", ["instagram"], 7),
        GuideId,
        GuideVersionId,
        4,
        GuideIsActiveVersion: true,
        [
            new BrandContextGuidance(BrandStyleGuideSectionKey.Voice, null, "Warm, never breezy.", BrandContextOrigin.GuideSection),
            new BrandContextGuidance(BrandStyleGuideSectionKey.Tone, null, "Plain words.", BrandContextOrigin.GuideSection),
        ],
        [new BrandContextRule(BrandStyleGuideRuleKind.Dont, "Never say moist.")],
        [
            new BrandContextExcerpt(
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                3,
                12,
                "The trick is to salt it the night before."),
            new BrandContextExcerpt(
                Guid.Parse("55555555-5555-5555-5555-555555555555"),
                Guid.Parse("66666666-6666-6666-6666-666666666666"),
                1,
                4,
                "We never bother with a stand mixer."),
        ],
        [],
        [],
        410,
        "sha256:0000000000000000000000000000000000000000000000000000000000000000",
        Assembled);

    private static BrandContextPackage Empty() => new(
        AiTaskType.SeoPackage,
        ChannelKey: null,
        Audience: null,
        AudienceOrigin: null,
        Profile: null,
        GuideId: null,
        GuideVersionId: null,
        GuideVersionNumber: null,
        GuideIsActiveVersion: false,
        [],
        [],
        [],
        [BrandContextConflict.GuideVersionUnapproved],
        [BrandContextOmission.NoActiveGuide, BrandContextOmission.NoBrandProfile],
        0,
        "sha256:1111111111111111111111111111111111111111111111111111111111111111",
        Assembled);
}
