using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Content;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Recipes;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// PRM-002's read: what the channel filter and the search match, how the keyset pages, what a summary row
/// carries, and the boundary between two workspaces' libraries.
/// </summary>
/// <remarks>
/// Over a real database rather than a fake repository, because the keyset predicate agreeing with the
/// <c>ORDER BY</c> — and the preview being truncated by SQL rather than in memory — are the behaviour under
/// test. Driven through the facade, so the validator, the query factory and the cursor's scope binding are all
/// in the path a request actually takes.
/// </remarks>
public sealed class PromptSearchTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);
    private const string UserId = "user-1";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly MovableClock _clock = new(Start);
    private readonly ServiceProvider _provider;

    public PromptSearchTests()
    {
        _connection.Open();

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
            .AddLogging()
            .AddDistributedMemoryCache()
            .AddApplicationCache()
            .AddSingleton<IClock>(_clock)
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiProposalLookupBusiness, AiProposalLookupBusiness>()
            .AddScoped<IAiProposalLookupFacade, AiProposalLookupFacade>()
            .AddIdempotency(new ConfigurationBuilder().Build())
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = Start },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "workspace-b", CreatedAt = Start });

        db.SaveChanges();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AsyncServiceScope ScopeFor(Guid workspaceId)
    {
        var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner,
            UserId);

        return scope;
    }

    /// <summary>
    /// Saves one prompt through the real write seam, advancing the clock so every prompt has its own instant
    /// unless a test deliberately wants a tie.
    /// </summary>
    private async Task<Guid> SaveAsync(
        Guid workspaceId,
        string text,
        string channelKey = "instagram",
        string? label = null,
        bool advanceClock = true)
    {
        if (advanceClock)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        await using var scope = ScopeFor(workspaceId);
        var outcome = await scope.ServiceProvider.GetRequiredService<IPromptRecordFacade>().SaveAsync(
            UserId,
            new SavePromptRecordViewModel
            {
                ChannelKey = channelKey,
                ImageKind = PromptImageKind.Hero,
                Text = text,
                Label = label,
                Source = PromptRecordSource.Manual,
            },
            idempotencyKey: null,
            Ct);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Code);

        return outcome.Result.Value!.PromptRecordId;
    }

    private async Task<PromptSearchPageServiceModel> ListAsync(
        Guid workspaceId, PromptSearchViewModel? query = null)
    {
        await using var scope = ScopeFor(workspaceId);
        var result = await scope.ServiceProvider.GetRequiredService<IPromptRecordFacade>()
            .SearchAsync(query ?? new PromptSearchViewModel(), Ct);

        Assert.True(result.Succeeded, result.Error?.Code);

        return result.Value!;
    }

    private async Task<Domain.Managers.Results.OperationResult<PromptSearchPageServiceModel>> ListResultAsync(
        Guid workspaceId, PromptSearchViewModel query)
    {
        await using var scope = ScopeFor(workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IPromptRecordFacade>().SearchAsync(query, Ct);
    }

    private static IReadOnlyList<Guid> Ids(PromptSearchPageServiceModel page) =>
        [.. page.Items.Select(item => item.PromptRecordId)];

    // ---- the default ordering ----------------------------------------------------------------------------

    [Fact]
    public async Task An_empty_library_is_an_empty_page_rather_than_a_refusal()
    {
        var page = await ListAsync(WorkspaceA);

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task The_library_reads_newest_first()
    {
        var first = await SaveAsync(WorkspaceA, "First prompt.");
        var second = await SaveAsync(WorkspaceA, "Second prompt.");
        var third = await SaveAsync(WorkspaceA, "Third prompt.");

        var page = await ListAsync(WorkspaceA);

        Assert.Equal([third, second, first], Ids(page));
        Assert.Equal(3, page.TotalCount);
    }

    /// <summary>
    /// Prompts saved in the same instant still have a total order, broken by id — which is what keeps a page
    /// boundary falling between them from repeating one and skipping the other.
    /// </summary>
    [Fact]
    public async Task Prompts_saved_in_the_same_instant_are_still_ordered()
    {
        for (var index = 0; index < 6; index++)
        {
            await SaveAsync(WorkspaceA, $"Prompt {index}.", advanceClock: false);
        }

        var firstPage = await ListAsync(WorkspaceA, new PromptSearchViewModel(Limit: 3));
        var secondPage = await ListAsync(WorkspaceA, new PromptSearchViewModel(Cursor: firstPage.NextCursor));

        var seen = Ids(firstPage).Concat(Ids(secondPage)).ToList();

        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen.Distinct().Count());
    }

    // ---- the channel filter ------------------------------------------------------------------------------

    [Fact]
    public async Task The_channel_filter_returns_only_that_channels_prompts()
    {
        var instagram = await SaveAsync(WorkspaceA, "For the feed.", channelKey: "instagram");
        await SaveAsync(WorkspaceA, "For the blog.", channelKey: "blog");
        var alsoInstagram = await SaveAsync(WorkspaceA, "Also for the feed.", channelKey: "instagram");

        var page = await ListAsync(WorkspaceA, new PromptSearchViewModel(Channel: "instagram"));

        Assert.Equal([alsoInstagram, instagram], Ids(page));
        Assert.Equal(2, page.TotalCount);
    }

    /// <summary>
    /// A channel key no catalogue entry has is an empty page, not a refusal — unlike a save, which validates it.
    /// A read has to keep answering for keys the catalogue has retired, because a prompt that stored one is
    /// still in the library.
    /// </summary>
    [Fact]
    public async Task A_channel_naming_nothing_is_an_empty_page()
    {
        await SaveAsync(WorkspaceA, "For the feed.");

        var page = await ListAsync(WorkspaceA, new PromptSearchViewModel(Channel: "mastodon"));

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Null(page.NextCursor);
    }

    // ---- the search --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_search_matches_the_prompt_text_and_the_label()
    {
        var byText = await SaveAsync(WorkspaceA, "Overhead shot of soda bread on linen.");
        var byLabel = await SaveAsync(WorkspaceA, "A styled scene.", label: "Soda bread hero");
        await SaveAsync(WorkspaceA, "Close crumb detail.", label: "Crumb");

        var page = await ListAsync(WorkspaceA, new PromptSearchViewModel(Search: "soda"));

        Assert.Equal([byLabel, byText], Ids(page));
        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task The_search_ignores_case()
    {
        await SaveAsync(WorkspaceA, "Overhead shot of SODA bread.");

        Assert.Single((await ListAsync(WorkspaceA, new PromptSearchViewModel(Search: "soda"))).Items);
        Assert.Single((await ListAsync(WorkspaceA, new PromptSearchViewModel(Search: "SoDa"))).Items);
    }

    /// <summary>
    /// A search that matches nothing is an empty page, indistinguishable from an empty library — neither is an
    /// error, and a creator must not be able to tell the two apart by status.
    /// </summary>
    [Fact]
    public async Task A_search_matching_nothing_is_an_empty_page()
    {
        await SaveAsync(WorkspaceA, "Overhead shot.");

        var page = await ListAsync(WorkspaceA, new PromptSearchViewModel(Search: "risotto"));

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    /// <summary>
    /// A term below the floor is ignored rather than applied: one character matches most of a library, so
    /// filtering on it costs a scan to return what the unfiltered first page would have returned anyway.
    /// </summary>
    [Fact]
    public async Task A_term_below_the_length_floor_is_not_a_filter()
    {
        await SaveAsync(WorkspaceA, "Overhead shot.");
        await SaveAsync(WorkspaceA, "Close crumb detail.");

        var page = await ListAsync(WorkspaceA, new PromptSearchViewModel(Search: "o"));

        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task A_term_past_the_ceiling_is_refused_with_its_field()
    {
        var result = await ListResultAsync(
            WorkspaceA, new PromptSearchViewModel(Search: new string('a', PromptSearchPolicy.SearchMaxLength + 1)));

        Assert.False(result.Succeeded);
        Assert.Equal(ContentErrorCodes.PromptSearchInvalid, result.Error!.Code);
        Assert.Contains("search", result.Error.FieldErrors.Keys);
    }

    /// <summary>
    /// The model's draft is deliberately not searched: it records what the creator changed, so matching it would
    /// find a prompt by the very words they removed.
    /// </summary>
    [Fact]
    public async Task The_search_does_not_match_the_models_draft()
    {
        await using (var scope = ScopeFor(WorkspaceA))
        {
            var proposalId = await SeedProposalAsync(scope, WorkspaceA);
            var outcome = await scope.ServiceProvider.GetRequiredService<IPromptRecordFacade>().SaveAsync(
                UserId,
                new SavePromptRecordViewModel
                {
                    ChannelKey = "instagram",
                    ImageKind = PromptImageKind.Hero,
                    Text = "Overhead shot on linen.",
                    GeneratedText = "Overhead shot on a gingham tablecloth.",
                    Source = PromptRecordSource.ImagePromptComposition,
                    AiProposalId = proposalId,
                    PromptTemplateId = "image.prompt",
                    PromptTemplateVersion = "1.0.0",
                    PromptTemplateBodyChecksum = $"sha256:{new string('a', 64)}",
                },
                idempotencyKey: null,
                Ct);

            Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Code);
        }

        Assert.Single((await ListAsync(WorkspaceA, new PromptSearchViewModel(Search: "linen"))).Items);
        Assert.Empty((await ListAsync(WorkspaceA, new PromptSearchViewModel(Search: "gingham"))).Items);
    }

    [Fact]
    public async Task A_channel_and_a_search_narrow_together()
    {
        var wanted = await SaveAsync(WorkspaceA, "Overhead soda bread.", channelKey: "instagram");
        await SaveAsync(WorkspaceA, "Overhead soda bread.", channelKey: "blog");
        await SaveAsync(WorkspaceA, "Close crumb detail.", channelKey: "instagram");

        var page = await ListAsync(WorkspaceA, new PromptSearchViewModel(Search: "soda", Channel: "instagram"));

        Assert.Equal([wanted], Ids(page));
        Assert.Equal(1, page.TotalCount);
    }

    // ---- the summary projection --------------------------------------------------------------------------

    /// <summary>
    /// A row carries a preview and the full length, not the prompt. Truncated by the database, so the bytes are
    /// never fetched to be dropped.
    /// </summary>
    [Fact]
    public async Task A_row_carries_a_truncated_preview_and_the_real_length()
    {
        var text = new string('a', ContentPolicy.PromptTextMaxLength);
        await SaveAsync(WorkspaceA, text);

        var row = Assert.Single((await ListAsync(WorkspaceA)).Items);

        Assert.Equal(ContentPolicy.PromptPreviewMaxLength, row.TextPreview.Length);
        Assert.Equal(ContentPolicy.PromptTextMaxLength, row.TextLength);
    }

    [Fact]
    public async Task A_prompt_shorter_than_the_preview_is_not_padded_or_truncated()
    {
        await SaveAsync(WorkspaceA, "Overhead shot.");

        var row = Assert.Single((await ListAsync(WorkspaceA)).Items);

        Assert.Equal("Overhead shot.", row.TextPreview);
        Assert.Equal("Overhead shot.".Length, row.TextLength);
    }

    [Fact]
    public async Task A_row_carries_the_lineage_a_card_shows()
    {
        await SaveAsync(WorkspaceA, "Overhead shot.", label: "Hero");

        var row = Assert.Single((await ListAsync(WorkspaceA)).Items);

        Assert.Equal("instagram", row.ChannelKey);
        Assert.Equal(PromptImageKind.Hero, row.ImageKind);
        Assert.Equal(PromptRecordSource.Manual, row.Source);
        Assert.Equal("Hero", row.Label);
        Assert.Null(row.RecipeId);
        Assert.Null(row.RecipeVersionId);
    }

    /// <summary>
    /// What a summary row must <em>not</em> publish. A rule no ordinary test would notice breaking: a field
    /// holding the whole prompt, a membership id, or anything named like a URL or an object path would work
    /// perfectly and quietly undo the reasons this projection is narrow.
    /// </summary>
    [Fact]
    public void A_summary_row_publishes_no_prompt_body_membership_or_object_path()
    {
        var properties = typeof(PromptSummaryServiceModel).GetProperties().Select(property => property.Name).ToList();

        Assert.DoesNotContain("Text", properties);
        Assert.DoesNotContain("GeneratedText", properties);
        Assert.DoesNotContain("WorkspaceId", properties);
        Assert.DoesNotContain("CreatedByMembershipId", properties);
        Assert.DoesNotContain("AiProposalId", properties);

        Assert.DoesNotContain(
            properties,
            name => new[] { "Url", "Uri", "Path", "BlobName", "ObjectKey", "Location", "Href" }
                .Any(banned => name.Contains(banned, StringComparison.OrdinalIgnoreCase)));
    }

    // ---- paging ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_keyset_pages_the_whole_library_without_repeating_or_skipping()
    {
        var saved = new List<Guid>();
        for (var index = 0; index < 7; index++)
        {
            saved.Add(await SaveAsync(WorkspaceA, $"Prompt {index}."));
        }

        saved.Reverse();

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = await ListAsync(WorkspaceA, new PromptSearchViewModel(Cursor: cursor, Limit: 3));
            seen.AddRange(Ids(page));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(saved, seen);
    }

    [Fact]
    public async Task The_last_page_carries_no_cursor()
    {
        await SaveAsync(WorkspaceA, "Only one.");

        var page = await ListAsync(WorkspaceA, new PromptSearchViewModel(Limit: 1));

        Assert.Single(page.Items);

        // Exactly as many rows as the limit and no more to come: the probe row is what tells the page that,
        // rather than a count comparison that would promise a page which turns out to be empty.
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task An_out_of_range_limit_is_clamped_rather_than_refused()
    {
        for (var index = 0; index < 3; index++)
        {
            await SaveAsync(WorkspaceA, $"Prompt {index}.");
        }

        Assert.Equal(3, (await ListAsync(WorkspaceA, new PromptSearchViewModel(Limit: 10_000))).Items.Count);
        Assert.Single((await ListAsync(WorkspaceA, new PromptSearchViewModel(Limit: 0))).Items);
        Assert.Single((await ListAsync(WorkspaceA, new PromptSearchViewModel(Limit: -5))).Items);
    }

    [Fact]
    public async Task A_total_is_not_counted_when_it_was_not_asked_for()
    {
        await SaveAsync(WorkspaceA, "Overhead shot.");

        var page = await ListAsync(WorkspaceA, new PromptSearchViewModel(IncludeTotal: false));

        Assert.Single(page.Items);
        Assert.Null(page.TotalCount);
    }

    /// <summary>The total counts every match across all pages, not the page in hand.</summary>
    [Fact]
    public async Task A_total_counts_past_the_page()
    {
        for (var index = 0; index < 5; index++)
        {
            await SaveAsync(WorkspaceA, $"Prompt {index}.");
        }

        var page = await ListAsync(WorkspaceA, new PromptSearchViewModel(Limit: 2));

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(5, page.TotalCount);
    }

    [Fact]
    public async Task A_cursor_that_is_not_even_base64_is_refused_with_the_cursor_code()
    {
        var result = await ListResultAsync(WorkspaceA, new PromptSearchViewModel(Cursor: "not a cursor"));

        Assert.False(result.Succeeded);
        Assert.Equal(ContentErrorCodes.PromptCursorInvalid, result.Error!.Code);
        Assert.Contains("cursor", result.Error.FieldErrors.Keys);
    }

    /// <summary>
    /// A cursor is bound to the filters it was issued for. Replayed against different ones it would still decode
    /// and still produce a page — just not the page it names, with rows silently skipped or repeated, which is
    /// the exact failure the keyset design exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_cursor_from_a_different_filter_is_refused()
    {
        for (var index = 0; index < 4; index++)
        {
            await SaveAsync(WorkspaceA, $"Prompt {index}.", channelKey: "instagram");
        }

        var page = await ListAsync(WorkspaceA, new PromptSearchViewModel(Channel: "instagram", Limit: 2));
        Assert.NotNull(page.NextCursor);

        foreach (var replay in new[]
        {
            new PromptSearchViewModel(Cursor: page.NextCursor),
            new PromptSearchViewModel(Channel: "blog", Cursor: page.NextCursor),
            new PromptSearchViewModel(Channel: "instagram", Search: "prompt", Cursor: page.NextCursor),
        })
        {
            var result = await ListResultAsync(WorkspaceA, replay);

            Assert.False(result.Succeeded);
            Assert.Equal(ContentErrorCodes.PromptCursorInvalid, result.Error!.Code);
        }
    }

    /// <summary>
    /// Asking for a different page size after a position is legitimate: a position is a position. The page size
    /// is deliberately not part of the cursor's scope.
    /// </summary>
    [Fact]
    public async Task A_cursor_survives_a_change_of_page_size()
    {
        for (var index = 0; index < 5; index++)
        {
            await SaveAsync(WorkspaceA, $"Prompt {index}.");
        }

        var first = await ListAsync(WorkspaceA, new PromptSearchViewModel(Limit: 2));
        var second = await ListAsync(WorkspaceA, new PromptSearchViewModel(Cursor: first.NextCursor, Limit: 3));

        Assert.Equal(3, second.Items.Count);
        Assert.Empty(Ids(first).Intersect(Ids(second)));
    }

    // ---- two workspaces ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_library_holds_only_its_own_workspaces_prompts()
    {
        var mine = await SaveAsync(WorkspaceA, "Mine.");
        var theirs = await SaveAsync(WorkspaceB, "Theirs.");

        var pageA = await ListAsync(WorkspaceA);
        var pageB = await ListAsync(WorkspaceB);

        Assert.Equal([mine], Ids(pageA));
        Assert.Equal([theirs], Ids(pageB));
        Assert.Equal(1, pageA.TotalCount);
        Assert.Equal(1, pageB.TotalCount);
    }

    /// <summary>
    /// A term that appears only in the other workspace's prompts matches nothing — the search is scoped by the
    /// query filter, so another library's text is not merely filtered out of it, it is not visible to it.
    /// </summary>
    [Fact]
    public async Task A_search_never_reaches_the_other_workspaces_prompts()
    {
        await SaveAsync(WorkspaceA, "Overhead shot of soda bread.");
        await SaveAsync(WorkspaceB, "Overhead shot of risotto.", label: "Risotto hero");

        var byText = await ListAsync(WorkspaceA, new PromptSearchViewModel(Search: "risotto"));
        var byLabel = await ListAsync(WorkspaceA, new PromptSearchViewModel(Search: "hero"));

        Assert.Empty(byText.Items);
        Assert.Equal(0, byText.TotalCount);
        Assert.Empty(byLabel.Items);
    }

    [Fact]
    public async Task A_channel_filter_never_reaches_the_other_workspaces_prompts()
    {
        await SaveAsync(WorkspaceB, "Theirs.", channelKey: "instagram");

        var page = await ListAsync(WorkspaceA, new PromptSearchViewModel(Channel: "instagram"));

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    /// <summary>
    /// A cursor minted in one workspace is refused in the other. The workspace is in the scope fingerprint
    /// precisely so this is an error rather than a page of the wrong library — nothing in the shared paging
    /// kernel would have put it there, because reference data has no workspace to put.
    /// </summary>
    [Fact]
    public async Task A_cursor_minted_in_one_workspace_is_refused_in_the_other()
    {
        for (var index = 0; index < 4; index++)
        {
            await SaveAsync(WorkspaceA, $"Mine {index}.");
            await SaveAsync(WorkspaceB, $"Theirs {index}.");
        }

        var mine = await ListAsync(WorkspaceA, new PromptSearchViewModel(Limit: 2));
        Assert.NotNull(mine.NextCursor);

        var result = await ListResultAsync(WorkspaceB, new PromptSearchViewModel(Cursor: mine.NextCursor));

        Assert.False(result.Succeeded);
        Assert.Equal(ContentErrorCodes.PromptCursorInvalid, result.Error!.Code);
    }

    /// <summary>
    /// And the fingerprints genuinely differ, which is what the refusal above rests on. Asserted directly
    /// because a scope that forgot the workspace would still make that test pass if the filters differed.
    /// </summary>
    [Fact]
    public void A_scope_is_built_per_workspace()
    {
        var filters = new PromptSearchFilters(Search: "soda", ChannelKey: "instagram");

        var mine = PromptSearchScope.Build(WorkspaceA, filters);
        var theirs = PromptSearchScope.Build(WorkspaceB, filters);

        Assert.NotEqual(mine, theirs);
        Assert.NotEqual(ReferenceCursor.Fingerprint(mine), ReferenceCursor.Fingerprint(theirs));
    }

    /// <summary>
    /// A cursor carrying this workspace's own fingerprint but the other workspace's row as its position still
    /// returns nothing of theirs.
    /// </summary>
    /// <remarks>
    /// The position is only ever a <c>WHERE</c> predicate over a set the query filter has already scoped, so
    /// this is safe by construction rather than by checking — which is exactly why it is worth a test. A
    /// hand-forged cursor is reachable (the fingerprint is an unkeyed checksum, not a signature), and nothing
    /// else locks in that forging one buys no access.
    /// </remarks>
    [Fact]
    public async Task A_forged_cursor_positioned_on_another_workspaces_row_reaches_none_of_it()
    {
        // Mine first, so every one of theirs is strictly newer than it. Positioning on their newest row then
        // means mine genuinely does follow that position — so an empty page would prove nothing, and the test
        // can tell "their rows are invisible" apart from "the position excluded everything".
        var mine = await SaveAsync(WorkspaceA, "Mine.");

        var theirs = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            theirs.Add(await SaveAsync(WorkspaceB, $"Theirs {index}."));
        }

        var theirNewest = (await ListAsync(WorkspaceB)).Items[0];

        // A's own scope, so the fingerprint is accepted — with B's newest row as the position. Their exact
        // instant rather than a nudged one: a tie would make the id the tie-break and the outcome depend on
        // GUID ordering.
        var forged = ReferenceCursor.Encode(
            PromptSearchPosition.FormatTimestamp(theirNewest.CreatedAt),
            theirNewest.PromptRecordId.ToString("D"),
            PromptSearchScope.Build(WorkspaceA, new PromptSearchFilters()));

        var page = await ListAsync(WorkspaceA, new PromptSearchViewModel(Cursor: forged));

        Assert.Equal([mine], Ids(page));
        Assert.DoesNotContain(page.Items, item => theirs.Contains(item.PromptRecordId));
    }

    /// <summary>
    /// Two filter sets that are different must not share a scope, whatever characters they contain.
    /// </summary>
    /// <remarks>
    /// The scope string is assembled from <c>|</c> and <c>=</c>, and two of its parts are text a caller
    /// chooses — the channel is not validated against the catalogue, because a read has to keep answering for
    /// retired keys. Without the length prefixes these two would build the identical string and accept each
    /// other's cursors, which is the silent skipping and repeating of rows the keyset exists to prevent.
    /// </remarks>
    [Fact]
    public void Two_different_filter_sets_never_share_a_scope()
    {
        var injected = PromptSearchScope.Build(
            WorkspaceA, new PromptSearchFilters(ChannelKey: "instagram|q=foo"));
        var honest = PromptSearchScope.Build(
            WorkspaceA, new PromptSearchFilters(Search: "foo", ChannelKey: "instagram"));

        Assert.NotEqual(honest, injected);
        Assert.NotEqual(ReferenceCursor.Fingerprint(honest), ReferenceCursor.Fingerprint(injected));

        // And no filter is distinct from a filter that happens to be empty.
        Assert.NotEqual(
            PromptSearchScope.Build(WorkspaceA, new PromptSearchFilters()),
            PromptSearchScope.Build(WorkspaceA, new PromptSearchFilters(ChannelKey: string.Empty)));
    }

    /// <summary>
    /// Nothing on this read path caches, and that is load-bearing rather than incidental.
    /// </summary>
    /// <remarks>
    /// A rule about what must <em>not</em> be added. A list of workspace-private rows put behind
    /// <c>CachedPageReader</c> or <c>IApplicationCache</c> would work perfectly, and the first key written
    /// without a <c>workspace:{id}:</c> prefix would serve one creator's library to another — the exact failure
    /// tenancy.md's cache rule exists for. There is no cached value here to isolate, and this is what keeps
    /// that true.
    /// </remarks>
    [Fact]
    public void The_read_path_takes_no_cache_dependency()
    {
        Type[] path =
        [
            typeof(Domain.Modules.Content.Facade.IPromptRecordFacade),
            typeof(IPromptRecordBusiness),
            typeof(Domain.Modules.Content.Data.IPromptRecordDataLayer),
            typeof(Domain.Modules.Content.Data.IPromptRecordSearchRepository),
        ];

        var offenders = new List<string>();

        foreach (var contract in path)
        {
            var implementation = contract.Assembly.GetTypes()
                .Single(type => type.IsClass && !type.IsAbstract && contract.IsAssignableFrom(type));

            offenders.AddRange(implementation
                .GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Where(parameter => parameter.ParameterType.Name.Contains("Cache", StringComparison.Ordinal))
                .Select(parameter => $"{implementation.Name} -> {parameter.ParameterType.Name}"));
        }

        Assert.Empty(offenders);
    }

    /// <summary>A list cannot be read before a workspace is resolved; the query filter refuses rather than answering for all.</summary>
    [Fact]
    public async Task A_library_cannot_be_listed_before_a_workspace_is_resolved()
    {
        await using var scope = _provider.CreateAsyncScope();
        var facade = scope.ServiceProvider.GetRequiredService<IPromptRecordFacade>();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => facade.SearchAsync(new PromptSearchViewModel(), Ct));
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private async Task<Guid> SeedProposalAsync(AsyncServiceScope scope, Guid workspaceId)
    {
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var operationId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();

        db.AiOperations.Add(new Domain.Modules.Ai.Data.Entities.AiOperation
        {
            Id = operationId,
            WorkspaceId = workspaceId,
            TaskType = Domain.Modules.Ai.Managers.AiTaskType.RecipeConcepts,
            Scope = Domain.Modules.Ai.Managers.AiOperationScope.NotApplicable,
            Status = Domain.Modules.Ai.Managers.AiOperationStatus.Proposed,
            IdempotencyKey = $"prompt-{operationId}",
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = Start,
            StatusChangedAt = Start,
            AvailableAt = Start,
        });

        db.AiProposals.Add(new Domain.Modules.Ai.Data.Entities.AiProposal
        {
            Id = proposalId,
            WorkspaceId = workspaceId,
            AiOperationId = operationId,
            OutputSchemaVersion = "image.prompt.v1",
            PromptTemplateId = "image.prompt",
            PromptTemplateVersion = "1.0.0",
            PromptTemplateBodyChecksum = "sha256:seed",
            ProviderName = "test-provider",
            ModelName = "test-model",
            CreatedAt = Start,
        });

        await db.SaveChangesAsync(Ct);

        return proposalId;
    }
}
