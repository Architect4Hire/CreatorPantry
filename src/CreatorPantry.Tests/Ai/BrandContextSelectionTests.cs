using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// 11A.19's deterministic rules on their own: which sections a task is grounded on, what a package is estimated
/// to cost, what the budget drops, and the checksum that identifies the result.
/// </summary>
/// <remarks>
/// Unit tests over a pure type, so each rule is asserted directly rather than through whichever assemblies a
/// database test happens to arrange — including the cases a creator would have to try hard to produce.
/// </remarks>
public sealed class BrandContextSelectionTests
{
    // ---- which sections a task gets ---------------------------------------------------------------------

    /// <summary>
    /// Style rules may not alter canonical recipe facts or safety, so the capabilities that produce or judge
    /// those facts are grounded in no brand voice at all.
    /// </summary>
    [Theory]
    [InlineData(AiTaskType.IngredientSubstitution)]
    [InlineData(AiTaskType.RecipeReview)]
    [InlineData(AiTaskType.RecipeRevision)]
    [InlineData(AiTaskType.RecipeAdaptation)]
    [InlineData(AiTaskType.ProposalExplanation)]
    [InlineData(AiTaskType.Diagnostic)]
    [InlineData(AiTaskType.Unspecified)]
    public void A_task_that_handles_recipe_facts_is_grounded_in_no_brand_voice(AiTaskType taskType)
    {
        Assert.Empty(BrandContextSelection.SectionKeysFor(taskType));
        Assert.False(BrandContextSelection.AppliesTo(taskType));
    }

    /// <summary>Grounding the task that writes a guide in the guide it is proposing would be circular.</summary>
    [Fact]
    public void The_brand_guide_proposal_is_not_grounded_in_the_brand_guide()
    {
        Assert.Empty(BrandContextSelection.SectionKeysFor(AiTaskType.BrandGuideProposal));
    }

    [Theory]
    [InlineData(AiTaskType.EditorialPackage)]
    [InlineData(AiTaskType.SeoPackage)]
    [InlineData(AiTaskType.RecipeConcepts)]
    [InlineData(AiTaskType.RecipeFirstDraft)]
    public void A_writing_task_is_grounded_in_the_voice_sections(AiTaskType taskType)
    {
        var keys = BrandContextSelection.SectionKeysFor(taskType);

        Assert.Contains(BrandStyleGuideSectionKey.Voice, keys);
        Assert.Contains(BrandStyleGuideSectionKey.Tone, keys);
        Assert.Contains(BrandStyleGuideSectionKey.Vocabulary, keys);
        Assert.True(BrandContextSelection.AppliesTo(taskType));
    }

    /// <summary>Long-form gets how a post is built; the others do not need it.</summary>
    [Fact]
    public void Only_long_form_editorial_gets_storytelling_and_calls_to_action()
    {
        var editorial = BrandContextSelection.SectionKeysFor(AiTaskType.EditorialPackage);
        var seo = BrandContextSelection.SectionKeysFor(AiTaskType.SeoPackage);

        Assert.Contains(BrandStyleGuideSectionKey.Storytelling, editorial);
        Assert.Contains(BrandStyleGuideSectionKey.CallsToAction, editorial);
        Assert.Contains(BrandStyleGuideSectionKey.BlogGuidance, editorial);

        Assert.DoesNotContain(BrandStyleGuideSectionKey.Storytelling, seo);
        Assert.DoesNotContain(BrandStyleGuideSectionKey.CallsToAction, seo);
    }

    /// <summary>
    /// The creator's own notes on their guide are notes to themselves. Sending them as guidance would turn a
    /// reminder into an instruction.
    /// </summary>
    [Fact]
    public void No_task_is_ever_grounded_in_the_creators_own_notes()
    {
        Assert.DoesNotContain(BrandStyleGuideSectionKey.UserNotes, BrandContextSelection.AllSelectableKeys);
    }

    /// <summary>
    /// A channel variant is never in the table: it is selected by the channel being written for, not by the
    /// task, so a table entry would make it apply to every channel at once.
    /// </summary>
    [Fact]
    public void A_channel_variant_is_not_selected_by_the_task()
    {
        Assert.DoesNotContain(BrandStyleGuideSectionKey.ChannelVariant, BrandContextSelection.AllSelectableKeys);
    }

    /// <summary>A task's keys are distinct and in a fixed order, so two assemblies cannot differ by ordering.</summary>
    [Theory]
    [InlineData(AiTaskType.EditorialPackage)]
    [InlineData(AiTaskType.SeoPackage)]
    [InlineData(AiTaskType.BrandStyleTestDrive)]
    public void A_tasks_section_keys_are_distinct_and_stable(AiTaskType taskType)
    {
        var first = BrandContextSelection.SectionKeysFor(taskType);
        var second = BrandContextSelection.SectionKeysFor(taskType);

        Assert.Equal(first, second);
        Assert.Equal(first.Distinct().Count(), first.Count);
    }

    // ---- the token estimate ----------------------------------------------------------------------------

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    public void The_estimate_rounds_up_so_nothing_is_counted_as_free(string? text, int expected)
    {
        Assert.Equal(expected, BrandContextSelection.EstimateTokens(text));
    }

    [Fact]
    public void A_packages_estimate_counts_every_piece_of_creator_text_it_carries()
    {
        var estimate = BrandContextSelection.EstimatePackageTokens(
            Profile("Pantry", description: "Weeknight baking.", audience: "Home cooks"),
            [Guidance(BrandStyleGuideSectionKey.Voice, "Plain and unhurried.")],
            [new BrandContextRule(BrandStyleGuideRuleKind.Do, "Say it plainly.")],
            [Excerpt("We write like a friend.")],
            audience: "Home cooks");

        Assert.Equal(
            BrandContextSelection.EstimateProfileTokens(
                Profile("Pantry", description: "Weeknight baking.", audience: "Home cooks"))
                + BrandContextSelection.EstimateTokens("Home cooks")
                + BrandContextSelection.EstimateTokens("Plain and unhurried.")
                + BrandContextSelection.EstimateTokens("Say it plainly.")
                + BrandContextSelection.EstimateTokens("We write like a friend."),
            estimate);
    }

    [Fact]
    public void A_package_with_nothing_in_it_is_estimated_at_nothing()
    {
        Assert.Equal(0, BrandContextSelection.EstimatePackageTokens(null, [], [], [], null));
        Assert.Equal(0, BrandContextSelection.EstimateProfileTokens(null));
    }

    // ---- the budget ------------------------------------------------------------------------------------

    [Fact]
    public void Everything_fits_when_the_budget_is_ample()
    {
        var result = BrandContextSelection.Spend(
            budget: 10_000,
            Profile("Pantry"),
            audience: "Home cooks",
            [Guidance(BrandStyleGuideSectionKey.Voice, "Plain.")],
            [new BrandContextRule(BrandStyleGuideRuleKind.Do, "Say it plainly.")],
            [Excerpt("A passage.")]);

        Assert.Equal(0, result.DroppedGuidance);
        Assert.Equal(0, result.DroppedExcerpts);
        Assert.Single(result.Guidance);
        Assert.Single(result.Excerpts);
    }

    /// <summary>
    /// Excerpts go first: an excerpt is evidence for a style the sections already state, so it is the most
    /// replaceable thing in the package.
    /// </summary>
    [Fact]
    public void Excerpts_are_dropped_before_guidance()
    {
        var result = BrandContextSelection.Spend(
            budget: BrandContextSelection.EstimateTokens(new string('g', 40)) + 1,
            profile: null,
            audience: null,
            [Guidance(BrandStyleGuideSectionKey.Voice, new string('g', 40))],
            [],
            [Excerpt(new string('e', 400))]);

        Assert.Single(result.Guidance);
        Assert.Empty(result.Excerpts);
        Assert.Equal(1, result.DroppedExcerpts);
        Assert.Equal(0, result.DroppedGuidance);
    }

    /// <summary>Half an excerpt is not a citable passage, and half a section is advice with its caveat cut off.</summary>
    [Fact]
    public void An_item_that_does_not_fit_is_dropped_whole_rather_than_truncated()
    {
        var kept = Excerpt("short");
        var dropped = Excerpt(new string('e', 4_000));

        var result = BrandContextSelection.Spend(
            budget: 100, profile: null, audience: null, [], [], [kept, dropped]);

        Assert.Equal([kept], result.Excerpts);
        Assert.Equal(1, result.DroppedExcerpts);
        Assert.All(result.Excerpts, excerpt => Assert.Equal("short", excerpt.Text));
    }

    /// <summary>
    /// A later item that fits is still taken after an earlier one was dropped: the budget skips what will not
    /// fit rather than stopping at the first refusal.
    /// </summary>
    [Fact]
    public void A_small_item_after_an_oversized_one_still_fits()
    {
        var big = Guidance(BrandStyleGuideSectionKey.Voice, new string('g', 4_000));
        var small = Guidance(BrandStyleGuideSectionKey.Tone, "Warm.");

        var result = BrandContextSelection.Spend(
            budget: 100, profile: null, audience: null, [big, small], [], []);

        Assert.Equal([small], result.Guidance);
        Assert.Equal(1, result.DroppedGuidance);
    }

    /// <summary>
    /// Rules and the profile are never dropped: a guide whose do-and-don't list cannot fit is a product problem
    /// to surface, not one to paper over by discarding the brand's hard constraints.
    /// </summary>
    [Fact]
    public void Rules_and_profile_facts_survive_a_budget_they_do_not_fit()
    {
        var rules = new[] { new BrandContextRule(BrandStyleGuideRuleKind.Dont, new string('r', 4_000)) };

        var result = BrandContextSelection.Spend(
            budget: 1, Profile("Pantry"), audience: null, [Guidance(BrandStyleGuideSectionKey.Voice, "Plain.")], rules, []);

        Assert.True(result.EstimatedTokens > 1);
        Assert.Empty(result.Guidance);
        Assert.Equal(1, result.DroppedGuidance);
    }

    [Fact]
    public void The_reported_cost_is_the_cost_of_what_was_kept()
    {
        var result = BrandContextSelection.Spend(
            budget: 100,
            profile: null,
            audience: null,
            [Guidance(BrandStyleGuideSectionKey.Voice, "Plain.")],
            [],
            [Excerpt(new string('e', 4_000)), Excerpt("kept")]);

        Assert.Equal(
            BrandContextSelection.EstimatePackageTokens(
                null, result.Guidance, [], result.Excerpts, null),
            result.EstimatedTokens);
    }

    // ---- the checksum ----------------------------------------------------------------------------------

    [Fact]
    public void The_same_state_hashes_the_same_twice()
    {
        Assert.Equal(Checksum(), Checksum());
    }

    [Fact]
    public void The_checksum_is_prefixed_and_lowercase_hex()
    {
        var checksum = Checksum();

        Assert.StartsWith("sha256:", checksum, StringComparison.Ordinal);
        Assert.Equal(7 + 64, checksum.Length);
        Assert.Equal(checksum, checksum.ToLowerInvariant());
    }

    /// <summary>Everything pinned or selected moves the checksum, because that is what it is for.</summary>
    [Fact]
    public void Every_pinned_input_changes_the_checksum()
    {
        var baseline = Checksum();

        Assert.NotEqual(baseline, Checksum(taskType: AiTaskType.SeoPackage));
        Assert.NotEqual(baseline, Checksum(channelKey: "pinterest"));
        Assert.NotEqual(baseline, Checksum(audience: "Someone else"));
        Assert.NotEqual(baseline, Checksum(profile: Profile("Pantry", revision: 9)));
        Assert.NotEqual(baseline, Checksum(profile: Profile("Another brand")));
        Assert.NotEqual(baseline, Checksum(guideVersionId: Guid.NewGuid()));
        Assert.NotEqual(baseline, Checksum(guideVersionNumber: 7));
        Assert.NotEqual(baseline, Checksum(guidance: [Guidance(BrandStyleGuideSectionKey.Voice, "Different.")]));
        Assert.NotEqual(baseline, Checksum(rules: [new BrandContextRule(BrandStyleGuideRuleKind.Dont, "No.")]));
        Assert.NotEqual(baseline, Checksum(excerpts: [Excerpt("Other words.")]));
    }

    /// <summary>
    /// A corrected extraction rewrites a chunk under the same id, so a package grounded on the new words must
    /// not hash as the old — which is why the excerpt's text is in the checksum and not only its id.
    /// </summary>
    [Fact]
    public void Rewritten_passage_text_under_the_same_id_changes_the_checksum()
    {
        var passageId = Guid.NewGuid();

        Assert.NotEqual(
            Checksum(excerpts: [Excerpt("The original words.", passageId)]),
            Checksum(excerpts: [Excerpt("The corrected words.", passageId)]));
    }

    /// <summary>
    /// Lengths are written before values so two different selections cannot render to one canonical string — a
    /// body ending in a separator must not be able to forge the next field.
    /// </summary>
    [Fact]
    public void Text_that_looks_like_the_canonical_format_cannot_forge_another_field()
    {
        Assert.NotEqual(
            Checksum(guidance: [Guidance(BrandStyleGuideSectionKey.Voice, "a\nrule:Do=3:yes")]),
            Checksum(
                guidance: [Guidance(BrandStyleGuideSectionKey.Voice, "a")],
                rules: [new BrandContextRule(BrandStyleGuideRuleKind.Do, "yes")]));
    }

    /// <summary>A null and an empty string are different states, and the checksum keeps them apart.</summary>
    [Fact]
    public void A_null_and_an_empty_value_do_not_hash_alike()
    {
        Assert.NotEqual(Checksum(audience: null), Checksum(audience: string.Empty));
    }

    /// <summary>
    /// Two sections differing only in channel are two different selections — otherwise one channel's variant
    /// could hash as another's.
    /// </summary>
    [Fact]
    public void A_channel_variant_hashes_differently_per_channel()
    {
        Assert.NotEqual(
            Checksum(guidance: [Variant("instagram", "Short.")]),
            Checksum(guidance: [Variant("pinterest", "Short.")]));
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static readonly Guid GuideVersionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DocumentId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static string Checksum(
        AiTaskType taskType = AiTaskType.EditorialPackage,
        string? channelKey = "instagram",
        string? audience = "Home cooks",
        BrandContextProfile? profile = null,
        Guid? guideVersionId = null,
        int? guideVersionNumber = 3,
        IReadOnlyList<BrandContextGuidance>? guidance = null,
        IReadOnlyList<BrandContextRule>? rules = null,
        IReadOnlyList<BrandContextExcerpt>? excerpts = null) =>
        BrandContextSelection.Checksum(
            taskType,
            channelKey,
            audience,
            profile ?? Profile("Pantry"),
            guideVersionId ?? GuideVersionId,
            guideVersionNumber,
            guidance ?? [Guidance(BrandStyleGuideSectionKey.Voice, "Plain and unhurried.")],
            rules ?? [new BrandContextRule(BrandStyleGuideRuleKind.Do, "Say it plainly.")],
            excerpts ?? [Excerpt("We write like a friend who happens to cook.")]);

    private static BrandContextProfile Profile(
        string name, string? description = null, string? audience = "Home cooks", int revision = 1) =>
        new(name, description, audience, "en-GB", ["instagram"], revision);

    private static BrandContextGuidance Guidance(BrandStyleGuideSectionKey key, string body) =>
        new(key, null, body, BrandContextOrigin.GuideSection);

    private static BrandContextGuidance Variant(string channelKey, string body) =>
        new(BrandStyleGuideSectionKey.ChannelVariant, channelKey, body, BrandContextOrigin.GuideChannelVariant);

    private static BrandContextExcerpt Excerpt(string text, Guid? passageId = null) =>
        new(passageId ?? Guid.Parse("33333333-3333-3333-3333-333333333333"), DocumentId, 1, 1, text);

    [Theory]
    [InlineData(AiTaskType.PhotographyConcept)]
    [InlineData(AiTaskType.ImagePrompt)]
    public void An_image_task_is_grounded_in_the_visual_sections_only(AiTaskType taskType)
    {
        var keys = BrandContextSelection.SectionKeysFor(taskType);

        Assert.Equal(
            [
                BrandStyleGuideSectionKey.VisualIdentity,
                BrandStyleGuideSectionKey.PhotographyDirection,
                BrandStyleGuideSectionKey.ImagePromptGuidance,
                BrandStyleGuideSectionKey.NegativeVisualGuidance,
            ],
            keys);
        Assert.True(BrandContextSelection.IsVisual(taskType));
        Assert.Equal([BrandSourcePurpose.VisualDirection], BrandContextSelection.PurposesFor(taskType));
    }

    [Fact]
    public void A_writing_task_is_not_visual_and_reads_the_writing_purposes()
    {
        Assert.False(BrandContextSelection.IsVisual(AiTaskType.EditorialPackage));
        Assert.DoesNotContain(
            BrandSourcePurpose.VisualDirection, BrandContextSelection.PurposesFor(AiTaskType.EditorialPackage));
    }

    // ---- the test drive, which is the one task grounded in both the voice and the look (11A.24) ---------

    /// <summary>
    /// A test drive writes a blog introduction, a social caption and an image prompt in one pass, so it is the
    /// only entry in the table that unions a writing list with the visual one.
    /// </summary>
    [Fact]
    public void The_test_drive_is_grounded_in_the_voice_and_the_look_together()
    {
        var keys = BrandContextSelection.SectionKeysFor(AiTaskType.BrandStyleTestDrive);

        Assert.Equal(
            [
                BrandStyleGuideSectionKey.Voice,
                BrandStyleGuideSectionKey.Tone,
                BrandStyleGuideSectionKey.Tenor,
                BrandStyleGuideSectionKey.WritingStyle,
                BrandStyleGuideSectionKey.Audience,
                BrandStyleGuideSectionKey.PointOfView,
                BrandStyleGuideSectionKey.Vocabulary,
                BrandStyleGuideSectionKey.SentenceRhythm,
                BrandStyleGuideSectionKey.Formatting,
                BrandStyleGuideSectionKey.Storytelling,
                BrandStyleGuideSectionKey.CallsToAction,
                BrandStyleGuideSectionKey.BlogGuidance,
                BrandStyleGuideSectionKey.SocialGuidance,
                BrandStyleGuideSectionKey.VisualIdentity,
                BrandStyleGuideSectionKey.PhotographyDirection,
                BrandStyleGuideSectionKey.ImagePromptGuidance,
                BrandStyleGuideSectionKey.NegativeVisualGuidance,
            ],
            keys);
        Assert.True(BrandContextSelection.AppliesTo(AiTaskType.BrandStyleTestDrive));
    }

    /// <summary>
    /// Two tasks write for a social channel: the test drive, whose caption is a sample, and the posts
    /// capability (AF.6.3), whose captions are the product. The guide's social guidance reaches those two and
    /// no other. A third entry gaining it would be a capability change, not a tidy-up.
    /// </summary>
    [Fact]
    public void Only_the_test_drive_and_channel_posts_are_grounded_in_the_guides_social_guidance()
    {
        Assert.Contains(
            BrandStyleGuideSectionKey.SocialGuidance, BrandContextSelection.SectionKeysFor(AiTaskType.ChannelPosts));

        // Posts are prose, never an image: no visual section, so a style rule about pictures cannot reach copy.
        Assert.DoesNotContain(
            BrandStyleGuideSectionKey.PhotographyDirection, BrandContextSelection.SectionKeysFor(AiTaskType.ChannelPosts));
        Assert.False(BrandContextSelection.IsVisual(AiTaskType.ChannelPosts));

        foreach (var taskType in Enum.GetValues<AiTaskType>()
            .Where(type => type is not (AiTaskType.BrandStyleTestDrive or AiTaskType.ChannelPosts)))
        {
            Assert.DoesNotContain(
                BrandStyleGuideSectionKey.SocialGuidance,
                BrandContextSelection.SectionKeysFor(taskType));
        }
    }

    /// <summary>
    /// It reads both sets of examples, because it demonstrates both — and it is still not an image task: it
    /// generates no image, and the visual-reference rules that turn on <c>IsVisual</c> are about generating one.
    /// </summary>
    [Fact]
    public void The_test_drive_reads_both_sets_of_examples_without_being_an_image_task()
    {
        var purposes = BrandContextSelection.PurposesFor(AiTaskType.BrandStyleTestDrive);

        Assert.Equal(
            [
                BrandSourcePurpose.Voice,
                BrandSourcePurpose.WritingStyle,
                BrandSourcePurpose.Background,
                BrandSourcePurpose.VisualDirection,
            ],
            purposes);
        Assert.False(BrandContextSelection.IsVisual(AiTaskType.BrandStyleTestDrive));
    }

    /// <summary>
    /// Unioning two lists must not have widened either of them. The image tasks and the long-form writing task
    /// keep exactly the sections they had.
    /// </summary>
    [Fact]
    public void The_union_did_not_widen_the_lists_it_was_built_from()
    {
        Assert.DoesNotContain(
            BrandStyleGuideSectionKey.VisualIdentity,
            BrandContextSelection.SectionKeysFor(AiTaskType.EditorialPackage));
        Assert.DoesNotContain(
            BrandStyleGuideSectionKey.Voice,
            BrandContextSelection.SectionKeysFor(AiTaskType.ImagePrompt));
    }
}
