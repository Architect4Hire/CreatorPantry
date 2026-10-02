using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// What a creator is told about the brand context a generation was grounded on (11A.20): a stale guide said out
/// loud rather than refused, an unapproved version named, and nothing the assembler detects left unsaid.
/// </summary>
public sealed class BrandContextNoticeTests
{
    private static readonly Guid GuideId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GuideVersionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Assembled = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The restriction, as a test: a stale guide is never selected silently. The generation proceeds on the
    /// version the creator asked for, and the warning says which it was.
    /// </summary>
    [Fact]
    public void A_superseded_guide_version_is_said_out_loud_with_its_number()
    {
        var warnings = BrandContextNotices.For(
            Package(conflicts: [BrandContextConflict.GuideVersionNotActive], versionNumber: 2));

        var warning = Assert.Single(warnings);

        Assert.Equal(AiWarningKind.Limitation, warning.Kind);
        Assert.Contains(BrandContextNotices.GuideVersionNotActive, warning.Message, StringComparison.Ordinal);
        Assert.Contains("version 2", warning.Message, StringComparison.Ordinal);
        Assert.Null(warning.ChangeIndex);
    }

    [Fact]
    public void An_unapproved_guide_version_is_a_limitation_of_its_own()
    {
        var warnings = BrandContextNotices.For(
            Package(conflicts: [BrandContextConflict.GuideVersionUnapproved]));

        var warning = Assert.Single(warnings);

        Assert.Equal(AiWarningKind.Limitation, warning.Kind);
        Assert.Contains(BrandContextNotices.GuideVersionUnapproved, warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A workspace with nothing set up gets told so, because a piece written in no particular voice otherwise
    /// looks like a model that ignored the brand.
    /// </summary>
    [Fact]
    public void An_empty_workspace_is_told_what_was_missing()
    {
        var warnings = BrandContextNotices.For(Package(
            omissions: [BrandContextOmission.NoActiveGuide, BrandContextOmission.NoBrandProfile]));

        Assert.Equal(2, warnings.Count);
        Assert.Contains(BrandContextNotices.NoActiveGuide, warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains(BrandContextNotices.NoBrandProfile, warnings[1].Message, StringComparison.Ordinal);
        Assert.All(warnings, warning => Assert.Equal(AiWarningKind.Limitation, warning.Kind));
    }

    /// <summary>
    /// The budget is a product decision the creator can act on — shorten a section, or name fewer documents — so
    /// the two ways of exceeding it stay separate messages rather than one about size.
    /// </summary>
    [Fact]
    public void The_two_budget_omissions_say_different_things()
    {
        var warnings = BrandContextNotices.For(Package(
            omissions: [BrandContextOmission.ExcerptOverBudget, BrandContextOmission.GuideSectionOverBudget]));

        Assert.Equal(2, warnings.Count);
        Assert.NotEqual(warnings[0].Message, warnings[1].Message);
    }

    /// <summary>
    /// The creator typed the audience, so being told it was used is noise. It is recorded in provenance instead,
    /// with where it came from.
    /// </summary>
    [Fact]
    public void An_audience_the_creator_chose_is_not_warned_about()
    {
        var warnings = BrandContextNotices.For(
            Package(conflicts: [BrandContextConflict.AudienceOverridesProfile]));

        Assert.Empty(warnings);
    }

    [Fact]
    public void A_package_with_nothing_to_report_produces_nothing()
    {
        Assert.Empty(BrandContextNotices.For(Package()));
    }

    /// <summary>
    /// Conflicts before omissions, each group in enum order, so two generations grounded the same way read the
    /// same way to the creator reviewing them.
    /// </summary>
    [Fact]
    public void Conflicts_come_before_omissions()
    {
        var warnings = BrandContextNotices.For(Package(
            conflicts: [BrandContextConflict.GuideVersionNotActive],
            omissions: [BrandContextOmission.NoSourceExcerpts]));

        Assert.Equal(2, warnings.Count);
        Assert.Contains(BrandContextNotices.GuideVersionNotActive, warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains(BrandContextNotices.NoSourceExcerpts, warnings[1].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every member the assembler can produce either has a notice or is listed as deliberately silent. The
    /// alternative is a conflict the assembler detects and nobody ever sees, which is the failure this whole
    /// translator exists to prevent.
    /// </summary>
    [Fact]
    public void Every_conflict_is_either_surfaced_or_deliberately_silent()
    {
        foreach (var conflict in Enum.GetValues<BrandContextConflict>())
        {
            var warnings = BrandContextNotices.For(Package(conflicts: [conflict]));

            if (BrandContextNotices.SilentConflicts.Contains(conflict))
            {
                Assert.Empty(warnings);

                continue;
            }

            var warning = Assert.Single(warnings);

            Assert.NotEqual(AiWarningKind.Unspecified, warning.Kind);
            Assert.StartsWith("[brand_context.", warning.Message, StringComparison.Ordinal);
        }
    }

    /// <inheritdoc cref="Every_conflict_is_either_surfaced_or_deliberately_silent"/>
    [Fact]
    public void Every_omission_is_surfaced()
    {
        foreach (var omission in Enum.GetValues<BrandContextOmission>())
        {
            var warning = Assert.Single(BrandContextNotices.For(Package(omissions: [omission])));

            Assert.NotEqual(AiWarningKind.Unspecified, warning.Kind);
            Assert.StartsWith("[brand_context.", warning.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Every message fits the column it is stored in. A notice truncated on the way to the database would be a
    /// caution the creator reads half of.
    /// </summary>
    [Fact]
    public void Every_message_fits_a_stored_warning()
    {
        var all = Enum.GetValues<BrandContextConflict>()
            .SelectMany(conflict => BrandContextNotices.For(Package(conflicts: [conflict])))
            .Concat(Enum.GetValues<BrandContextOmission>()
                .SelectMany(omission => BrandContextNotices.For(Package(omissions: [omission]))));

        Assert.All(all, warning => Assert.True(warning.Message.Length <= AiPolicy.MessageMaxLength));
    }

    [Fact]
    public void An_image_task_reads_visual_wording_and_the_new_omission_has_a_notice()
    {
        var package = Package(omissions:
            [BrandContextOmission.NoActiveGuide, BrandContextOmission.NoVisualReferenceText])
            with { TaskType = AiTaskType.ImagePrompt };

        var warnings = BrandContextNotices.For(package);

        Assert.Contains(warnings, w => w.Message.Contains("visual direction", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Message.Contains(BrandContextNotices.NoVisualReferenceText, StringComparison.Ordinal));
        Assert.All(warnings, w => Assert.True(w.Message.Length <= AiPolicy.MessageMaxLength));
    }

    private static BrandContextPackage Package(
        IReadOnlyList<BrandContextConflict>? conflicts = null,
        IReadOnlyList<BrandContextOmission>? omissions = null,
        int versionNumber = 4) => new(
        AiTaskType.EditorialPackage,
        ChannelKey: null,
        Audience: null,
        AudienceOrigin: null,
        Profile: null,
        GuideId,
        GuideVersionId,
        versionNumber,
        GuideIsActiveVersion: false,
        [],
        [],
        [],
        conflicts ?? [],
        omissions ?? [],
        0,
        "sha256:0000000000000000000000000000000000000000000000000000000000000000",
        Assembled);
}
