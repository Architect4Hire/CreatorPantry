using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Ai.EditorialPackage;

/// <summary>
/// LF.5.3: the same handler with an accepted long-form draft as its subject. One capability, so these run the
/// real handler, the real template and the real validator and scanners; only the draft is scripted.
/// </summary>
/// <remarks>
/// That the content module reads only the resolved workspace's drafts is its own tests' and the endpoint
/// tests'. Here a draft that cannot be read stands for every reason one cannot.
/// </remarks>
public sealed partial class SeoPackageAiTaskHandlerTests
{
    private static readonly Guid Draft = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private const string ArticleSections = "seoTitle,metaDescription,keyPhrases,slug,internalLinks";

    private const string ArticleTitle = "The soda bread I make when there is no time";

    private const string ArticleMarkdown =
        "## Why there is no kneading\n\nButtermilk and bicarbonate of soda do the work yeast would.\n\n"
        + "## From bowl to oven\n\nBake at 220C for 25 minutes, until the base sounds hollow.\n";

    private const string ArticleMeta =
        "A soda bread with no kneading and no yeast: buttermilk and bicarbonate of soda do the work, and it bakes in 25 minutes.";

    // ---- what a valid answer becomes ---------------------------------------------------------------------

    [Fact]
    public async Task An_article_becomes_a_proposal_from_the_same_template_pinned_to_no_recipe_version()
    {
        var recipe = await SeedRecipeAsync();
        HoldArticle();

        var outcome = await RunArticle(FakeChatClient.Returning(Answer(ArticleEverything(recipe))), recipe);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        var proposal = outcome.Proposal!;

        // One capability: the template, the schema and the checksum a recipe's package records.
        Assert.Equal(Template.OutputSchemaVersion, proposal.OutputSchemaVersion);
        Assert.Equal(Template.Id, proposal.PromptTemplateId);
        Assert.Equal(Template.BodyChecksum, proposal.PromptTemplateBodyChecksum);
        Assert.Null(proposal.SourceRecipeVersionId);

        Assert.All(proposal.Changes, change =>
        {
            Assert.Equal(AiChangeTargetKind.ContentSection, change.TargetKind);
            Assert.False(AiChangeApplicability.IsApplicable(change.ChangeKind, change.TargetKind));
        });

        var sections = Sections(proposal);
        Assert.Equal(1, sections.Count(section => section == "seoTitle"));
        Assert.Equal(1, sections.Count(section => section == "metaDescription"));
        Assert.Equal(3, sections.Count(section => section == "keyPhrases"));
        Assert.Equal(1, sections.Count(section => section == "internalLinks"));
        Assert.Equal(1, sections.Count(section => section == "slug"));
        Assert.DoesNotContain("altText", sections);
    }

    // ---- what reaches the prompt ---------------------------------------------------------------------------

    [Fact]
    public async Task The_article_reaches_the_model_fenced_as_its_source_and_the_server_says_what_it_is()
    {
        var recipe = await SeedRecipeAsync();
        HoldArticle(note: "A short one this week.");

        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));
        await RunArticle(client, recipe, sections: "seoTitle");

        var user = UserMessage(client);
        var system = SystemMessage(client);

        // The piece as it reads: its title, the note, the sections under their headings.
        Assert.Contains("# " + ArticleTitle, user, StringComparison.Ordinal);
        Assert.Contains("A short one this week.", user, StringComparison.Ordinal);
        Assert.Contains("Bake at 220C for 25 minutes", user, StringComparison.Ordinal);
        Assert.Contains("\"subject\":\"article\"", user, StringComparison.Ordinal);

        // And no recipe: the subject is the article, not the recipe the test happens to have seeded.
        Assert.DoesNotContain(IngredientLine, user, StringComparison.Ordinal);

        // Creator words are data. None of them is in the instructions.
        Assert.DoesNotContain("Bake at 220C", system, StringComparison.Ordinal);
        Assert.DoesNotContain(ArticleTitle, system, StringComparison.Ordinal);
        Assert.Contains("When the subject is an article", system, StringComparison.Ordinal);

        // An article is offered no alt text, so it is not among the sections the model is asked for.
        Assert.DoesNotContain("\"altText\"", user[user.IndexOf("\"sections\"", StringComparison.Ordinal)..user.IndexOf("\"linkCandidates\"", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recipe_subject_is_named_as_one_and_still_reads_the_recipe()
    {
        var recipe = await SeedRecipeAsync();
        HoldArticle();

        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));
        var outcome = await Run(client, recipe, sections: "seoTitle");

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        Assert.Contains("\"subject\":\"recipe\"", UserMessage(client), StringComparison.Ordinal);
        Assert.Contains(IngredientLine, UserMessage(client), StringComparison.Ordinal);
        Assert.DoesNotContain("Bake at 220C for 25 minutes, until the base sounds hollow.", UserMessage(client), StringComparison.Ordinal);

        // A recipe's package never read a draft at all.
        Assert.Equal(0, _documents.Reads);
        Assert.Equal(recipe.VersionId, outcome.Proposal!.SourceRecipeVersionId);
    }

    [Fact]
    public async Task An_article_may_link_to_any_approved_recipe_of_the_workspace_and_to_no_other()
    {
        var recipe = await SeedRecipeAsync();
        HoldArticle();

        var client = FakeChatClient.Returning(Answer(ArticleEverything(recipe)));
        var outcome = await RunArticle(client, recipe);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);

        // Not a recipe itself, so the recipe it is about is a link it may suggest — the best one there is.
        var reference = ReferenceSegment(UserMessage(client));
        Assert.Contains(recipe.RecipeId.ToString(), reference, StringComparison.Ordinal);
        Assert.Contains(recipe.OtherRecipeId.ToString(), reference, StringComparison.Ordinal);
        Assert.DoesNotContain("Unfinished Draft Loaf", reference, StringComparison.Ordinal);
        Assert.DoesNotContain("Other Workspace Secret Loaf", reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_link_idea_to_another_workspaces_recipe_fails_an_articles_package_too()
    {
        var recipe = await SeedRecipeAsync();
        HoldArticle();

        var client = FakeChatClient.Returning(Answer(
            $$""" "internalLinks":[{"recipeId":"{{recipe.OtherWorkspaceRecipeId}}","anchorText":"A loaf","reason":"It is another loaf."}] """));

        var outcome = await RunArticle(client, recipe, sections: "internalLinks");

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Proposal);
        Assert.DoesNotContain("Secret", outcome.FailureSummary!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- no image, so no alt text --------------------------------------------------------------------------

    [Fact]
    public async Task An_answer_that_writes_alt_text_about_an_article_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();
        HoldArticle();

        // An image id the model could only have invented, or lifted from the article's own Markdown.
        var client = FakeChatClient.Returning(Answer(
            $$""" "seoTitle":{"text":"{{Title}}"},"altText":[{"assetLinkId":"{{recipe.CaptionedAssetId}}","text":"Soda bread","basis":"RecipeTitle"}] """));

        var outcome = await RunArticle(client, recipe);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Proposal);
    }

    [Fact]
    public async Task An_operation_asking_for_alt_text_about_an_article_is_refused_before_the_model_is_called()
    {
        var recipe = await SeedRecipeAsync();
        HoldArticle();

        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));
        var outcome = await RunArticle(client, recipe, sections: "seoTitle,altText");

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.Validation, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
    }

    // ---- recommendation, never measurement; the article's figures, never others ----------------------------

    [Fact]
    public async Task A_metric_and_a_figure_the_article_does_not_state_are_each_reported_on_an_articles_package()
    {
        var recipe = await SeedRecipeAsync();
        HoldArticle();

        var client = FakeChatClient.Returning(Answer(
            "\"metaDescription\":{\"text\":\"The top ranking soda bread article with high search volume, baked in 45 minutes for busy days.\"}"));

        var outcome = await RunArticle(client, recipe, sections: "metaDescription");

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        Assert.Contains(outcome.Proposal!.Warnings, warning => warning.Message.Contains(AiSeoClaimScanner.UnsupportedMetric, StringComparison.Ordinal));
        Assert.Contains(outcome.Proposal.Warnings, warning => warning.Message.Contains(AiEditorialClaimScanner.UnsupportedNumber, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_figure_the_article_states_is_not_reported()
    {
        var recipe = await SeedRecipeAsync();
        HoldArticle();

        var outcome = await RunArticle(
            FakeChatClient.Returning(Answer("\"metaDescription\":{\"text\":\"" + ArticleMeta + "\"}")), recipe, sections: "metaDescription");

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        Assert.DoesNotContain(outcome.Proposal!.Warnings, warning => warning.Message.Contains(AiEditorialClaimScanner.UnsupportedNumber, StringComparison.Ordinal));
    }

    // ---- the slug is code's ------------------------------------------------------------------------------

    [Fact]
    public async Task An_articles_slug_is_derived_by_rule_and_is_the_same_every_time()
    {
        var recipe = await SeedRecipeAsync();
        HoldArticle();

        async Task<(string Slug, string Basis)> SlugAsync(string answer, string sections)
        {
            var outcome = await RunArticle(FakeChatClient.Returning(Answer(answer)), recipe, sections: sections);
            Assert.True(outcome.Succeeded, outcome.FailureSummary);

            var slug = outcome.Proposal!.Changes.Single(change =>
                change.ChangeKind is AiChangeKind.Add && Field(outcome.Proposal, change, AiEditorialFields.Section) == "slug");

            return (slug.AfterValue!, Field(outcome.Proposal, slug, AiSeoFields.SlugBasis));
        }

        var titled = "\"seoTitle\":{\"text\":\"" + Title + "\"}";

        var first = await SlugAsync(titled, "seoTitle,slug");
        var second = await SlugAsync(titled, "seoTitle,slug");

        Assert.Equal(("buttermilk-soda-bread-an-easy-weeknight-loaf", "seoTitle"), first);
        Assert.Equal(first, second);

        // With no title proposed, the article's own — and said to be the article's, not a recipe's.
        var untitled = await SlugAsync("\"metaDescription\":{\"text\":\"" + ArticleMeta + "\"}", "metaDescription,slug");
        Assert.Equal(("the-soda-bread-i-make-when-there-is-no-time", "articleTitle"), untitled);

        // A slug the model wrote is not a field there is.
        var written = await RunArticle(
            FakeChatClient.Returning(Answer(titled + ",\"slug\":{\"text\":\"my-own-slug\"}")), recipe, sections: "seoTitle,slug");
        Assert.False(written.Succeeded);
    }

    // ---- the subject must still be what was asked about ----------------------------------------------------

    [Theory]
    [InlineData(LongFormDraftStatus.NeedsReview, 4, "no longer accepted")]
    [InlineData(LongFormDraftStatus.Writing, null, "no longer accepted")]
    [InlineData(LongFormDraftStatus.Accepted, 5, "different revision")]
    public async Task A_piece_that_is_not_accepted_at_the_revision_asked_about_is_refused_before_the_model_is_called(
        LongFormDraftStatus status, int? acceptedRevision, string says)
    {
        var recipe = await SeedRecipeAsync();
        _documents.Hold(Draft, ArticleMarkdown, status, acceptedRevision);
        _exports.Title(Draft, ArticleTitle);

        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));
        var outcome = await RunArticle(client, recipe, sections: "seoTitle");

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
        Assert.Contains(says, outcome.FailureSummary!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(client.LastMessages);
    }

    [Fact]
    public async Task A_piece_whose_sources_move_while_the_model_runs_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();
        HoldArticle();

        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));
        client.BeforeAnswering = () =>
        {
            _documents.Hold(Draft, ArticleMarkdown, LongFormDraftStatus.NeedsReview);

            return Task.CompletedTask;
        };

        var outcome = await RunArticle(client, recipe, sections: "seoTitle");

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Proposal);
        Assert.NotNull(client.LastMessages);
        Assert.Contains("no longer accepted", outcome.FailureSummary!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_draft_that_cannot_be_read_is_refused_and_nothing_of_it_is_said()
    {
        var recipe = await SeedRecipeAsync();

        // No draft held: an unknown one, a removed one and another workspace's are all this.
        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));
        var outcome = await RunArticle(client, recipe, sections: "seoTitle");

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.Validation, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
        Assert.DoesNotContain(Draft.ToString(), outcome.FailureSummary!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_operation_naming_a_draft_and_no_revision_is_refused()
    {
        var recipe = await SeedRecipeAsync();
        HoldArticle();

        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));
        var outcome = await Run(
            client, recipe, recipeless: true, sections: "seoTitle",
            subjectInputs: new Dictionary<string, string> { [AiSeoPackageInputs.LongFormDraftId] = Draft.ToString("D") },
            operationScope: AiOperationScope.NotApplicable);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.Validation, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private void HoldArticle(string? title = ArticleTitle, string? note = null)
    {
        _documents.Hold(Draft, ArticleMarkdown, framing: note is null ? null : new LongFormFramingServiceModel(null, note));
        _exports.Title(Draft, title);
    }

    private Task<AiTaskHandlerOutcome> RunArticle(FakeChatClient client, SeededRecipe recipe, string sections = ArticleSections) =>
        Run(
            client,
            recipe,
            recipeless: true,
            sections: sections,
            subjectInputs: new Dictionary<string, string>
            {
                [AiSeoPackageInputs.LongFormDraftId] = Draft.ToString("D"),
                [AiSeoPackageInputs.LongFormRevisionNumber] = "4",
            },
            operationScope: AiOperationScope.NotApplicable);

    private static string ArticleEverything(SeededRecipe recipe) => $$"""
        "seoTitle":{"text":"{{Title}}"},
        "metaDescription":{"text":"{{ArticleMeta}}"},
        "keyPhrases":[{"phrase":"soda bread"},{"phrase":"no knead bread"},{"phrase":"buttermilk bread"}],
        "internalLinks":[{"recipeId":"{{recipe.RecipeId}}","anchorText":"Buttermilk soda bread","reason":"It is the recipe this article is about."}]
        """;

    private static List<string> Sections(AiProposal proposal) =>
    [
        .. proposal.Changes
            .Where(change => change.ChangeKind is AiChangeKind.Add)
            .Select(add => Field(proposal, add, AiEditorialFields.Section)),
    ];

    private static string Field(AiProposal proposal, AiStructuredChange add, string name) =>
        proposal.Changes.Single(change => change.TargetId == add.TargetId && change.FieldName == name).AfterValue!;
}
