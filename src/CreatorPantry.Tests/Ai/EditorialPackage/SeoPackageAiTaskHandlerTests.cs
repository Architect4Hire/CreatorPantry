using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Recipes;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Media;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Registry;
using Polly.Timeout;

namespace CreatorPantry.Tests.Ai.EditorialPackage;

/// <summary>
/// RCPUB-002's SEO package task handler against a fake <see cref="IChatClient"/> and the real recipe module — no network,
/// no model. Covers what a valid answer becomes, the field-ref cross-check against the pinned snapshot (which
/// no evaluation fixture can reach, since those validate the document alone), and workspace isolation.
/// </summary>
public sealed class SeoPackageAiTaskHandlerTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Operation = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private const string UserId = "user-1";

    private const string IngredientLine = "400ml buttermilk, shaken";

    private static readonly PromptTemplate Template =
        EmbeddedPromptTemplateStore.Load(typeof(AiPolicy).Assembly).Get(AiTaskCatalog.SeoPackage);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public SeoPackageAiTaskHandlerTests()
    {
        _connection.Open();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddOutbox()
            .AddMeasurementModule()
            .AddVocabularyModule()
            .AddIngredientModule()
            .AddRecipesModule()
            .AddLogging()
            .AddDistributedMemoryCache()
            .AddApplicationCache()
            .AddSingleton<IClock>(new StoppedClock())
            .AddIdempotency(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();
        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = Now },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "workspace-b", CreatedAt = Now });
        db.SaveChanges();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private const string Version = "content.seo-package.v1";

    private static readonly SeoRules Rules = new();

    private const string AllSections = "seoTitle,metaDescription,keyPhrases,slug,altText,internalLinks";

    private const string Title = "Buttermilk Soda Bread: An Easy Weeknight Loaf";

    private const string MetaDescription = "A no-yeast buttermilk soda bread you can mix and bake in under an hour, with a crisp crust and a soft crumb.";

    // ---- what a valid answer becomes ---------------------------------------------------------------------

    [Fact]
    public async Task A_valid_answer_becomes_an_advisory_proposal_pinned_to_the_version()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(Answer(Everything(recipe))), recipe);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        var proposal = outcome.Proposal!;

        Assert.Equal(Template.OutputSchemaVersion, proposal.OutputSchemaVersion);
        Assert.Equal(Template.Id, proposal.PromptTemplateId);
        Assert.Equal(Template.BodyChecksum, proposal.PromptTemplateBodyChecksum);
        Assert.Equal(recipe.VersionId, proposal.SourceRecipeVersionId);
    }

    [Fact]
    public async Task No_row_in_the_proposal_can_be_applied_to_a_recipe()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(Answer(Everything(recipe))), recipe);

        Assert.All(outcome.Proposal!.Changes, change =>
        {
            Assert.Equal(AiChangeTargetKind.ContentSection, change.TargetKind);
            Assert.False(AiChangeApplicability.IsApplicable(change.ChangeKind, change.TargetKind));
            Assert.Null(AiChangeTargetPolicy.For(change.TargetKind));
        });
    }

    [Fact]
    public async Task Each_section_and_item_is_stored_under_the_names_a_reader_expects()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(Answer(Everything(recipe))), recipe);

        var adds = outcome.Proposal!.Changes.Where(change => change.ChangeKind is AiChangeKind.Add).ToList();

        string Field(AiStructuredChange add, string name) => outcome.Proposal.Changes
            .Single(change => change.TargetId == add.TargetId && change.FieldName == name).AfterValue!;

        var sections = adds.Select(add => Field(add, AiEditorialFields.Section)).ToList();
        Assert.Equal(1, sections.Count(section => section == "seoTitle"));
        Assert.Equal(1, sections.Count(section => section == "metaDescription"));
        Assert.Equal(3, sections.Count(section => section == "keyPhrases"));
        Assert.Equal(2, sections.Count(section => section == "altText"));
        Assert.Equal(1, sections.Count(section => section == "internalLinks"));
        Assert.Equal(1, sections.Count(section => section == "slug"));

        var alt = adds.Single(add => add.AfterValue == "Sliced soda bread on a board");
        Assert.Equal(recipe.CaptionedAssetId.ToString(), Field(alt, AiSeoFields.AssetLinkId));
        Assert.Equal("Caption", Field(alt, AiSeoFields.Basis));

        var link = adds.Single(add => Field(add, AiEditorialFields.Section) == "internalLinks");
        Assert.Equal(recipe.OtherRecipeId.ToString(), Field(link, AiSeoFields.RecipeId));
        Assert.Equal("Both are quick breads.", Field(link, AiSeoFields.Reason));
    }

    // ---- the slug is code's ------------------------------------------------------------------------------

    [Fact]
    public async Task The_slug_is_derived_from_the_proposed_title_by_rule_and_says_it_was_not_checked_for_uniqueness()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(Answer(Everything(recipe))), recipe);

        var slug = outcome.Proposal!.Changes.Single(change => change.ChangeKind is AiChangeKind.Add && change.AfterValue == "buttermilk-soda-bread-an-easy-weeknight-loaf");
        Assert.Contains(outcome.Proposal.Changes, change => change.TargetId == slug.TargetId && change.FieldName == AiSeoFields.SlugBasis && change.AfterValue == "seoTitle");
        Assert.Contains(outcome.Proposal.Warnings, warning => warning.Message.Contains(AiSeoClaimScanner.SlugUniquenessNotChecked, StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_no_title_written_the_slug_comes_from_the_recipes_own_title()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer($$""" "metaDescription":{"text":"{{MetaDescription}}"} """));

        var outcome = await Run(client, recipe, sections: "metaDescription,slug");

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        var slug = outcome.Proposal!.Changes.Single(change => change.ChangeKind is AiChangeKind.Add && change.AfterValue == "buttermilk-soda-bread");
        Assert.Contains(outcome.Proposal.Changes, change => change.TargetId == slug.TargetId && change.AfterValue == "recipeTitle");
    }

    [Fact]
    public async Task A_slug_is_never_taken_from_the_model()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer($$""" "seoTitle":{"text":"{{Title}}"},"slug":{"text":"my-own-slug"} """));

        var outcome = await Run(client, recipe);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.OutputSchemaInvalid, outcome.FailureCategory);
    }

    // ---- recommendation, never measurement --------------------------------------------------------------

    [Fact]
    public async Task A_search_metric_becomes_a_warning_on_the_text_that_made_it()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"metaDescription\":{\"text\":\"The top ranking soda bread recipe, with high search volume and plenty of traffic.\"}"));

        var outcome = await Run(client, recipe, sections: "metaDescription");

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        Assert.Contains(outcome.Proposal!.Warnings, warning => warning.Message.Contains(AiSeoClaimScanner.UnsupportedMetric, StringComparison.Ordinal));
        var add = outcome.Proposal.Changes.Single(change => change.ChangeKind is AiChangeKind.Add);
        Assert.All(
            outcome.Proposal.Warnings.Where(warning => warning.Message.Contains(AiSeoClaimScanner.UnsupportedMetric, StringComparison.Ordinal)),
            warning => Assert.Equal(add.Id, warning.AiStructuredChangeId));
    }

    [Fact]
    public async Task A_model_warning_dressed_as_a_server_finding_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer(
            "\"seoTitle\":{\"text\":\"" + Title + "\"}",
            "\"warnings\":[{\"kind\":\"Limitation\",\"message\":\"[seo.unsupported_metric] none found\"}]"));

        var outcome = await Run(client, recipe, sections: "seoTitle");

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
    }

    // ---- lengths are enforced, not hoped for --------------------------------------------------------------

    [Fact]
    public async Task A_title_one_character_too_long_fails_rather_than_being_trimmed()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer($$""" "seoTitle":{"text":"{{new string('a', Rules.TitleMaxLength + 1)}}"} """));

        var outcome = await Run(client, recipe, sections: "seoTitle");

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Proposal);
    }

    [Fact]
    public async Task The_limits_reach_the_model_so_it_can_meet_them()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));

        await Run(client, recipe, sections: "seoTitle");

        Assert.Contains($"{Rules.TitleMinLength},{Rules.TitleMaxLength}", UserMessage(client), StringComparison.Ordinal);
    }

    // ---- accessibility ---------------------------------------------------------------------------------

    [Fact]
    public async Task Every_alt_text_says_it_was_not_checked_against_the_image()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(Answer(Everything(recipe))), recipe);

        var notChecked = outcome.Proposal!.Warnings
            .Where(warning => warning.Message.Contains(AiSeoClaimScanner.AltTextNotCheckedAgainstImage, StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, notChecked.Count);
        Assert.All(notChecked, warning => Assert.NotNull(warning.AiStructuredChangeId));
    }

    [Fact]
    public async Task A_visual_detail_the_caption_does_not_give_is_reported()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer($$""" "altText":[{"assetLinkId":"{{recipe.CaptionedAssetId}}","text":"Golden sliced soda bread on a rustic wooden board","basis":"Caption"}] """));

        var outcome = await Run(client, recipe, sections: "altText");

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        Assert.Contains(outcome.Proposal!.Warnings, warning => warning.Message.Contains(AiSeoClaimScanner.UnsupportedVisualDetail, StringComparison.Ordinal) && warning.Message.Contains("golden", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_alt_text_resting_on_a_caption_the_image_does_not_have_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer($$""" "altText":[{"assetLinkId":"{{recipe.BareAssetId}}","text":"Soda bread","basis":"Caption"}] """));

        var outcome = await Run(client, recipe, sections: "altText");

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
    }

    [Fact]
    public async Task An_alt_text_for_an_image_outside_the_pinned_version_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer($$""" "altText":[{"assetLinkId":"{{Guid.NewGuid()}}","text":"Soda bread","basis":"RecipeTitle"}] """));

        var outcome = await Run(client, recipe, sections: "altText");

        Assert.False(outcome.Succeeded);
        Assert.Contains("not one of the recipe's images", outcome.FailureSummary!, StringComparison.Ordinal);
    }

    // ---- internal links are grounded in the workspace's own recipes --------------------------------------

    [Fact]
    public async Task Only_the_workspaces_other_approved_recipes_are_offered_as_link_candidates()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));

        await Run(client, recipe, sections: "seoTitle,internalLinks");

        var message = UserMessage(client);
        Assert.Contains("Irish Brown Bread", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Unfinished Draft Loaf", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Other Workspace Secret Loaf", message, StringComparison.Ordinal);
        Assert.Contains(recipe.OtherRecipeId.ToString(), message, StringComparison.Ordinal);
        Assert.DoesNotContain(recipe.RecipeId.ToString(), ReferenceSegment(message), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_candidates_are_sent_when_no_link_ideas_were_asked_for()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));

        await Run(client, recipe, sections: "seoTitle");

        Assert.DoesNotContain("Irish Brown Bread", UserMessage(client), StringComparison.Ordinal);
    }

    /// <summary>A candidate reopened while the model ran is a stale link idea, exactly as a moved source is a stale package.</summary>
    [Fact]
    public async Task A_linked_recipe_that_is_reopened_while_the_model_runs_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer($$""" "internalLinks":[{"recipeId":"{{recipe.OtherRecipeId}}","anchorText":"Irish brown bread","reason":"Both are quick breads."}] """));
        client.BeforeAnswering = () => SetStatusAsync(recipe.OtherRecipeId, RecipeStatus.InDevelopment);

        var outcome = await Run(client, recipe, sections: "internalLinks");

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Proposal);
        Assert.Contains("no longer approved", outcome.FailureSummary!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_number_of_link_candidates_is_stated_to_the_model_rather_than_inferred_from_absence()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));

        await Run(client, recipe, sections: "seoTitle,internalLinks");

        Assert.Contains("\"linkCandidates\":1", UserMessage(client), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("other-workspace")]
    [InlineData("unapproved")]
    [InlineData("self")]
    [InlineData("invented")]
    public async Task A_link_to_a_recipe_that_was_not_offered_fails_the_operation(string target)
    {
        var recipe = await SeedRecipeAsync();
        var id = target switch
        {
            "other-workspace" => recipe.OtherWorkspaceRecipeId,
            "unapproved" => recipe.DraftRecipeId,
            "self" => recipe.RecipeId,
            _ => Guid.NewGuid(),
        };
        var client = FakeChatClient.Returning(Answer($$""" "internalLinks":[{"recipeId":"{{id}}","anchorText":"Try this too","reason":"Related."}] """));

        var outcome = await Run(client, recipe, sections: "internalLinks");

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
        Assert.Contains("was not offered", outcome.FailureSummary!, StringComparison.Ordinal);
    }

    // ---- the rules and the request -------------------------------------------------------------------------

    /// <summary>The membership check lives in the validator, so the gateway's one corrective re-ask applies to an invented id.</summary>
    [Fact]
    public async Task A_first_answer_linking_a_recipe_that_was_not_offered_is_corrected_on_the_re_ask()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Sequence(
            Answer($$""" "internalLinks":[{"recipeId":"{{recipe.OtherWorkspaceRecipeId}}","anchorText":"Try this","reason":"Related."}] """),
            Answer($$""" "internalLinks":[{"recipeId":"{{recipe.OtherRecipeId}}","anchorText":"Irish brown bread","reason":"Both are quick breads."}] """));

        var outcome = await Run(client, recipe, sections: "internalLinks");

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        Assert.Equal(2, outcome.Attempts.Count);
    }

    [Fact]
    public async Task Key_phrases_and_link_ideas_always_carry_a_standing_finding_that_no_search_data_was_used()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(Answer(Everything(recipe))), recipe);

        Assert.Contains(outcome.Proposal!.Warnings, w => w.Message.Contains(AiSeoClaimScanner.KeyPhrasesNotFromSearchData, StringComparison.Ordinal) && w.AiStructuredChangeId is not null);
        Assert.Contains(outcome.Proposal.Warnings, w => w.Message.Contains(AiSeoClaimScanner.LinkIdeasNotChecked, StringComparison.Ordinal) && w.AiStructuredChangeId is not null);
    }

    [Fact]
    public async Task Every_model_warning_is_labelled_so_it_is_not_mistaken_for_a_server_finding()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(Answer(
            "\"seoTitle\":{\"text\":\"" + Title + "\"}",
            "\"warnings\":[{\"kind\":\"Limitation\",\"message\":\"No voice guide was supplied.\"}]")), recipe, sections: "seoTitle");

        Assert.Contains(outcome.Proposal!.Warnings, w => w.Message == AiPolicy.ModelWarningLabel + "No voice guide was supplied.");
    }

    [Fact]
    public async Task A_rule_set_mismatch_names_both_versions_so_divergent_hosts_are_visible()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));

        var outcome = await Run(client, recipe, sections: "seoTitle", ruleSet: "seo.rules/1-00000000");

        Assert.Contains("seo.rules/1-00000000", outcome.FailureSummary!, StringComparison.Ordinal);
        Assert.Contains(Rules.Version, outcome.FailureSummary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_title_of_emoji_is_measured_in_characters_a_reader_counts_not_code_units()
    {
        var recipe = await SeedRecipeAsync();
        var sixty = string.Concat(Enumerable.Repeat("\uD83C\uDF5E", 60));

        var within = await Run(FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + sixty + "\"}")), recipe, sections: "seoTitle");
        var over = await Run(FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + sixty + "\uD83C\uDF5E\"}")), recipe, sections: "seoTitle");

        Assert.True(within.Succeeded, within.FailureSummary);
        Assert.False(over.Succeeded);
    }

    [Fact]
    public async Task A_rule_set_that_changed_since_the_request_is_refused_before_the_model_is_called()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));

        var outcome = await Run(client, recipe, sections: "seoTitle", ruleSet: "seo.rules/1-00000000");

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
    }

    [Fact]
    public async Task A_section_the_request_did_not_ask_for_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer($$""" "seoTitle":{"text":"{{Title}}"},"metaDescription":{"text":"{{MetaDescription}}"} """));

        var outcome = await Run(client, recipe, sections: "seoTitle");

        Assert.False(outcome.Succeeded);
        Assert.Contains("did not ask for", outcome.FailureSummary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_operation_naming_no_recipe_or_only_a_slug_is_refused()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));

        var recipeless = await Run(client, recipe, recipeless: true);
        var slugOnly = await Run(client, recipe, sections: "slug");

        Assert.Equal(AiFailureCategory.Validation, recipeless.FailureCategory);
        Assert.Equal(AiFailureCategory.Validation, slugOnly.FailureCategory);
        Assert.Null(client.LastMessages);
    }

    // ---- stale source ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_recipe_that_has_a_newer_version_is_refused_before_the_model_is_called()
    {
        var recipe = await SeedRecipeAsync();
        await AddNewerVersionAsync(recipe);
        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));

        var outcome = await Run(client, recipe, sections: "seoTitle");

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
    }

    [Fact]
    public async Task A_recipe_that_moves_while_the_model_runs_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));
        client.BeforeAnswering = () => AddNewerVersionAsync(recipe);

        var outcome = await Run(client, recipe, sections: "seoTitle");

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Proposal);
        Assert.NotNull(client.LastMessages);
        Assert.Contains("changed", outcome.FailureSummary!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_recipe_reopened_while_the_model_runs_fails_the_operation_even_at_the_same_version()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));
        client.BeforeAnswering = () => SetStatusAsync(recipe.RecipeId, RecipeStatus.InDevelopment);

        var outcome = await Run(client, recipe, sections: "seoTitle");

        Assert.False(outcome.Succeeded);
        Assert.Contains("no longer approved", outcome.FailureSummary!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- what reaches the prompt ---------------------------------------------------------------------------

    [Fact]
    public async Task The_recipes_text_the_captions_and_the_brand_facts_reach_the_user_message_and_never_the_system_message()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));

        await Run(client, recipe, sections: "seoTitle");

        Assert.Contains(IngredientLine, UserMessage(client), StringComparison.Ordinal);
        Assert.Contains("Sliced soda bread on a board", UserMessage(client), StringComparison.Ordinal);
        Assert.Contains("Sam's Kitchen", UserMessage(client), StringComparison.Ordinal);
        Assert.DoesNotContain(IngredientLine, SystemMessage(client), StringComparison.Ordinal);
        Assert.DoesNotContain("Irish Brown Bread", SystemMessage(client), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recipe_in_another_workspace_is_invisible_to_the_handler()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"seoTitle\":{\"text\":\"" + Title + "\"}"));

        var outcome = await Run(client, recipe, workspaceId: WorkspaceB, sections: "seoTitle");

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.Validation, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
        Assert.DoesNotContain("Buttermilk", outcome.FailureSummary!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- answers -----------------------------------------------------------------------------------------

    private static string Answer(string sections, string? extra = null) =>
        $$"""{"schemaVersion":"{{Version}}","sections":{{{sections}}}{{(extra is null ? string.Empty : "," + extra)}}}""";

    private static string Everything(SeededRecipe recipe) => $$"""
        "seoTitle":{"text":"{{Title}}"},
        "metaDescription":{"text":"{{MetaDescription}}"},
        "keyPhrases":[{"phrase":"soda bread"},{"phrase":"buttermilk bread"},{"phrase":"no yeast bread"}],
        "altText":[
          {"assetLinkId":"{{recipe.CaptionedAssetId}}","text":"Sliced soda bread on a board","basis":"Caption"},
          {"assetLinkId":"{{recipe.BareAssetId}}","text":"Buttermilk soda bread","basis":"RecipeTitle"}],
        "internalLinks":[{"recipeId":"{{recipe.OtherRecipeId}}","anchorText":"Irish brown bread","reason":"Both are quick breads."}]
        """;

    // ---- helpers ---------------------------------------------------------------------------------------

    private sealed record SeededRecipe(
        Guid RecipeId, Guid VersionId, Guid CaptionedAssetId, Guid BareAssetId,
        Guid OtherRecipeId, Guid DraftRecipeId, Guid OtherWorkspaceRecipeId);

    private static string UserMessage(FakeChatClient client) =>
        client.LastMessages!.Single(message => message.Role == ChatRole.User).Text;

    private static string SystemMessage(FakeChatClient client) =>
        client.LastMessages!.Single(message => message.Role == ChatRole.System).Text;

    /// <summary>The text of the REFERENCE segment alone, so the assertion is about the candidates and not the source.</summary>
    private static string ReferenceSegment(string message)
    {
        var start = message.IndexOf("BEGIN REFERENCE", StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        var end = message.IndexOf("END REFERENCE", start, StringComparison.Ordinal);
        return message[start..(end < 0 ? message.Length : end)];
    }

    private async Task SetStatusAsync(Guid recipeId, RecipeStatus status, Guid? workspaceId = null)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId ?? WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        (await db.Recipes.SingleAsync(candidate => candidate.Id == recipeId, TestContext.Current.CancellationToken)).Status = status;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task AddNewerVersionAsync(SeededRecipe recipe)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.RecipeVersions.Add(new RecipeVersion
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceA,
            RecipeId = recipe.RecipeId,
            VersionNumber = 99,
            ParentVersionId = recipe.VersionId,
            Source = RecipeVersionSource.CreatorEdit,
            Readiness = RecipeVersionReadiness.Draft,
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
            SnapshotSchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<AiTaskHandlerOutcome> Run(
        FakeChatClient client,
        SeededRecipe recipe,
        bool recipeless = false,
        Guid? workspaceId = null,
        string sections = AllSections,
        string? ruleSet = null)
    {
        const string pipelineKey = "test-ai-seo";

        var services = new ServiceCollection();
        services.AddResiliencePipeline(pipelineKey, pipeline => pipeline
            .AddRetry(new Polly.Retry.RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is AiTransientFailureException),
            })
            .AddTimeout(new TimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(30) }));
        using var pipelines = services.BuildServiceProvider();

        var gateway = new AiCompletionGateway(
            client,
            new DefaultAiFailureClassifier(),
            new ConfiguredAiCostEstimator(new AiCostOptions()),
            new StoppedClock(),
            pipelines.GetRequiredService<ResiliencePipelineProvider<string>>(),
            new AiGatewayOptions
            {
                ResiliencePipelineKey = pipelineKey,
                ProviderName = "test-provider",
                ModelName = "test-model",
            },
            NullLogger<AiCompletionGateway>.Instance);

        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId ?? WorkspaceA);

        var handler = new SeoPackageAiTaskHandler(
            gateway,
            scope.ServiceProvider.GetRequiredService<IRecipeFacade>(),
            EmbeddedPromptTemplateStore.Load(typeof(AiPolicy).Assembly),
            Rules,
            new StoppedClock());

        var inputs = new Dictionary<string, string>
        {
            [AiSeoPackageInputs.Sections] = sections,
            [AiSeoPackageInputs.RuleSet] = ruleSet ?? Rules.Version,
            [AiSeoPackageInputs.BrandProfileRevision] = "3",
            [AiSeoPackageInputs.BrandName] = "Sam's Kitchen",
            [AiSeoPackageInputs.Audience] = "busy weeknight cooks",
        };

        var context = new AiTaskExecutionContext(
            Operation,
            workspaceId ?? WorkspaceA,
            LeaseToken: Guid.NewGuid(),
            AiOperationScope.Advisory,
            RecipeId: recipeless ? null : recipe.RecipeId,
            RecipeVersionId: recipeless ? null : recipe.VersionId,
            CorrelationId: Guid.NewGuid(),
            RenewLeaseAsync: _ => Task.CompletedTask,
            Inputs: inputs);

        return await handler.HandleAsync(context, TestContext.Current.CancellationToken);
    }

    private async Task<Guid> CreateApprovedAsync(Guid workspaceId, string title, bool approved = true)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var recipes = scope.ServiceProvider.GetRequiredService<IRecipeFacade>();

        var created = await recipes.CreateAsync(
            UserId,
            new CreateRecipeViewModel
            {
                Title = title,
                IngredientGroups = [new RecipeIngredientGroupInputViewModel { Ingredients = [new RecipeIngredientInputViewModel { DisplayText = "1 cup flour" }] }],
                Instructions = [new RecipeInstructionGroupInputViewModel { Steps = [new RecipeInstructionStepInputViewModel { Text = "Mix and bake." }] }],
            },
            idempotencyKey: null,
            TestContext.Current.CancellationToken);

        Assert.True(created.Result.Succeeded, created.Result.Error?.Message);

        if (approved)
        {
            await SetStatusAsync(created.Result.Value!.RecipeId, RecipeStatus.Approved, workspaceId);
        }

        return created.Result.Value!.RecipeId;
    }

    private async Task<SeededRecipe> SeedRecipeAsync()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var recipes = scope.ServiceProvider.GetRequiredService<IRecipeFacade>();

        var created = await recipes.CreateAsync(
            UserId,
            new CreateRecipeViewModel
            {
                Title = "Buttermilk Soda Bread",
                IngredientGroups =
                [
                    new RecipeIngredientGroupInputViewModel
                    {
                        Ingredients = [new RecipeIngredientInputViewModel { DisplayText = IngredientLine }],
                    },
                ],
                Instructions =
                [
                    new RecipeInstructionGroupInputViewModel
                    {
                        Steps = [new RecipeInstructionStepInputViewModel { Text = "Mix and bake at 220C." }],
                    },
                ],
            },
            idempotencyKey: null,
            TestContext.Current.CancellationToken);

        Assert.True(created.Result.Succeeded, created.Result.Error?.Message);
        var recipeId = created.Result.Value!.RecipeId;

        // The recipe facade has no way to attach an image, so the links are written directly; the next version
        // then snapshots them, which is what the handler reads.
        var captioned = Guid.NewGuid();
        var bare = Guid.NewGuid();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // 12.9 gave RecipeAssetLink the composite foreign key its configuration had promised, so a link
        // now has to name a real asset of this workspace.
        var asset = SeededMediaAsset.For(WorkspaceA);
        db.MediaAssets.Add(asset);

        db.RecipeAssetLinks.AddRange(
            new RecipeAssetLink { Id = captioned, WorkspaceId = WorkspaceA, RecipeId = recipeId, MediaAssetId = asset.Id, Role = RecipeAssetRole.Hero, Caption = "Sliced soda bread on a board", SortOrder = 0 },
            new RecipeAssetLink { Id = bare, WorkspaceId = WorkspaceA, RecipeId = recipeId, MediaAssetId = asset.Id, Role = RecipeAssetRole.Gallery, SortOrder = 1 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var detail = await recipes.GetDetailAsync(recipeId, TestContext.Current.CancellationToken);
        var updated = await recipes.UpdateAsync(
            UserId,
            recipeId,
            new UpdateRecipeViewModel
            {
                ExpectedConcurrencyToken = detail.Value!.ConcurrencyToken,
                Headnote = Domain.Managers.Patching.PatchField<string?>.Submitted("A dense, tangy loaf."),
            },
            idempotencyKey: null,
            TestContext.Current.CancellationToken);

        Assert.True(updated.Result.Succeeded, updated.Result.Error?.Message);
        var versionId = updated.Result.Value!.CurrentVersion!.Id;

        await SetStatusAsync(recipeId, RecipeStatus.Approved);

        var other = await CreateApprovedAsync(WorkspaceA, "Irish Brown Bread");
        var draft = await CreateApprovedAsync(WorkspaceA, "Unfinished Draft Loaf", approved: false);
        var otherWorkspace = await CreateApprovedAsync(WorkspaceB, "Other Workspace Secret Loaf");

        return new SeededRecipe(recipeId, versionId, captioned, bare, other, draft, otherWorkspace);
    }

    private static void Resolve(IServiceScope scope, Guid workspaceId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner, "test-account");

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }

    /// <summary>A scripted <see cref="IChatClient"/>: no network, no model, fully deterministic.</summary>
    private sealed class FakeChatClient : IChatClient
    {
        private Func<string>? _always;

        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public static FakeChatClient Returning(string body) => new() { _always = () => body };

        /// <summary>Answers in order, repeating the last: the first answer invalid and the second sound, to prove a corrective re-ask recovers.</summary>
        public static FakeChatClient Sequence(params string[] bodies)
        {
            var next = 0;

            return new FakeChatClient { _always = () => bodies[Math.Min(next++, bodies.Length - 1)] };
        }

        public Func<Task>? BeforeAnswering { get; set; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();

            if (BeforeAnswering is not null)
            {
                await BeforeAnswering();
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, _always!())) { ModelId = "test-model" };
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The gateway does not stream.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
