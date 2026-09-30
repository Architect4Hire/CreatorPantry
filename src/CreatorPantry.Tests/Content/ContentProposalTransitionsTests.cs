using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Content;

public sealed class ContentProposalTransitionsTests
{
    [Fact]
    public void Accepting_and_rejecting_are_available_only_from_Proposed()
    {
        Assert.NotNull(ContentProposalTransitions.Find(ContentProposalStatus.Proposed, ContentProposalStatus.Accepted));
        Assert.NotNull(ContentProposalTransitions.Find(ContentProposalStatus.Proposed, ContentProposalStatus.Rejected));

        foreach (var from in new[] { ContentProposalStatus.Accepted, ContentProposalStatus.Rejected, ContentProposalStatus.NeedsReview })
        {
            Assert.Null(ContentProposalTransitions.Find(from, ContentProposalStatus.Rejected));
        }

        Assert.Null(ContentProposalTransitions.Find(ContentProposalStatus.Rejected, ContentProposalStatus.Accepted));
    }

    [Fact]
    public void Staleness_is_applied_from_Accepted_only_and_by_the_system()
    {
        var rule = ContentProposalTransitions.Find(ContentProposalStatus.Accepted, ContentProposalStatus.NeedsReview);
        Assert.NotNull(rule);
        Assert.Null(rule.MinimumRole);

        Assert.Single(ContentProposalTransitions.All, r => r.To == ContentProposalStatus.NeedsReview);
    }

    [Fact]
    public void Reaffirming_writes_a_revision_and_accepts_it()
    {
        var rule = ContentProposalTransitions.Find(ContentProposalStatus.NeedsReview, ContentProposalStatus.Accepted);
        Assert.NotNull(rule);
        Assert.True(rule.WritesRevision);
        Assert.True(rule.SetsAcceptedRevision);
    }

    [Fact]
    public void An_unaccepted_proposal_cannot_be_marked_stale()
    {
        Assert.Null(ContentProposalTransitions.Find(ContentProposalStatus.Proposed, ContentProposalStatus.NeedsReview));
    }

    [Fact]
    public void Only_creation_starts_from_nothing()
    {
        Assert.Equal([ContentProposalStatus.Proposed], ContentProposalTransitions.From(null));
    }
}

public sealed class ContentSourcePinsTests
{
    private static readonly Guid Version = Guid.NewGuid();
    private static readonly Guid Brand = Guid.NewGuid();
    private static readonly Guid Voice = Guid.NewGuid();

    private static ContentSourcePins Pins(Guid? version = null, Guid? brand = null, Guid? voice = null, string checksum = "sha256:a") =>
        new(version ?? Version, brand ?? Brand, voice ?? Voice, "editorial", "1.0.0", checksum);

    [Fact]
    public void Identical_pins_are_not_stale() =>
        Assert.Equal(ContentStaleReasons.None, Pins().StaleAgainst(Pins()));

    [Fact]
    public void Each_moved_source_is_reported_and_several_combine()
    {
        Assert.Equal(ContentStaleReasons.RecipeChanged, Pins().StaleAgainst(Pins(version: Guid.NewGuid())));
        Assert.Equal(ContentStaleReasons.BrandChanged, Pins().StaleAgainst(Pins(brand: Guid.NewGuid())));
        Assert.Equal(ContentStaleReasons.VoiceChanged, Pins().StaleAgainst(Pins(voice: Guid.NewGuid())));
        Assert.Equal(ContentStaleReasons.TemplateChanged, Pins().StaleAgainst(Pins(checksum: "sha256:b")));
        Assert.Equal(
            ContentStaleReasons.RecipeChanged | ContentStaleReasons.VoiceChanged,
            Pins().StaleAgainst(Pins(version: Guid.NewGuid(), voice: Guid.NewGuid())));
    }

    [Fact]
    public void Gaining_a_style_guide_after_writing_without_one_is_a_change()
    {
        var without = Pins() with { BrandStyleGuideVersionId = null };
        Assert.Equal(ContentStaleReasons.VoiceChanged, without.StaleAgainst(Pins()));
        Assert.Equal(ContentStaleReasons.None, without.StaleAgainst(without));
    }
}
