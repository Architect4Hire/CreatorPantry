using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// 13.7's blocked-package rule, with no store: which pinned sources count as having moved, given where each
/// one stands now.
/// </summary>
public sealed class RecipePackageReviewTests
{
    private static readonly Guid Recipe = Guid.NewGuid();
    private static readonly Guid Pinned = Guid.NewGuid();
    private static readonly Guid Newer = Guid.NewGuid();
    private static readonly Guid EditorialRevision = Guid.NewGuid();
    private static readonly Guid PostRevision = Guid.NewGuid();

    private static EditorialPinRecord Editorial(
        ContentProposalStatus status = ContentProposalStatus.Accepted, Guid? accepted = null, Guid? writtenFor = null) =>
        new(EditorialRevision, Guid.NewGuid(), Recipe, ContentPackageKind.Editorial, status, accepted ?? EditorialRevision, writtenFor ?? Pinned);

    private static SocialPinRecord Post(
        ContentProposalStatus status = ContentProposalStatus.Accepted, Guid? accepted = null, Guid? writtenFor = null, bool noRecipe = false) =>
        new(PostRevision, Guid.NewGuid(), "instagram", Guid.NewGuid(), Guid.NewGuid(), status, accepted ?? PostRevision,
            noRecipe ? null : Recipe, noRecipe ? null : writtenFor ?? Pinned);

    private static IReadOnlyList<(ContentCardPackageReviewReason Reason, string? Channel)> Reasons(
        Guid? current = null,
        bool recipeAvailable = true,
        bool editorialPinned = false,
        EditorialPinRecord? editorial = null,
        params (string, Guid, SocialPinRecord?)[] posts) =>
        [.. RecipePackageReview.Reasons(Pinned, recipeAvailable, current ?? Pinned, editorialPinned, editorial, posts)
            .Select(reason => (reason.Reason, reason.ChannelKey))];

    [Fact]
    public void A_package_whose_sources_all_stand_has_nothing_to_review()
    {
        Assert.Empty(Reasons());
        Assert.Empty(Reasons(editorialPinned: true, editorial: Editorial(), posts: ("instagram", PostRevision, Post())));
    }

    [Fact]
    public void A_recipe_with_a_newer_version_has_moved_and_one_that_cannot_be_read_says_so_instead()
    {
        Assert.Equal([(ContentCardPackageReviewReason.RecipeVersionSuperseded, null)], Reasons(current: Newer));

        // Unavailable is its own reason, and is not also reported as superseded: nothing is known about its versions.
        Assert.Equal([(ContentCardPackageReviewReason.RecipeUnavailable, null)], Reasons(current: Newer, recipeAvailable: false));
    }

    [Fact]
    public void A_recipe_whose_current_version_is_not_known_is_not_assumed_to_have_moved()
    {
        Assert.Empty(RecipePackageReview.Reasons(Pinned, recipeAvailable: true, currentRecipeVersionId: null, false, null, []));
    }

    [Theory]
    [InlineData(ContentProposalStatus.NeedsReview)]
    [InlineData(ContentProposalStatus.Rejected)]
    [InlineData(ContentProposalStatus.Proposed)]
    public void An_editorial_that_is_no_longer_simply_accepted_needs_review(ContentProposalStatus status) =>
        Assert.Equal(
            [(ContentCardPackageReviewReason.EditorialNeedsReview, null)],
            Reasons(editorialPinned: true, editorial: Editorial(status)));

    [Fact]
    public void An_editorial_revision_that_has_been_replaced_or_has_gone_needs_review()
    {
        Assert.Equal(
            [(ContentCardPackageReviewReason.EditorialNeedsReview, null)],
            Reasons(editorialPinned: true, editorial: Editorial(accepted: Guid.NewGuid())));

        // Pinned, and not there to ask: that is a reason to look, never a reason to assume it stands.
        Assert.Equal(
            [(ContentCardPackageReviewReason.EditorialNeedsReview, null)],
            Reasons(editorialPinned: true, editorial: null));
    }

    [Fact]
    public void An_editorial_written_for_another_version_of_the_recipe_says_so_even_while_accepted() =>
        Assert.Equal(
            [(ContentCardPackageReviewReason.EditorialForAnotherRecipeVersion, null)],
            Reasons(editorialPinned: true, editorial: Editorial(writtenFor: Newer)));

    [Fact]
    public void A_post_is_judged_channel_by_channel()
    {
        var blog = new SocialPinRecord(Guid.NewGuid(), Guid.NewGuid(), "blog", Guid.NewGuid(), Guid.NewGuid(), ContentProposalStatus.NeedsReview, Guid.NewGuid(), Recipe, Pinned);

        Assert.Equal(
            [(ContentCardPackageReviewReason.PostNeedsReview, "blog")],
            Reasons(posts: [("instagram", PostRevision, Post()), ("blog", blog.RevisionId, blog)]));

        Assert.Equal(
            [(ContentCardPackageReviewReason.PostNeedsReview, "instagram")],
            Reasons(posts: ("instagram", PostRevision, Post(accepted: Guid.NewGuid()))));

        Assert.Equal(
            [(ContentCardPackageReviewReason.PostNeedsReview, "instagram")],
            Reasons(posts: ("instagram", PostRevision, null)));
    }

    [Fact]
    public void A_post_written_for_another_version_says_so_and_one_written_for_no_recipe_does_not()
    {
        Assert.Equal(
            [(ContentCardPackageReviewReason.PostForAnotherRecipeVersion, "instagram")],
            Reasons(posts: ("instagram", PostRevision, Post(writtenFor: Newer))));

        Assert.Empty(Reasons(posts: ("instagram", PostRevision, Post(noRecipe: true))));
    }

    [Fact]
    public void A_package_with_nothing_to_review_is_never_blocked_and_one_with_no_allowance_always_is()
    {
        ContentCardPackageReviewServiceModel[] editorial = [new(ContentCardPackageReviewReason.EditorialNeedsReview, null)];

        Assert.False(RecipePackageReview.IsBlocked([], allowedReasons: null));
        Assert.False(RecipePackageReview.IsBlocked([], allowedReasons: "EditorialNeedsReview"));
        Assert.True(RecipePackageReview.IsBlocked(editorial, allowedReasons: null));
        Assert.True(RecipePackageReview.IsBlocked(editorial, allowedReasons: string.Empty));
    }

    [Fact]
    public void An_allowance_covers_the_reasons_that_were_looked_at_and_no_others()
    {
        ContentCardPackageReviewServiceModel editorial = new(ContentCardPackageReviewReason.EditorialNeedsReview, null);
        ContentCardPackageReviewServiceModel instagram = new(ContentCardPackageReviewReason.PostNeedsReview, "instagram");
        ContentCardPackageReviewServiceModel blog = new(ContentCardPackageReviewReason.PostNeedsReview, "blog");
        ContentCardPackageReviewServiceModel superseded = new(ContentCardPackageReviewReason.RecipeVersionSuperseded, null);

        var allowed = RecipePackageReview.Encode([instagram, editorial]);

        // The same reasons, in any order, and any fewer of them, stand.
        Assert.False(RecipePackageReview.IsBlocked([editorial, instagram], allowed));
        Assert.False(RecipePackageReview.IsBlocked([instagram], allowed));

        // A reason nobody looked at blocks the package again — a new kind, or the same kind on another channel.
        Assert.True(RecipePackageReview.IsBlocked([editorial, instagram, superseded], allowed));
        Assert.True(RecipePackageReview.IsBlocked([blog], allowed));
    }

    [Fact]
    public void Allowed_reasons_are_stored_the_same_way_whatever_order_they_were_found_in()
    {
        ContentCardPackageReviewServiceModel editorial = new(ContentCardPackageReviewReason.EditorialNeedsReview, null);
        ContentCardPackageReviewServiceModel instagram = new(ContentCardPackageReviewReason.PostNeedsReview, "instagram");

        Assert.Equal(RecipePackageReview.Encode([editorial, instagram]), RecipePackageReview.Encode([instagram, editorial, instagram]));
        Assert.Equal("EditorialNeedsReview;PostNeedsReview:instagram", RecipePackageReview.Encode([instagram, editorial]));
        Assert.Equal(string.Empty, RecipePackageReview.Encode([]));
    }

    [Fact]
    public void Every_reason_is_listed_in_one_fixed_order()
    {
        var reasons = Reasons(
            current: Newer,
            editorialPinned: true,
            editorial: Editorial(ContentProposalStatus.NeedsReview, writtenFor: Newer),
            posts: ("instagram", PostRevision, Post(ContentProposalStatus.NeedsReview, writtenFor: Newer)));

        Assert.Equal(
            [
                (ContentCardPackageReviewReason.RecipeVersionSuperseded, null),
                (ContentCardPackageReviewReason.EditorialNeedsReview, null),
                (ContentCardPackageReviewReason.EditorialForAnotherRecipeVersion, null),
                (ContentCardPackageReviewReason.PostNeedsReview, "instagram"),
                (ContentCardPackageReviewReason.PostForAnotherRecipeVersion, "instagram"),
            ],
            reasons);
    }
}
