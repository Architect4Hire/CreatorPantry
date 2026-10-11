using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Recipes;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary;
using CreatorPantry.Tests.Reference;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// "What has this workspace already made for this themed day" (AF.7.2) over real modules and a real schema,
/// for two workspaces: what counts as an outcome, the order, the bounds, a retired theme, the cache and what
/// retires it, and the boundary between workspaces.
/// </summary>
/// <remarks>
/// Nothing is faked but the clock and the cache store. The cache is the recording one so a test can see which
/// keys were touched and prove a read was served from it; everything that decides what the history says is the
/// production code reading production tables.
/// </remarks>
public sealed class ThemedDayHistoryTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);

    private const string UserId = "user-1";
    private const string Monday = "meat-free-monday";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeApplicationCache _cache = new();
    private readonly MovableClock _clock = new(Now);
    private readonly ServiceProvider _provider;

    public ThemedDayHistoryTests()
    {
        _connection.Open();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Idempotency:FingerprintKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddOutbox()
            .AddApplicationTime()
            .AddMeasurementModule()
            .AddVocabularyModule()
            .AddIngredientModule()
            .AddRecipesModule()
            .AddContentModule()
            .AddMediaModule()
            .AddLogging()
            .AddSingleton<IApplicationCache>(_cache)
            .AddSingleton<IClock>(_clock)

            // The Ai module's two narrow lookups and nothing else from it: no gateway, no worker, no model.
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiProposalLookupBusiness, AiProposalLookupBusiness>()
            .AddScoped<IAiProposalLookupFacade, AiProposalLookupFacade>()
            .AddScoped<IAiConceptLookupBusiness, AiConceptLookupBusiness>()
            .AddScoped<IAiConceptLookupFacade, AiConceptLookupFacade>()
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- What counts, and in what order ---------------------------------------------------------------

    [Fact]
    public async Task Work_that_came_to_something_is_listed_newest_outcome_first()
    {
        await ThemeAsync(WorkspaceA, Monday);

        var pictured = await ContextAsync(WorkspaceA, "Pictured");
        var assetId = await KeepPictureAsync(WorkspaceA, pictured, Now.AddDays(-3));

        var drafted = await ContextAsync(WorkspaceA, "Drafted");
        var recipeId = await AcceptDraftAsync(WorkspaceA, drafted, Now.AddDays(-2));

        var posted = await ContextAsync(WorkspaceA, "Posted");
        await AcceptPostAsync(WorkspaceA, posted, "instagram");
        await AcceptPostAsync(WorkspaceA, posted, "pinterest");

        // Work that came to nothing is not history, however recently it was touched.
        await ContextAsync(WorkspaceA, "Abandoned");

        var page = await ReadAsync(WorkspaceA, Monday);

        Assert.Equal(["Posted", "Drafted", "Pictured"], page.Items.Select(item => item.WorkingTitle));
        Assert.Null(page.NextCursor);

        var posts = Assert.Single(page.Items[0].Outcomes);
        Assert.Equal(ThemedDayOutcomeKind.AcceptedPosts, posts.Kind);
        Assert.Equal(["instagram", "pinterest"], posts.ChannelKeys);
        Assert.False(posts.NeedsReview);
        Assert.Equal(_clock.UtcNow, page.Items[0].LatestOutcomeAt);

        var draft = Assert.Single(page.Items[1].Outcomes);
        Assert.Equal(ThemedDayOutcomeKind.AcceptedRecipeDraft, draft.Kind);
        Assert.Equal(recipeId, draft.RecipeId);
        Assert.Equal(Now.AddDays(-2), draft.At);

        var picture = Assert.Single(page.Items[2].Outcomes);
        Assert.Equal(ThemedDayOutcomeKind.KeptPicture, picture.Kind);
        Assert.Equal(assetId, picture.MediaAssetId);
        Assert.Equal(pictured, page.Items[2].ContextId);
        Assert.Equal(DayOfWeek.Monday, page.Items[2].Day);
    }

    [Fact]
    public async Task One_piece_of_work_lists_everything_it_came_to_and_sorts_by_the_newest()
    {
        await ThemeAsync(WorkspaceA, Monday);

        var both = await ContextAsync(WorkspaceA, "Both");
        await KeepPictureAsync(WorkspaceA, both, Now.AddDays(-10));
        await AcceptDraftAsync(WorkspaceA, both, Now.AddDays(-1));

        var middle = await ContextAsync(WorkspaceA, "Middle");
        await KeepPictureAsync(WorkspaceA, middle, Now.AddDays(-5));

        var page = await ReadAsync(WorkspaceA, Monday);

        // Its old picture alone would put it last; its newer draft puts it first.
        Assert.Equal(["Both", "Middle"], page.Items.Select(item => item.WorkingTitle));
        Assert.Equal(
            [ThemedDayOutcomeKind.AcceptedRecipeDraft, ThemedDayOutcomeKind.KeptPicture],
            page.Items[0].Outcomes.Select(outcome => outcome.Kind));
        Assert.Equal(Now.AddDays(-1), page.Items[0].LatestOutcomeAt);
    }

    [Fact]
    public async Task Only_what_the_creator_actually_did_is_an_outcome()
    {
        await ThemeAsync(WorkspaceA, Monday);

        // A recipe written by hand, and one whose draft was proposed or rejected but never accepted.
        var handWritten = await ContextAsync(WorkspaceA, "Hand-written");
        await ReferRecipeAsync(WorkspaceA, handWritten, await RecipeAsync(WorkspaceA));

        var proposed = await ContextAsync(WorkspaceA, "Proposed");
        await AcceptDraftAsync(WorkspaceA, proposed, Now.AddDays(-1), AiOperationStatus.Proposed);

        var rejected = await ContextAsync(WorkspaceA, "Rejected");
        await AcceptDraftAsync(WorkspaceA, rejected, Now.AddDays(-1), AiOperationStatus.Rejected);

        // A picture the work only takes cues from.
        var cue = await ContextAsync(WorkspaceA, "Cue");
        await KeepPictureAsync(WorkspaceA, cue, Now.AddDays(-1), CreativeContextReferencePurpose.Source);

        // A post that was drafted and never accepted.
        var draftedPost = await ContextAsync(WorkspaceA, "Drafted post");
        await DraftPostAsync(WorkspaceA, draftedPost, "instagram");

        // Work on another theme altogether.
        await ThemeAsync(WorkspaceA, Monday, "taco-tuesday");
        var tuesday = await ContextAsync(WorkspaceA, "Tuesday", themeKey: "taco-tuesday");
        await KeepPictureAsync(WorkspaceA, tuesday, Now.AddDays(-1));

        // And one partial acceptance, which is an acceptance.
        var partial = await ContextAsync(WorkspaceA, "Partial");
        await AcceptDraftAsync(WorkspaceA, partial, Now.AddDays(-1), AiOperationStatus.PartiallyAccepted);

        var page = await ReadAsync(WorkspaceA, Monday);

        Assert.Equal(["Partial"], page.Items.Select(item => item.WorkingTitle));
    }

    [Fact]
    public async Task An_archived_piece_of_work_is_still_history()
    {
        await ThemeAsync(WorkspaceA, Monday);
        var archived = await ContextAsync(WorkspaceA, "Archived", archivedAt: Now.AddDays(-1));
        await KeepPictureAsync(WorkspaceA, archived, Now.AddDays(-2));

        var item = Assert.Single((await ReadAsync(WorkspaceA, Monday)).Items);

        Assert.True(item.IsArchived);
    }

    // ---- Bounds -----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_outcome_older_than_the_window_is_not_recent_history()
    {
        await ThemeAsync(WorkspaceA, Monday);

        var old = await ContextAsync(WorkspaceA, "Old");
        await KeepPictureAsync(WorkspaceA, old, Now.AddDays(-(ThemedDayHistoryPolicy.MaxAgeDays + 1)));

        var edge = await ContextAsync(WorkspaceA, "Edge");
        await KeepPictureAsync(WorkspaceA, edge, Now.AddDays(-ThemedDayHistoryPolicy.MaxAgeDays));

        // One outcome inside the window and one outside it: listed, with only the one that is recent.
        var mixed = await ContextAsync(WorkspaceA, "Mixed");
        await KeepPictureAsync(WorkspaceA, mixed, Now.AddDays(-(ThemedDayHistoryPolicy.MaxAgeDays + 30)));
        await AcceptDraftAsync(WorkspaceA, mixed, Now.AddDays(-7));

        var page = await ReadAsync(WorkspaceA, Monday);

        Assert.Equal(["Mixed", "Edge"], page.Items.Select(item => item.WorkingTitle));
        Assert.Equal(ThemedDayOutcomeKind.AcceptedRecipeDraft, Assert.Single(page.Items[0].Outcomes).Kind);
    }

    [Fact]
    public async Task The_history_is_capped_and_paged_by_a_cursor()
    {
        await ThemeAsync(WorkspaceA, Monday);
        var assetId = await AssetAsync(WorkspaceA);

        // Five more than the cap, the i-th kept i minutes ago: the newest fifty are the history.
        for (var index = 0; index < ThemedDayHistoryPolicy.MaxItems + 5; index++)
        {
            var contextId = await ContextAsync(WorkspaceA, $"Work {index:00}");
            await ReferAsync(WorkspaceA, contextId, CreativeContextReferenceKind.DamAsset, Now.AddMinutes(-index), reference =>
            {
                reference.MediaAssetId = assetId;
                reference.Purpose = CreativeContextReferencePurpose.Keeper;
            });
        }

        var seen = new List<string?>();
        string? cursor = null;
        var pages = 0;

        do
        {
            // Asked for far more than a page may hold: clamped, not refused.
            var page = await ReadAsync(WorkspaceA, Monday, cursor, limit: 500);

            Assert.True(page.Items.Count <= ThemedDayHistoryPolicy.MaxPageSize);
            seen.AddRange(page.Items.Select(item => item.WorkingTitle));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(2, pages);
        Assert.Equal(
            Enumerable.Range(0, ThemedDayHistoryPolicy.MaxItems).Select(index => $"Work {index:00}"),
            seen);

        // An absent limit is the default page.
        Assert.Equal(ThemedDayHistoryPolicy.DefaultPageSize, (await ReadAsync(WorkspaceA, Monday)).Items.Count);
    }

    [Fact]
    public async Task Only_the_most_recently_touched_work_is_looked_at()
    {
        await ThemeAsync(WorkspaceA, Monday);

        // The one piece of work with an outcome is the least recently touched of more than a scan may read.
        var buried = await ContextAsync(WorkspaceA, "Buried", updatedAt: Now.AddDays(-30));
        await KeepPictureAsync(WorkspaceA, buried, Now.AddDays(-30));

        await DbAsync(WorkspaceA, async db =>
        {
            for (var index = 0; index < ThemedDayHistoryPolicy.MaxCandidates; index++)
            {
                var context = CreativeContextSeeds.NewContext(Now.AddMinutes(-index), $"Newer {index}");
                context.WeeklyThemeKey = Monday;
                db.CreativeContexts.Add(context);
            }

            return await db.SaveChangesAsync(Ct);
        });

        Assert.Empty((await ReadAsync(WorkspaceA, Monday)).Items);
    }

    [Fact]
    public async Task A_cursor_from_somewhere_else_is_refused()
    {
        await ThemeAsync(WorkspaceA, Monday, "taco-tuesday");

        foreach (var title in new[] { "One", "Two" })
        {
            await KeepPictureAsync(WorkspaceA, await ContextAsync(WorkspaceA, title), Now.AddDays(-1));
        }

        var cursor = (await ReadAsync(WorkspaceA, Monday, limit: 1)).NextCursor;
        Assert.NotNull(cursor);

        // Good where it was issued.
        Assert.Single((await ReadAsync(WorkspaceA, Monday, cursor, limit: 1)).Items);

        // Refused for another theme of the same workspace, and for anything that is not a cursor at all.
        foreach (var (key, bad) in new[] { ("taco-tuesday", cursor), (Monday, "not-a-cursor") })
        {
            var refused = await TryReadAsync(WorkspaceA, key, bad);

            Assert.False(refused.Succeeded);
            Assert.Equal(ContentErrorCodes.ThemedDayHistoryCursorInvalid, refused.Error!.Code);
        }
    }

    // ---- The theme --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_retired_themes_history_reads_as_it_did_before()
    {
        await ThemeAsync(WorkspaceA, Monday);
        await KeepPictureAsync(WorkspaceA, await ContextAsync(WorkspaceA, "Kept"), Now.AddDays(-1));

        var before = await ReadAsync(WorkspaceA, Monday);

        // An empty week retires every theme: the row and the key survive, and so does what was made for it.
        await ThemeAsync(WorkspaceA);

        var after = await ReadAsync(WorkspaceA, Monday);

        Assert.Equal("Kept", Assert.Single(after.Items).WorkingTitle);
        Assert.Equal(before.Items.Select(item => item.ContextId), after.Items.Select(item => item.ContextId));
    }

    [Theory]
    [InlineData("never-existed")]
    [InlineData("NOT A KEY")]
    [InlineData(" ")]
    public async Task A_key_this_workspace_does_not_have_is_not_found(string key)
    {
        // Work can carry a key no theme owns — the reference is weak by design — and that is still no theme.
        await KeepPictureAsync(WorkspaceA, await ContextAsync(WorkspaceA, "Orphan", themeKey: "never-existed"), Now);

        var result = await TryReadAsync(WorkspaceA, key);

        Assert.False(result.Succeeded);
        Assert.Equal(ContentErrorCodes.WeeklyThemeNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task A_deleted_themes_history_is_gone_even_when_it_was_cached()
    {
        await ThemeAsync(WorkspaceA, Monday);
        await KeepPictureAsync(WorkspaceA, await ContextAsync(WorkspaceA, "Kept"), Now.AddDays(-1));
        Assert.Single((await ReadAsync(WorkspaceA, Monday)).Items);

        var deleted = await InAsync(WorkspaceA, services =>
            services.GetRequiredService<IWorkspaceWeeklyThemeFacade>().DeleteAsync(UserId, Monday, null, Ct));
        Assert.True(deleted.Result.Succeeded, deleted.Result.Error?.Code);

        var result = await TryReadAsync(WorkspaceA, Monday);

        Assert.False(result.Succeeded);
        Assert.Equal(ContentErrorCodes.WeeklyThemeNotFound, result.Error!.Code);
    }

    // ---- The cache --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_second_read_is_served_from_the_cache_until_an_outcome_retires_it()
    {
        await ThemeAsync(WorkspaceA, Monday);
        var first = await ContextAsync(WorkspaceA, "First");
        await KeepPictureAsync(WorkspaceA, first, Now.AddDays(-2));

        Assert.Single((await ReadAsync(WorkspaceA, Monday)).Items);

        // Written straight to the table, past every facade: nothing retired the cached history, so the read
        // still says what it said. This is the proof the cache is what answered — and the reason every real
        // write goes through a facade that retires it.
        var second = await ContextAsync(WorkspaceA, "Second");
        await KeepPictureAsync(WorkspaceA, second, Now.AddDays(-1));

        Assert.Equal(["First"], (await ReadAsync(WorkspaceA, Monday)).Items.Select(item => item.WorkingTitle));

        // Accepting a post is an outcome, and it goes through the facade.
        _clock.Advance(TimeSpan.FromMinutes(1));
        await AcceptPostAsync(WorkspaceA, first, "instagram");

        var page = await ReadAsync(WorkspaceA, Monday);

        Assert.Equal(["First", "Second"], page.Items.Select(item => item.WorkingTitle));
        Assert.Equal(
            [ThemedDayOutcomeKind.AcceptedPosts, ThemedDayOutcomeKind.KeptPicture],
            page.Items[0].Outcomes.Select(outcome => outcome.Kind));
    }

    [Fact]
    public async Task Keeping_a_picture_through_the_context_retires_the_cached_history()
    {
        await ThemeAsync(WorkspaceA, Monday);
        var contextId = await ContextAsync(WorkspaceA, "Loaf");
        var assetId = await AssetAsync(WorkspaceA);

        Assert.Empty((await ReadAsync(WorkspaceA, Monday)).Items);

        var added = await InAsync(WorkspaceA, async services =>
        {
            var contexts = services.GetRequiredService<ICreativeContextFacade>();
            var current = await contexts.GetAsync(contextId, Ct);

            return await contexts.AddReferenceAsync(
                contextId,
                new AddCreativeContextReferenceViewModel
                {
                    ExpectedConcurrencyToken = current.Value!.ConcurrencyToken,
                    Reference = new CreativeContextReferenceInputViewModel
                    {
                        Kind = CreativeContextReferenceKind.DamAsset,
                        Purpose = CreativeContextReferencePurpose.Keeper,
                        MediaAssetId = assetId,
                    },
                },
                Ct);
        });
        Assert.True(added.Succeeded, added.Error?.Code);

        var kept = Assert.Single((await ReadAsync(WorkspaceA, Monday)).Items);
        Assert.Equal(assetId, Assert.Single(kept.Outcomes).MediaAssetId);

        // And taking the keeper away again retires it too: history follows what is true now.
        var referenceId = added.Value!.References.Single().Id;
        var removed = await InAsync(WorkspaceA, services => services.GetRequiredService<ICreativeContextFacade>()
            .RemoveReferenceAsync(contextId, referenceId, added.Value.ConcurrencyToken, Ct));
        Assert.True(removed.Succeeded, removed.Error?.Code);

        Assert.Empty((await ReadAsync(WorkspaceA, Monday)).Items);
    }

    [Fact]
    public async Task Newer_words_do_not_take_an_accepted_post_out_of_the_history_or_reorder_it()
    {
        await ThemeAsync(WorkspaceA, Monday);
        var contextId = await ContextAsync(WorkspaceA, "Loaf");
        var acceptedRevisionId = await AcceptPostAsync(WorkspaceA, contextId, "instagram");

        var accepted = Assert.Single((await ReadAsync(WorkspaceA, Monday)).Items);

        // A later draft puts the channel back to awaiting a decision. The post accepted before it is still
        // accepted, and when that happened has not moved.
        _clock.Advance(TimeSpan.FromDays(2));
        await DraftPostAsync(WorkspaceA, contextId, "instagram", acceptedRevisionId);

        var after = Assert.Single((await ReadAsync(WorkspaceA, Monday)).Items);

        Assert.Equal(["instagram"], Assert.Single(after.Outcomes).ChannelKeys);
        Assert.Equal(accepted.LatestOutcomeAt, after.LatestOutcomeAt);
    }

    [Fact]
    public async Task A_post_is_dated_by_when_it_was_accepted_not_when_it_was_written()
    {
        await ThemeAsync(WorkspaceA, Monday);
        var contextId = await ContextAsync(WorkspaceA, "Loaf");
        var revisionId = await DraftPostAsync(WorkspaceA, contextId, "instagram");

        // Written on the ninth, accepted on the twelfth.
        _clock.Advance(TimeSpan.FromDays(3));
        var accepted = await InAsync(WorkspaceA, services => services.GetRequiredService<ISocialPackageFacade>()
            .AcceptAsync(UserId, contextId, "instagram", revisionId, Ct));
        Assert.True(accepted.Succeeded, accepted.Error?.Code);

        var item = Assert.Single((await ReadAsync(WorkspaceA, Monday)).Items);

        Assert.Equal(Now.AddDays(3), item.LatestOutcomeAt);
        Assert.Equal(Now.AddDays(3), Assert.Single(item.Outcomes).At);
    }

    [Fact]
    public async Task Drafting_a_post_and_creating_work_each_retire_the_cached_history()
    {
        await ThemeAsync(WorkspaceA, Monday);
        var contextId = await ContextAsync(WorkspaceA, "First");
        Assert.Empty((await ReadAsync(WorkspaceA, Monday)).Items);

        // Past the facades, so the cached empty history still stands.
        await KeepPictureAsync(WorkspaceA, contextId, Now.AddDays(-1));
        Assert.Empty((await ReadAsync(WorkspaceA, Monday)).Items);

        await DraftPostAsync(WorkspaceA, contextId, "instagram");
        Assert.Equal(["First"], (await ReadAsync(WorkspaceA, Monday)).Items.Select(item => item.WorkingTitle));

        // A piece of work created already naming a recipe that began as an accepted draft is history at once.
        var recipeId = await RecipeAsync(WorkspaceA);
        await DraftOperationAsync(WorkspaceA, recipeId, Now.AddDays(-2), AiOperationStatus.Accepted);

        var created = await InAsync(WorkspaceA, services => services.GetRequiredService<ICreativeContextFacade>()
            .CreateAsync(
                UserId,
                new CreateCreativeContextViewModel
                {
                    WorkingTitle = "Second",
                    Day = DayOfWeek.Monday,
                    WeeklyThemeKey = Monday,
                    From = new CreativeContextReferenceInputViewModel
                    {
                        Kind = CreativeContextReferenceKind.Recipe,
                        RecipeId = recipeId,
                    },
                },
                null,
                Ct));
        Assert.True(created.Result.Succeeded, created.Result.Error?.Code);

        Assert.Equal(
            ["First", "Second"], (await ReadAsync(WorkspaceA, Monday)).Items.Select(item => item.WorkingTitle));
    }

    [Fact]
    public async Task A_refused_write_retires_nothing()
    {
        await ThemeAsync(WorkspaceA, Monday);
        var contextId = await ContextAsync(WorkspaceA, "Loaf");
        await ReadAsync(WorkspaceA, Monday);

        var writesBefore = _cache.Writes.Count;

        // No such revision: the decision is refused, so there is no new outcome to retire a history for.
        var refused = await InAsync(WorkspaceA, services => services.GetRequiredService<ISocialPackageFacade>()
            .AcceptAsync(UserId, contextId, "instagram", Guid.NewGuid(), Ct));

        Assert.False(refused.Succeeded);
        Assert.Equal(writesBefore, _cache.Writes.Count);
    }

    // ---- Two workspaces ---------------------------------------------------------------------------------

    [Fact]
    public async Task Each_workspace_reads_only_its_own_history_under_the_same_key()
    {
        await ThemeAsync(WorkspaceA, Monday);
        await ThemeAsync(WorkspaceB, Monday, "taco-tuesday");

        await KeepPictureAsync(WorkspaceA, await ContextAsync(WorkspaceA, "A's loaf"), Now.AddDays(-1));
        await KeepPictureAsync(WorkspaceA, await ContextAsync(WorkspaceA, "A's soup"), Now.AddDays(-2));

        var stew = await ContextAsync(WorkspaceB, "B's stew");
        await AcceptDraftAsync(WorkspaceB, stew, Now.AddDays(-1));
        await AcceptPostAsync(WorkspaceB, stew, "instagram");

        // A first, so its history is cached when B asks for the same key.
        Assert.Equal(["A's loaf", "A's soup"], (await ReadAsync(WorkspaceA, Monday)).Items.Select(item => item.WorkingTitle));

        var inB = Assert.Single((await ReadAsync(WorkspaceB, Monday)).Items);
        Assert.Equal("B's stew", inB.WorkingTitle);
        Assert.Equal(
            [ThemedDayOutcomeKind.AcceptedPosts, ThemedDayOutcomeKind.AcceptedRecipeDraft],
            inB.Outcomes.Select(outcome => outcome.Kind));

        // And again, now that both are cached: neither read finds the other's entry.
        Assert.Equal(2, (await ReadAsync(WorkspaceA, Monday)).Items.Count);
        Assert.Single((await ReadAsync(WorkspaceB, Monday)).Items);

        // A theme only B has is no theme in A.
        Assert.Equal(
            ContentErrorCodes.WeeklyThemeNotFound, (await TryReadAsync(WorkspaceA, "taco-tuesday")).Error!.Code);

        // A cursor issued for A's history is not a position in B's.
        var cursor = (await ReadAsync(WorkspaceA, Monday, limit: 1)).NextCursor;
        Assert.Equal(
            ContentErrorCodes.ThemedDayHistoryCursorInvalid, (await TryReadAsync(WorkspaceB, Monday, cursor)).Error!.Code);
    }

    [Fact]
    public async Task One_workspaces_outcome_does_not_retire_or_reach_the_others_history()
    {
        await ThemeAsync(WorkspaceA, Monday);
        await ThemeAsync(WorkspaceB, Monday);

        var inA = await ContextAsync(WorkspaceA, "A's loaf");
        var inB = await ContextAsync(WorkspaceB, "B's stew");
        await KeepPictureAsync(WorkspaceB, inB, Now.AddDays(-1));

        Assert.Empty((await ReadAsync(WorkspaceA, Monday)).Items);
        Assert.Single((await ReadAsync(WorkspaceB, Monday)).Items);

        // Written past the facades in B, so only an invalidation could surface it — and A's is not B's.
        await KeepPictureAsync(WorkspaceB, await ContextAsync(WorkspaceB, "B's second"), Now.AddDays(-2));
        await AcceptPostAsync(WorkspaceA, inA, "instagram");

        Assert.Equal("A's loaf", Assert.Single((await ReadAsync(WorkspaceA, Monday)).Items).WorkingTitle);
        Assert.Equal("B's stew", Assert.Single((await ReadAsync(WorkspaceB, Monday)).Items).WorkingTitle);
    }

    [Fact]
    public async Task Every_cache_key_starts_with_the_workspace_it_belongs_to()
    {
        await ThemeAsync(WorkspaceA, Monday);
        await ThemeAsync(WorkspaceB, Monday);
        await AcceptPostAsync(WorkspaceA, await ContextAsync(WorkspaceA, "A's loaf"), "instagram");
        await AcceptPostAsync(WorkspaceB, await ContextAsync(WorkspaceB, "B's stew"), "instagram");

        _cache.Reads.Clear();
        _cache.Writes.Clear();

        await ReadAsync(WorkspaceA, Monday);
        var fromA = _cache.Reads.Concat(_cache.Writes).ToList();

        _cache.Reads.Clear();
        _cache.Writes.Clear();

        await ReadAsync(WorkspaceB, Monday);
        var fromB = _cache.Reads.Concat(_cache.Writes).ToList();

        Assert.NotEmpty(fromA);
        Assert.NotEmpty(fromB);
        Assert.All(fromA, key => Assert.StartsWith($"workspace:{WorkspaceA:D}:theme-history:", key));
        Assert.All(fromB, key => Assert.StartsWith($"workspace:{WorkspaceB:D}:theme-history:", key));
        Assert.Empty(fromA.Intersect(fromB));
    }

    // ---- harness ----------------------------------------------------------------------------------------

    private async Task<T> InAsync<T>(Guid workspaceId, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner,
            UserId);

        return await work(scope.ServiceProvider);
    }

    private Task<T> DbAsync<T>(Guid workspaceId, Func<CreatorPantryDbContext, Task<T>> work) =>
        InAsync(workspaceId, services => work(services.GetRequiredService<CreatorPantryDbContext>()));

    private Task<OperationResult<CursorPageServiceModel<ThemedDayHistoryItemServiceModel>>> TryReadAsync(
        Guid workspaceId, string themeKey, string? cursor = null, int? limit = null) =>
        InAsync(workspaceId, services => services.GetRequiredService<IThemedDayHistoryFacade>()
            .GetPageAsync(themeKey, new ThemedDayHistoryViewModel(cursor, limit), Ct));

    private async Task<CursorPageServiceModel<ThemedDayHistoryItemServiceModel>> ReadAsync(
        Guid workspaceId, string themeKey, string? cursor = null, int? limit = null)
    {
        var result = await TryReadAsync(workspaceId, themeKey, cursor, limit);

        Assert.True(result.Succeeded, result.Error?.Code);

        return result.Value!;
    }

    /// <summary>Replaces the workspace's week with these keys, one per day from Monday. None clears the week.</summary>
    private async Task ThemeAsync(Guid workspaceId, params string[] keys)
    {
        var outcome = await InAsync(workspaceId, services =>
            services.GetRequiredService<IWorkspaceWeeklyThemeFacade>().ReplaceAsync(
                UserId,
                new ReplaceWeeklyThemesViewModel
                {
                    Themes =
                    [
                        .. keys.Select((key, index) => new WeeklyThemeInput
                        {
                            Day = (DayOfWeek)((index + 1) % 7),
                            Key = key,
                            DisplayName = key,
                        }),
                    ],
                },
                null,
                Ct));

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Code);
    }

    /// <summary>A piece of work carrying a theme key, written straight to the table.</summary>
    private Task<Guid> ContextAsync(
        Guid workspaceId,
        string title,
        string themeKey = Monday,
        DateTimeOffset? updatedAt = null,
        DateTimeOffset? archivedAt = null) =>
        DbAsync(workspaceId, async db =>
        {
            var context = CreativeContextSeeds.NewContext(updatedAt ?? Now, title);
            context.WeeklyThemeKey = themeKey;
            context.ArchivedAt = archivedAt;
            db.CreativeContexts.Add(context);
            await db.SaveChangesAsync(Ct);

            return context.Id;
        });

    private Task<Guid> AssetAsync(Guid workspaceId) =>
        DbAsync(workspaceId, db => CreativeContextSeeds.MediaAssetAsync(db, workspaceId, Now, Ct));

    private Task<Guid> RecipeAsync(Guid workspaceId) =>
        DbAsync(workspaceId, async db => (await CreativeContextSeeds.RecipeAsync(db, Now, Ct)).RecipeId);

    private Task<Guid> ReferAsync(
        Guid workspaceId,
        Guid contextId,
        CreativeContextReferenceKind kind,
        DateTimeOffset addedAt,
        Action<CreativeContextReference> shape) =>
        DbAsync(workspaceId, async db =>
        {
            var order = await db.CreativeContextReferences.CountAsync(row => row.CreativeContextId == contextId, Ct);
            var reference = new CreativeContextReference
            {
                Id = Guid.NewGuid(),
                CreativeContextId = contextId,
                Kind = kind,
                Purpose = CreativeContextReferencePurpose.Source,
                SortOrder = order,
                AddedAt = addedAt,
            };
            shape(reference);
            db.CreativeContextReferences.Add(reference);
            await db.SaveChangesAsync(Ct);

            return reference.Id;
        });

    /// <summary>A library asset the work names, as a keeper unless told otherwise. Past the facades.</summary>
    private async Task<Guid> KeepPictureAsync(
        Guid workspaceId,
        Guid contextId,
        DateTimeOffset at,
        CreativeContextReferencePurpose purpose = CreativeContextReferencePurpose.Keeper)
    {
        var assetId = await AssetAsync(workspaceId);

        await ReferAsync(workspaceId, contextId, CreativeContextReferenceKind.DamAsset, at, reference =>
        {
            reference.MediaAssetId = assetId;
            reference.Purpose = purpose;
        });

        return assetId;
    }

    private Task<Guid> ReferRecipeAsync(Guid workspaceId, Guid contextId, Guid recipeId) =>
        ReferAsync(workspaceId, contextId, CreativeContextReferenceKind.Recipe, Now,
            reference => reference.RecipeId = recipeId);

    /// <summary>
    /// A recipe the work names, and a first-draft operation that created it in the given state. Past the
    /// facades, so it retires no cache.
    /// </summary>
    private async Task<Guid> AcceptDraftAsync(
        Guid workspaceId,
        Guid contextId,
        DateTimeOffset at,
        AiOperationStatus status = AiOperationStatus.Accepted)
    {
        var recipeId = await RecipeAsync(workspaceId);
        await ReferRecipeAsync(workspaceId, contextId, recipeId);
        await DraftOperationAsync(workspaceId, recipeId, at, status);

        return recipeId;
    }

    /// <summary>A first-draft operation that created this recipe, in the given state.</summary>
    private Task<int> DraftOperationAsync(Guid workspaceId, Guid recipeId, DateTimeOffset at, AiOperationStatus status) =>
        DbAsync(workspaceId, async db =>
        {
            var operationId = Guid.NewGuid();

            db.AiOperations.Add(new AiOperation
            {
                Id = operationId,
                TaskType = AiTaskType.RecipeFirstDraft,
                Scope = AiOperationScope.NotApplicable,
                Status = status,
                RecipeId = recipeId,
                IdempotencyKey = $"draft-{operationId}",
                RequestedByMembershipId = Guid.NewGuid(),
                RequestedAt = at,
                StatusChangedAt = at,
                CompletedAt = status == AiOperationStatus.Proposed ? null : at,
                AvailableAt = at,
            });

            return await db.SaveChangesAsync(Ct);
        });

    /// <summary>Writes a post for one channel through the facade, and returns the revision it made.</summary>
    private async Task<Guid> DraftPostAsync(
        Guid workspaceId, Guid contextId, string channelKey, Guid? afterRevisionId = null)
    {
        var drafted = await InAsync(workspaceId, services => services.GetRequiredService<ISocialPackageFacade>()
            .RecordEditAsync(
                new SocialEditedRevisionInput(contextId, channelKey, "Still warm.", null, afterRevisionId), Ct));

        Assert.True(drafted.Succeeded, drafted.Error?.Code);

        return drafted.Value!.Channels.Single(channel => channel.ChannelKey == channelKey).Latest.Id;
    }

    /// <summary>Drafts and accepts a post for one channel, both through the facade.</summary>
    private async Task<Guid> AcceptPostAsync(Guid workspaceId, Guid contextId, string channelKey)
    {
        var revisionId = await DraftPostAsync(workspaceId, contextId, channelKey);

        var accepted = await InAsync(workspaceId, services => services.GetRequiredService<ISocialPackageFacade>()
            .AcceptAsync(UserId, contextId, channelKey, revisionId, Ct));

        Assert.True(accepted.Succeeded, accepted.Error?.Code);

        return revisionId;
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
