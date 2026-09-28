using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Recipes;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// AIREC-002's acceptance step over a real SQLite-backed data layer and the <em>real</em> recipe module: the
/// special transaction, replay, the creator's own rewrites, rejection, atomic rollback, and two-workspace
/// isolation.
/// </summary>
/// <remarks>
/// The recipe module is wired for real rather than stubbed, because the thing worth proving is that an
/// accepted draft goes through the same create path a typed recipe does and commits in one transaction with
/// the decision. A stub would prove the calls were made and nothing about whether they can be made together.
/// </remarks>
public sealed class AiDraftAcceptanceTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private const string UserId = "user-1";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public AiDraftAcceptanceTests()
    {
        _connection.Open();

        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddOutbox()
            .AddMeasurementModule()
            .AddVocabularyModule()
            .AddIngredientModule()
            .AddRecipesModule()
            // The recipe facade's paging reader wants a cache. In-memory: nothing here tests caching, and a
            // reader that could not be constructed would fail every test for a reason none of them is about.
            .AddLogging()
            .AddDistributedMemoryCache()
            .AddApplicationCache()
            .AddSingleton<IClock>(new StoppedClock())
            .AddSingleton(new AiTaskOptions())
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<IAiDraftAcceptanceBusiness, AiDraftAcceptanceBusiness>()
            .AddIdempotency(configuration)
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

    // ---- accepting -------------------------------------------------------------------------------------

    [Fact]
    public async Task Accepting_a_whole_draft_creates_the_recipe_it_describes()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        var result = await AcceptAllAsync(WorkspaceA, seeded);

        Assert.True(result.Succeeded, result.Error?.Message);
        var accepted = result.Value!;

        Assert.Equal(AiOperationStatus.Accepted, accepted.Status);
        Assert.NotNull(accepted.RecipeId);
        Assert.Equal(1, accepted.RecipeVersionNumber);
        Assert.False(accepted.Replayed);

        var recipe = await ReadRecipeAsync(WorkspaceA, accepted.RecipeId!.Value);
        Assert.Equal("Weeknight Mapo Tofu", recipe.Title);
        Assert.Single(recipe.IngredientGroups);
        Assert.Equal("2 tbsp doubanjiang", recipe.IngredientGroups[0].Ingredients[0].DisplayText);
        Assert.Single(recipe.InstructionGroups);
        Assert.Equal("Fry the paste.", recipe.InstructionGroups[0].Steps[0].Text);
    }

    /// <summary>Acceptance is the only create step — nothing exists until the creator says so.</summary>
    [Fact]
    public async Task A_proposed_draft_has_created_no_recipe_before_it_is_accepted()
    {
        await SeedDraftAsync(WorkspaceA);

        Assert.Equal(0, await CountRecipesAsync());
    }

    [Fact]
    public async Task The_created_recipe_is_a_draft_and_its_version_names_the_proposal()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);
        var accepted = (await AcceptAllAsync(WorkspaceA, seeded)).Value!;

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var version = await db.RecipeVersions.AsNoTracking()
            .SingleAsync(row => row.RecipeId == accepted.RecipeId, TestContext.Current.CancellationToken);

        Assert.Equal(1, version.VersionNumber);
        Assert.Equal(Domain.Modules.Recipes.Managers.RecipeVersionSource.AiProposalAccepted, version.Source);
        Assert.Equal(seeded.ProposalId, version.AiProposalId);
    }

    [Fact]
    public async Task The_operation_records_the_recipe_it_produced()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);
        var accepted = (await AcceptAllAsync(WorkspaceA, seeded)).Value!;

        var operation = await LoadOperationAsync(WorkspaceA, seeded.OperationId);

        Assert.Equal(accepted.RecipeId, operation.RecipeId);
        Assert.Equal(AiOperationStatus.Accepted, operation.Status);
        Assert.NotNull(operation.CompletedAt);
    }

    [Fact]
    public async Task Every_part_of_an_accepted_draft_is_recorded_as_taken()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);
        await AcceptAllAsync(WorkspaceA, seeded);

        var changes = await LoadChangesAsync(WorkspaceA, seeded.ProposalId);

        Assert.All(changes, change => Assert.Equal(AiChangeDisposition.Accepted, change.Disposition));
        Assert.All(changes, change => Assert.NotNull(change.DecidedAt));
        Assert.All(changes, change => Assert.NotNull(change.DecidedByMembershipId));
    }

    // ---- replay ----------------------------------------------------------------------------------------

    /// <summary>The restriction's own words: a replay creates no duplicate recipe.</summary>
    [Fact]
    public async Task Accepting_the_same_draft_twice_creates_one_recipe_and_names_it_both_times()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        var first = await AcceptAllAsync(WorkspaceA, seeded);
        var second = await AcceptAllAsync(WorkspaceA, seeded);

        Assert.True(second.Succeeded, second.Error?.Message);
        Assert.False(first.Value!.Replayed);
        Assert.True(second.Value!.Replayed);
        Assert.Equal(first.Value.RecipeId, second.Value.RecipeId);
        Assert.Equal(1, await CountRecipesAsync());
    }

    /// <summary>
    /// A retry asking for a <em>different</em> decision is a conflict, not a replay. Serving the earlier
    /// outcome would tell a creator their selection had been applied when a different one had.
    /// </summary>
    [Fact]
    public async Task A_second_different_decision_is_refused_rather_than_answered_with_the_first()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);
        await AcceptAllAsync(WorkspaceA, seeded);

        var second = await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.Reject,
        }, seeded.OperationId);

        Assert.False(second.Succeeded);
        Assert.Equal(AiDraftAcceptanceErrors.DraftDecided, second.Error!.Code);
        Assert.Equal(1, await CountRecipesAsync());
    }

    // ---- the creator's own rewrites --------------------------------------------------------------------

    [Fact]
    public async Task A_rewritten_value_goes_into_the_recipe_instead_of_the_models()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        var result = await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.AcceptAll,
            AcceptedChangeIds = seeded.ChangeIds,
            Edits =
            [
                new AiDraftFieldEditViewModel
                {
                    ChangeId = seeded.IngredientAddId,
                    Field = "displayText",
                    Value = "3 tbsp doubanjiang",
                },
            ],
        }, seeded.OperationId);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(1, result.Value!.RewrittenChangeCount);

        var recipe = await ReadRecipeAsync(WorkspaceA, result.Value.RecipeId!.Value);
        Assert.Equal("3 tbsp doubanjiang", recipe.IngredientGroups[0].Ingredients[0].DisplayText);
    }

    [Fact]
    public async Task A_rewritten_title_is_the_creators_words()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        var result = await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.AcceptAll,
            AcceptedChangeIds = seeded.ChangeIds,
            Edits =
            [
                new AiDraftFieldEditViewModel { ChangeId = seeded.TitleId, Field = "title", Value = "My Mapo Tofu" },
            ],
        }, seeded.OperationId);

        var recipe = await ReadRecipeAsync(WorkspaceA, result.Value!.RecipeId!.Value);
        Assert.Equal("My Mapo Tofu", recipe.Title);
    }

    /// <summary>A rewrite must address something the draft actually proposed, or nothing is written.</summary>
    [Fact]
    public async Task A_rewrite_naming_a_change_this_draft_does_not_contain_is_refused()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        var result = await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.AcceptAll,
            AcceptedChangeIds = seeded.ChangeIds,
            Edits = [new AiDraftFieldEditViewModel { ChangeId = Guid.NewGuid(), Field = "title", Value = "Mine" }],
        }, seeded.OperationId);

        Assert.False(result.Succeeded);
        Assert.Equal(AiDraftAcceptanceErrors.SelectionInvalid, result.Error!.Code);
        Assert.Equal(0, await CountRecipesAsync());
    }

    /// <summary>
    /// The check that stops a creator's words landing in a field they were never looking at: an ingredient
    /// line's Add row carries its display text, not a recipe-level title.
    /// </summary>
    [Fact]
    public async Task A_rewrite_naming_a_field_that_row_cannot_carry_is_refused()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        var result = await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.AcceptAll,
            AcceptedChangeIds = seeded.ChangeIds,
            Edits =
            [
                new AiDraftFieldEditViewModel { ChangeId = seeded.IngredientAddId, Field = "title", Value = "Mine" },
            ],
        }, seeded.OperationId);

        Assert.False(result.Succeeded);
        Assert.Equal(AiDraftAcceptanceErrors.SelectionInvalid, result.Error!.Code);
        Assert.Equal(0, await CountRecipesAsync());
    }

    // ---- rejecting -------------------------------------------------------------------------------------

    [Fact]
    public async Task Rejecting_a_draft_records_the_decision_and_creates_nothing()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        var result = await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.Reject,
            WasHelpful = false,
            Comment = "Not the dish I meant.",
        }, seeded.OperationId);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(AiOperationStatus.Rejected, result.Value!.Status);
        Assert.Null(result.Value.RecipeId);
        Assert.Null(result.Value.RecipeVersionNumber);
        Assert.Equal(0, await CountRecipesAsync());

        var operation = await LoadOperationAsync(WorkspaceA, seeded.OperationId);
        Assert.Equal(AiOperationStatus.Rejected, operation.Status);
        Assert.Null(operation.RecipeId);
    }

    [Fact]
    public async Task A_rejected_draft_records_every_part_as_declined()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.Reject,
        }, seeded.OperationId);

        var changes = await LoadChangesAsync(WorkspaceA, seeded.ProposalId);
        Assert.All(changes, change => Assert.Equal(AiChangeDisposition.Rejected, change.Disposition));
    }

    [Fact]
    public async Task Rejecting_the_same_draft_twice_replays()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);
        var reject = new AiDraftAcceptanceViewModel { Decision = AiDispositionDecision.Reject };

        await AcceptAsync(WorkspaceA, reject, seeded.OperationId);
        var second = await AcceptAsync(WorkspaceA, reject, seeded.OperationId);

        Assert.True(second.Succeeded, second.Error?.Message);
        Assert.True(second.Value!.Replayed);
    }

    // ---- atomic rollback -------------------------------------------------------------------------------

    /// <summary>
    /// The restriction's other half: an unresolved required field fails atomically. Accepting every part of a
    /// draft except its title describes a recipe with no title, which the create validator refuses — and the
    /// refusal must leave the draft exactly as it was, not half-decided.
    /// </summary>
    [Fact]
    public async Task Accepting_a_draft_with_no_title_writes_nothing_at_all()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);
        var withoutTitle = seeded.ChangeIds.Where(id => id != seeded.TitleId).ToList();

        var result = await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = withoutTitle,
        }, seeded.OperationId);

        Assert.False(result.Succeeded);
        Assert.Equal(0, await CountRecipesAsync());

        // Not half-decided: the operation is still awaiting a decision and no change was dispositioned.
        var operation = await LoadOperationAsync(WorkspaceA, seeded.OperationId);
        Assert.Equal(AiOperationStatus.Proposed, operation.Status);
        Assert.Null(operation.RecipeId);
        Assert.Null(operation.CompletedAt);

        var changes = await LoadChangesAsync(WorkspaceA, seeded.ProposalId);
        Assert.All(changes, change => Assert.Equal(AiChangeDisposition.Pending, change.Disposition));
    }

    /// <summary>And the creator can fix it and try again — the refusal left nothing in the way.</summary>
    [Fact]
    public async Task A_draft_refused_for_its_title_can_be_accepted_afterwards()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = seeded.ChangeIds.Where(id => id != seeded.TitleId).ToList(),
        }, seeded.OperationId);

        var second = await AcceptAllAsync(WorkspaceA, seeded);

        Assert.True(second.Succeeded, second.Error?.Message);
        Assert.Equal(1, await CountRecipesAsync());
    }

    /// <summary>
    /// The rollback that actually opens a transaction. A title over the recipe module's own bound clears the
    /// composer, so <c>CreateFromProposalAsync</c> is reached, refuses, and the whole unit unwinds — which is
    /// the only path that proves the recipe and the decision commit together or not at all.
    /// </summary>
    [Fact]
    public async Task A_draft_the_recipe_rules_refuse_writes_nothing_at_all()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        var result = await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.AcceptAll,
            AcceptedChangeIds = seeded.ChangeIds,
            Edits =
            [
                new AiDraftFieldEditViewModel
                {
                    ChangeId = seeded.TitleId,
                    Field = "title",
                    Value = new string('x', 5000),
                },
            ],
            WasHelpful = true,
            Comment = "Nearly right.",
        }, seeded.OperationId);

        Assert.False(result.Succeeded);

        // The recipe domain's own refusal, passed through unchanged rather than paraphrased.
        Assert.Equal("recipes.recipe.invalid_request", result.Error!.Code);

        await AssertNothingWrittenAsync(seeded);
    }

    /// <summary>Everything the transaction holds, asserted together — not just the recipe.</summary>
    private async Task AssertNothingWrittenAsync(SeededDraft seeded)
    {
        Assert.Equal(0, await CountRecipesAsync());

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var token = TestContext.Current.CancellationToken;

        Assert.Empty(await db.RecipeVersions.IgnoreQueryFilters().ToListAsync(token));
        Assert.Empty(await db.AiProposalFeedback.IgnoreQueryFilters().ToListAsync(token));
        Assert.Empty(await db.AuditLogs.IgnoreQueryFilters().ToListAsync(token));

        var operation = await LoadOperationAsync(WorkspaceA, seeded.OperationId);
        Assert.Equal(AiOperationStatus.Proposed, operation.Status);
        Assert.Null(operation.RecipeId);
        Assert.Null(operation.CompletedAt);

        var changes = await LoadChangesAsync(WorkspaceA, seeded.ProposalId);
        Assert.All(changes, change => Assert.Equal(AiChangeDisposition.Pending, change.Disposition));
    }

    // ---- partial acceptance ----------------------------------------------------------------------------

    [Fact]
    public async Task Accepting_part_of_a_draft_records_both_decisions_on_the_right_parts()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        // Everything but the equipment item, which a creator declining their own kit is an ordinary thing.
        var without = seeded.ChangeIds.Where(id => id != seeded.EquipmentAddId).ToList();

        var result = await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = without,
        }, seeded.OperationId);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(AiOperationStatus.PartiallyAccepted, result.Value!.Status);
        Assert.Equal(1, result.Value.RejectedChangeCount);
        Assert.Equal(without.Count, result.Value.AcceptedChangeCount);

        // Nothing was dropped this time: the only unmappable part is the one they declined.
        Assert.Equal(0, result.Value.DroppedChangeCount);

        var changes = await LoadChangesAsync(WorkspaceA, seeded.ProposalId);
        var equipment = changes.Single(change => change.Id == seeded.EquipmentAddId);
        Assert.Equal(AiChangeDisposition.Rejected, equipment.Disposition);
        Assert.All(
            changes.Where(change => change.Id != seeded.EquipmentAddId),
            change => Assert.Equal(AiChangeDisposition.Accepted, change.Disposition));
    }

    /// <summary>
    /// A rewritten part is recorded as taken, because it was: the creator's words went into the very row the
    /// change describes. See <c>AiDraftComposition</c> for why this is the opposite call from the proposal
    /// panel's, where an edited change genuinely could not be applied.
    /// </summary>
    [Fact]
    public async Task A_rewritten_part_is_still_recorded_as_accepted()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.AcceptAll,
            AcceptedChangeIds = seeded.ChangeIds,
            Edits =
            [
                new AiDraftFieldEditViewModel
                {
                    ChangeId = seeded.IngredientAddId,
                    Field = "displayText",
                    Value = "3 tbsp doubanjiang",
                },
            ],
        }, seeded.OperationId);

        var changes = await LoadChangesAsync(WorkspaceA, seeded.ProposalId);
        var rewritten = changes.Single(change => change.Id == seeded.IngredientAddId);

        Assert.Equal(AiChangeDisposition.Accepted, rewritten.Disposition);
    }

    // ---- states that cannot be decided -----------------------------------------------------------------

    /// <summary>
    /// The guard that stops another capability's proposal being accepted through this route and creating a
    /// recipe out of changes meant to patch one.
    /// </summary>
    [Fact]
    public async Task A_request_from_another_capability_cannot_be_accepted_here()
    {
        var seeded = await SeedDraftAsync(WorkspaceA, taskType: AiTaskType.RecipeConcepts);

        var result = await AcceptAllAsync(WorkspaceA, seeded);

        Assert.False(result.Succeeded);
        Assert.Equal(AiFirstDraftRequestErrors.RequestNotFound, result.Error!.Code);
        Assert.Equal(0, await CountRecipesAsync());
    }

    [Fact]
    public async Task A_request_that_produced_no_draft_has_nothing_to_decide()
    {
        var seeded = await SeedDraftAsync(WorkspaceA, withProposal: false);

        var result = await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.Reject,
        }, seeded.OperationId);

        Assert.False(result.Succeeded);
        Assert.Equal(AiDraftAcceptanceErrors.DraftNotFound, result.Error!.Code);
    }

    /// <summary>Still running: waiting is the remedy, which is a different answer from "already decided".</summary>
    [Theory]
    [InlineData(AiOperationStatus.Requested)]
    [InlineData(AiOperationStatus.Running)]
    public async Task A_draft_that_is_not_ready_yet_cannot_be_decided(AiOperationStatus status)
    {
        var seeded = await SeedDraftAsync(WorkspaceA, status: status);

        var result = await AcceptAllAsync(WorkspaceA, seeded);

        Assert.False(result.Succeeded);
        Assert.Equal(AiDraftAcceptanceErrors.DraftDecided, result.Error!.Code);
        Assert.Equal(0, await CountRecipesAsync());
    }

    /// <summary>
    /// The replay comparison's other half: two different partial selections both compute
    /// <c>PartiallyAccepted</c>, so the status check passes and only the set comparison refuses.
    /// </summary>
    [Fact]
    public async Task A_second_partial_acceptance_naming_different_parts_is_refused()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);
        var first = seeded.ChangeIds.Where(id => id != seeded.EquipmentAddId).ToList();
        var second = seeded.ChangeIds.Where(id => id != seeded.IngredientAddId).ToList();

        await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = first,
        }, seeded.OperationId);

        var result = await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = second,
        }, seeded.OperationId);

        Assert.False(result.Succeeded);
        Assert.Equal(AiDraftAcceptanceErrors.DraftDecided, result.Error!.Code);
        Assert.Equal(1, await CountRecipesAsync());
    }

    /// <summary>A replay has to be able to name the version it made, not only the recipe.</summary>
    [Fact]
    public async Task A_replay_names_version_one_as_well_as_the_recipe()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        await AcceptAllAsync(WorkspaceA, seeded);
        var replay = await AcceptAllAsync(WorkspaceA, seeded);

        Assert.True(replay.Value!.Replayed);
        Assert.Equal(1, replay.Value.RecipeVersionNumber);
    }

    /// <summary>
    /// Nothing stores what the creator's words were, only that some were theirs — so a retry carrying
    /// rewrites cannot be shown to match the decision already recorded. Refused rather than answered with a
    /// success that would quietly discard them.
    /// </summary>
    [Fact]
    public async Task A_replay_carrying_rewrites_is_refused_rather_than_silently_discarding_them()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);
        var withEdit = new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.AcceptAll,
            AcceptedChangeIds = seeded.ChangeIds,
            Edits =
            [
                new AiDraftFieldEditViewModel
                {
                    ChangeId = seeded.IngredientAddId,
                    Field = "displayText",
                    Value = "3 tbsp doubanjiang",
                },
            ],
        };

        var first = await AcceptAsync(WorkspaceA, withEdit, seeded.OperationId);
        var second = await AcceptAsync(WorkspaceA, withEdit, seeded.OperationId);

        Assert.True(first.Succeeded, first.Error?.Message);
        Assert.False(second.Succeeded);
        Assert.Equal(AiDraftAcceptanceErrors.DraftDecided, second.Error!.Code);
        Assert.Equal(1, await CountRecipesAsync());
    }

    /// <summary>A rewrite of a part the creator is not accepting is a client that has lost its own request.</summary>
    [Fact]
    public async Task A_rewrite_of_a_part_that_is_not_being_accepted_is_refused()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        var result = await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = seeded.ChangeIds.Where(id => id != seeded.IngredientAddId).ToList(),
            Edits =
            [
                new AiDraftFieldEditViewModel
                {
                    ChangeId = seeded.IngredientAddId,
                    Field = "displayText",
                    Value = "3 tbsp doubanjiang",
                },
            ],
        }, seeded.OperationId);

        Assert.False(result.Succeeded);
        Assert.Equal(AiDraftAcceptanceErrors.SelectionInvalid, result.Error!.Code);
        Assert.Equal(0, await CountRecipesAsync());
    }

    // ---- feedback and audit ----------------------------------------------------------------------------

    [Fact]
    public async Task Feedback_is_recorded_alongside_the_decision()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.AcceptAll,
            AcceptedChangeIds = seeded.ChangeIds,
            WasHelpful = true,
            Comment = "  Close enough to work from.  ",
        }, seeded.OperationId);

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var feedback = await db.AiProposalFeedback.AsNoTracking()
            .SingleAsync(row => row.AiProposalId == seeded.ProposalId, TestContext.Current.CancellationToken);

        Assert.True(feedback.WasHelpful);
        Assert.Equal("Close enough to work from.", feedback.Comment);
        Assert.Equal(WorkspaceA, feedback.WorkspaceId);
    }

    [Fact]
    public async Task No_feedback_row_is_written_when_the_creator_left_none()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        await AcceptAllAsync(WorkspaceA, seeded);

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        Assert.Empty(await db.AiProposalFeedback.AsNoTracking()
            .Where(row => row.AiProposalId == seeded.ProposalId)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// ai.md forbids generated creator content in the audit log. The summary carries counts and two status
    /// names, and the draft's own words appear nowhere in it.
    /// </summary>
    [Fact]
    public async Task The_audit_entry_records_counts_and_never_the_drafts_content()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        await AcceptAllAsync(WorkspaceA, seeded);

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var audit = await db.AuditLogs.AsNoTracking()
            .SingleAsync(row => row.ResourceId == seeded.ProposalId.ToString("D"), TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceA, audit.WorkspaceId);
        Assert.DoesNotContain("Weeknight Mapo Tofu", audit.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("doubanjiang", audit.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Fry the paste", audit.Summary, StringComparison.Ordinal);
        Assert.Contains("rewritten", audit.Summary, StringComparison.Ordinal);
    }

    // ---- selection -------------------------------------------------------------------------------------

    [Fact]
    public async Task An_accept_all_that_does_not_name_every_part_is_refused()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        var result = await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.AcceptAll,
            AcceptedChangeIds = [seeded.TitleId],
        }, seeded.OperationId);

        Assert.False(result.Succeeded);
        Assert.Equal(AiDraftAcceptanceErrors.SelectionInvalid, result.Error!.Code);
        Assert.Equal(0, await CountRecipesAsync());
    }

    [Fact]
    public async Task A_selection_naming_a_part_of_another_draft_is_refused()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        var result = await AcceptAsync(WorkspaceA, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = [Guid.NewGuid()],
        }, seeded.OperationId);

        Assert.False(result.Succeeded);
        Assert.Equal(AiDraftAcceptanceErrors.SelectionInvalid, result.Error!.Code);
    }

    /// <summary>
    /// Equipment is a real part of a draft with nowhere to be created — reported, never silently lost.
    /// </summary>
    [Fact]
    public async Task Accepted_equipment_is_reported_as_dropped_rather_than_lost()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        var result = await AcceptAllAsync(WorkspaceA, seeded);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(1, result.Value!.DroppedChangeCount);
    }

    // ---- authorization ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_viewer_may_not_accept_a_draft()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        var result = await AcceptAllAsync(WorkspaceA, seeded, WorkspaceRole.Viewer);

        Assert.False(result.Succeeded);
        Assert.Equal(AiDraftAcceptanceErrors.AcceptanceForbidden, result.Error!.Code);
        Assert.Equal(0, await CountRecipesAsync());
    }

    // ---- workspace isolation ---------------------------------------------------------------------------

    /// <summary>
    /// Workspace B naming workspace A's draft. The global query filter makes the operation invisible, and the
    /// seam reports it exactly as it reports a typo — so no recipe is created anywhere.
    /// </summary>
    [Fact]
    public async Task A_draft_belonging_to_another_workspace_cannot_be_accepted()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        var result = await AcceptAllAsync(WorkspaceB, seeded);

        Assert.False(result.Succeeded);
        Assert.Equal(AiFirstDraftRequestErrors.RequestNotFound, result.Error!.Code);
        Assert.Equal(0, await CountRecipesAsync());
    }

    /// <summary>And the same draft accepts perfectly well for the workspace that owns it.</summary>
    [Fact]
    public async Task The_same_draft_accepts_for_the_workspace_that_owns_it()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);

        var result = await AcceptAllAsync(WorkspaceA, seeded);

        Assert.True(result.Succeeded, result.Error?.Message);
    }

    /// <summary>
    /// The selection check doing isolation work rather than shape work: workspace B accepting <em>its own</em>
    /// draft while naming parts of workspace A's. A fabricated id proves only that unknown ids are refused.
    /// </summary>
    [Fact]
    public async Task A_selection_naming_another_workspaces_real_parts_is_refused()
    {
        var theirs = await SeedDraftAsync(WorkspaceA);
        var mine = await SeedDraftAsync(WorkspaceB);

        var result = await AcceptAsync(WorkspaceB, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.AcceptSelected,
            AcceptedChangeIds = theirs.ChangeIds,
        }, mine.OperationId);

        Assert.False(result.Succeeded);
        Assert.Equal(AiDraftAcceptanceErrors.SelectionInvalid, result.Error!.Code);
        Assert.Equal(0, await CountRecipesAsync());
    }

    /// <summary>The same, through the rewrite path, which refuses for its own reason.</summary>
    [Fact]
    public async Task A_rewrite_naming_another_workspaces_real_part_is_refused()
    {
        var theirs = await SeedDraftAsync(WorkspaceA);
        var mine = await SeedDraftAsync(WorkspaceB);

        var result = await AcceptAsync(WorkspaceB, new AiDraftAcceptanceViewModel
        {
            Decision = AiDispositionDecision.AcceptAll,
            AcceptedChangeIds = mine.ChangeIds,
            Edits =
            [
                new AiDraftFieldEditViewModel
                {
                    ChangeId = theirs.IngredientAddId,
                    Field = "displayText",
                    Value = "Mine now",
                },
            ],
        }, mine.OperationId);

        Assert.False(result.Succeeded);
        Assert.Equal(AiDraftAcceptanceErrors.SelectionInvalid, result.Error!.Code);
        Assert.Equal(0, await CountRecipesAsync());
    }

    /// <summary>
    /// A replay is answered with the recipe the first acceptance created — so a neighbour retrying against
    /// that same operation must not be handed it. They get the ordinary absence instead.
    /// </summary>
    [Fact]
    public async Task Another_workspace_replaying_an_accepted_draft_is_told_it_does_not_exist()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);
        var accepted = (await AcceptAllAsync(WorkspaceA, seeded)).Value!;

        var result = await AcceptAllAsync(WorkspaceB, seeded);

        Assert.False(result.Succeeded);
        Assert.Equal(AiFirstDraftRequestErrors.RequestNotFound, result.Error!.Code);
        Assert.NotNull(accepted.RecipeId);
        Assert.Equal(1, await CountRecipesAsync());
    }

    [Fact]
    public async Task The_created_recipe_belongs_to_the_accepting_workspace()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);
        var accepted = (await AcceptAllAsync(WorkspaceA, seeded)).Value!;

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var recipe = await db.Recipes.AsNoTracking()
            .SingleAsync(row => row.Id == accepted.RecipeId, TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceA, recipe.WorkspaceId);
    }

    [Fact]
    public async Task A_recipe_created_in_one_workspace_is_invisible_to_another()
    {
        var seeded = await SeedDraftAsync(WorkspaceA);
        var accepted = (await AcceptAllAsync(WorkspaceA, seeded)).Value!;

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceB);
        var recipes = scope.ServiceProvider.GetRequiredService<IRecipeFacade>();

        var read = await recipes.GetDetailAsync(accepted.RecipeId!.Value, TestContext.Current.CancellationToken);

        Assert.False(read.Succeeded);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private Task<Domain.Managers.Results.OperationResult<AiDraftAcceptanceServiceModel>> AcceptAllAsync(
        Guid workspaceId, SeededDraft seeded, WorkspaceRole role = WorkspaceRole.Contributor) =>
        AcceptAsync(
            workspaceId,
            new AiDraftAcceptanceViewModel
            {
                Decision = AiDispositionDecision.AcceptAll,
                AcceptedChangeIds = seeded.ChangeIds,
            },
            seeded.OperationId,
            role);

    private async Task<Domain.Managers.Results.OperationResult<AiDraftAcceptanceServiceModel>> AcceptAsync(
        Guid workspaceId,
        AiDraftAcceptanceViewModel model,
        Guid operationId,
        WorkspaceRole role = WorkspaceRole.Contributor)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId, role);

        return await scope.ServiceProvider.GetRequiredService<IAiDraftAcceptanceBusiness>()
            .AcceptAsync(UserId, operationId, model, TestContext.Current.CancellationToken);
    }

    private async Task<Domain.Modules.Recipes.Managers.RecipeDetailServiceModel> ReadRecipeAsync(
        Guid workspaceId, Guid recipeId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        var read = await scope.ServiceProvider.GetRequiredService<IRecipeFacade>()
            .GetDetailAsync(recipeId, TestContext.Current.CancellationToken);

        Assert.True(read.Succeeded, read.Error?.Message);
        return read.Value!;
    }

    /// <summary>Counted across every workspace, so "no recipe was created" means exactly that.</summary>
    private async Task<int> CountRecipesAsync()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.Recipes.IgnoreQueryFilters()
            .CountAsync(TestContext.Current.CancellationToken);
    }

    private async Task<AiOperation> LoadOperationAsync(Guid workspaceId, Guid operationId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.AiOperations.AsNoTracking()
            .SingleAsync(operation => operation.Id == operationId, TestContext.Current.CancellationToken);
    }

    private async Task<List<AiStructuredChange>> LoadChangesAsync(Guid workspaceId, Guid proposalId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.AiStructuredChanges.AsNoTracking()
            .Where(change => change.AiProposalId == proposalId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private sealed record SeededDraft(
        Guid OperationId,
        Guid ProposalId,
        Guid TitleId,
        Guid IngredientAddId,
        Guid EquipmentAddId,
        IReadOnlyList<Guid> ChangeIds);

    /// <summary>
    /// A completed first-draft request, in the shape <c>RecipeFirstDraftAiTaskHandler.Translate</c> produces:
    /// recipe-level <c>Set</c> rows with no target id, then one <c>Add</c> per group, line, step and equipment
    /// item with its own <c>Set</c> rows addressed to it.
    /// </summary>
    private async Task<SeededDraft> SeedDraftAsync(
        Guid workspaceId,
        AiTaskType taskType = AiTaskType.RecipeFirstDraft,
        AiOperationStatus status = AiOperationStatus.Proposed,
        bool withProposal = true)
    {
        var operationId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();

        var ingredientGroupTarget = Guid.NewGuid();
        var lineTarget = Guid.NewGuid();
        var instructionGroupTarget = Guid.NewGuid();
        var stepTarget = Guid.NewGuid();
        var equipmentTarget = Guid.NewGuid();

        var order = 0;
        var changes = new List<AiStructuredChange>();

        AiStructuredChange Add(
            AiChangeKind kind,
            AiChangeTargetKind target,
            Guid? targetId,
            string? field,
            string? value,
            int? position = null)
        {
            var change = new AiStructuredChange
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                AiProposalId = proposalId,
                ChangeKind = kind,
                TargetKind = target,
                TargetId = targetId,
                FieldName = field,
                AfterValue = value,
                ProposedPosition = position,
                SortOrder = order++,
            };

            changes.Add(change);
            return change;
        }

        var title = Add(AiChangeKind.Set, AiChangeTargetKind.Recipe, null, "title", "Weeknight Mapo Tofu");
        Add(AiChangeKind.Set, AiChangeTargetKind.Recipe, null, "description", "A quick version.");
        Add(AiChangeKind.Set, AiChangeTargetKind.Recipe, null, "prepTimeMinutes", "10");
        Add(AiChangeKind.Set, AiChangeTargetKind.Recipe, null, "yieldText", "Serves 4");

        // Untitled, which recipes.md makes the ordinary case — grouping is opt-in. An Add row carries its
        // position, which is also what satisfies CK_AiStructuredChanges_HasAValue when there is no title:
        // the handler sets it for exactly this reason, and a seed that omitted it would be testing a shape
        // the handler never produces.
        Add(AiChangeKind.Add, AiChangeTargetKind.IngredientGroup, ingredientGroupTarget, null, null, 0);
        var line = Add(AiChangeKind.Add, AiChangeTargetKind.Ingredient, lineTarget, null, "2 tbsp doubanjiang", 0);
        Add(AiChangeKind.Set, AiChangeTargetKind.Ingredient, lineTarget, "ingredientNameText", "doubanjiang");
        Add(AiChangeKind.Set, AiChangeTargetKind.Ingredient, lineTarget, "unitText", "tbsp");
        Add(AiChangeKind.Set, AiChangeTargetKind.Ingredient, lineTarget, "quantity", "2");

        Add(AiChangeKind.Add, AiChangeTargetKind.InstructionGroup, instructionGroupTarget, null, null, 0);
        Add(AiChangeKind.Add, AiChangeTargetKind.InstructionStep, stepTarget, null, "Fry the paste.", 0);
        Add(AiChangeKind.Set, AiChangeTargetKind.InstructionStep, stepTarget, "durationMinutes", "2");

        // The one part with nowhere to be created. Kept in the seed deliberately: every real draft has some.
        var equipment = Add(AiChangeKind.Add, AiChangeTargetKind.Equipment, equipmentTarget, null, "Wok", 0);

        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AiOperations.Add(new AiOperation
        {
            Id = operationId,
            WorkspaceId = workspaceId,
            TaskType = taskType,
            Scope = AiOperationScope.NotApplicable,
            Status = status,
            IdempotencyKey = $"draft-{operationId}",
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = Now,
            StatusChangedAt = Now,
            AvailableAt = Now,

            // CK_AiOperations_Running_HasStarted: a Running operation records when work began. Honoured here
            // so the seed builds rows the lifecycle can actually produce, rather than shapes it forbids.
            StartedAt = status is AiOperationStatus.Running ? Now : null,
        });

        if (withProposal)
        {
            db.AiProposals.Add(new AiProposal
            {
                Id = proposalId,
                WorkspaceId = workspaceId,
                AiOperationId = operationId,
                OutputSchemaVersion = "recipe.first-draft.v1",
                PromptTemplateId = AiTaskCatalog.RecipeFirstDraft,
                PromptTemplateVersion = "1.0.0",
                PromptTemplateBodyChecksum = "sha256:seed",
                ProviderName = "test-provider",
                ModelName = "test-model",
                CreatedAt = Now,
            });

            db.AiStructuredChanges.AddRange(changes);
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new SeededDraft(
            operationId, proposalId, title.Id, line.Id, equipment.Id, [.. changes.Select(change => change.Id)]);
    }

    private static void Resolve(
        IServiceScope scope, Guid workspaceId, WorkspaceRole role = WorkspaceRole.Contributor) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            role);

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }
}
