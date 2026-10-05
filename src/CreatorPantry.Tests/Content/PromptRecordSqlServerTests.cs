using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The prompt library against a real SQL Server, for the things SQLite cannot answer: that the migration's DDL is
/// accepted at all, that the check constraints and the two index shapes are written in a dialect SQL Server parses,
/// and that write-once holds against the real engine.
/// </summary>
/// <remarks>
/// The index shapes are the reason this exists rather than being folded into the SQLite aggregate tests. A
/// descending composite index and a filtered unique index are both provider-specific DDL, and the filtered one is
/// what makes a duplicate prompt record on a DAM retry unrepresentable (12.3a) — a guarantee worth proving on the
/// engine that will actually enforce it.
/// </remarks>
public sealed class PromptRecordSqlServerTests : IAsyncLifetime
{
    private static readonly Guid WorkspaceA = Guid.NewGuid();

    private static readonly Guid WorkspaceB = Guid.NewGuid();

    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-22.04").Build();

    private ServiceProvider? _provider;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddApplicationTime()
            .AddAudit()
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlServer(_container.GetConnectionString()))
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // The migration, not EnsureCreated: this is where the generated DDL is proved to be accepted.
        await db.Database.MigrateAsync();

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = Now },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "workspace-b", CreatedAt = Now });
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    private AsyncServiceScope ScopeFor(Guid workspaceId, string? slug = null)
    {
        var scope = _provider!.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            slug ?? (workspaceId == WorkspaceA ? "workspace-a" : "workspace-b"),
            Guid.NewGuid(),
            WorkspaceRole.Owner,
            "user-1");

        return scope;
    }

    private static PromptRecord NewRecord(
        string text = "Overhead shot of soda bread on linen, soft window light.",
        string channelKey = "instagram",
        Guid? generatedImageId = null) => new()
        {
            Id = Guid.NewGuid(),
            ChannelKey = channelKey,
            ImageKind = PromptImageKind.Hero,
            Text = text,
            Label = "Soda bread hero",
            Source = PromptRecordSource.Manual,
            GeneratedImageId = generatedImageId,
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
        };

    [Fact]
    public async Task A_record_round_trips_through_the_real_engine()
    {
        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var record = NewRecord();
        db.PromptRecords.Add(record);
        await db.SaveChangesAsync(Ct);

        var loaded = await db.PromptRecords.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(record.Text, loaded.Text);

        // Stamped by the ownership interceptor from the resolved context, never set by hand.
        Assert.Equal(WorkspaceA, loaded.WorkspaceId);
    }

    [Fact]
    public async Task The_filtered_unique_index_refuses_a_second_record_for_one_image()
    {
        var imageId = Guid.NewGuid();

        await using (var first = ScopeFor(WorkspaceA))
        {
            var db = first.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.PromptRecords.Add(NewRecord(generatedImageId: imageId));
            await db.SaveChangesAsync(Ct);
        }

        await using var second = ScopeFor(WorkspaceA);
        var retry = second.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        retry.PromptRecords.Add(NewRecord(generatedImageId: imageId));

        await Assert.ThrowsAsync<DbUpdateException>(() => retry.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Records_with_no_committed_image_do_not_collide()
    {
        // The filter is what makes that possible: without it every uncommitted prompt would share one NULL slot,
        // which SQL Server treats as equal in a unique index.
        await using var scope = ScopeFor(WorkspaceB);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.PromptRecords.AddRange(NewRecord(text: "One."), NewRecord(text: "Two."), NewRecord(text: "Three."));
        await db.SaveChangesAsync(Ct);

        Assert.Equal(3, await db.PromptRecords.CountAsync(Ct));
    }

    [Theory]
    [InlineData("", "instagram")]
    [InlineData("   ", "instagram")]
    [InlineData("A prompt.", "")]
    [InlineData("A prompt.", "   ")]
    public async Task The_blank_checks_are_written_in_a_dialect_SQL_Server_enforces(string text, string channelKey)
    {
        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.PromptRecords.Add(NewRecord(text: text, channelKey: channelKey));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task The_provenance_constraints_are_enforced_by_the_engine()
    {
        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // A generated prompt with no proposal: refused by CK_PromptRecords_AiProposal_Source.
        var record = NewRecord();
        record.Source = PromptRecordSource.ImagePromptComposition;
        record.GeneratedText = record.Text;
        record.PromptTemplateId = "image.prompt";
        record.PromptTemplateVersion = "1.0.0";
        record.PromptTemplateBodyChecksum = $"sha256:{new string('a', 64)}";
        db.PromptRecords.Add(record);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_source_outside_the_enum_is_refused_by_the_engine()
    {
        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var record = NewRecord();
        record.ImageKind = (PromptImageKind)9;
        db.PromptRecords.Add(record);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Write_once_holds_against_the_real_engine()
    {
        await using (var seed = ScopeFor(WorkspaceA))
        {
            var db = seed.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.PromptRecords.Add(NewRecord());
            await db.SaveChangesAsync(Ct);
        }

        await using var scope = ScopeFor(WorkspaceA);
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var loaded = await context.PromptRecords.SingleAsync(Ct);
        loaded.Text = "Rewritten after the fact.";

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Each_workspace_sees_only_its_own_library_on_the_real_engine()
    {
        await using (var a = ScopeFor(WorkspaceA))
        {
            var db = a.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.PromptRecords.Add(NewRecord(text: "A's prompt."));
            await db.SaveChangesAsync(Ct);
        }

        await using (var b = ScopeFor(WorkspaceB))
        {
            var db = b.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.PromptRecords.Add(NewRecord(text: "B's prompt."));
            await db.SaveChangesAsync(Ct);
        }

        await using var scope = ScopeFor(WorkspaceA);
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        Assert.Equal("A's prompt.", (await context.PromptRecords.AsNoTracking().SingleAsync(Ct)).Text);
    }

    [Fact]
    public async Task Deleting_a_workspace_still_succeeds_with_a_fully_pinned_prompt_in_it()
    {
        // The one question only a real engine answers, and it is not academic: this row cascades from Workspace
        // while itself holding Restrict pins to a recipe, a version and a proposal that cascade from the same
        // workspace. If SQL Server checked those NO ACTION constraints mid-cascade rather than at the end of the
        // statement, a workspace holding a prompt could never be deleted at all — and immutability means the
        // prompt could not be removed first through EF to clear the way.
        var workspaceId = Guid.NewGuid();

        await using (var seed = _provider!.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId, Name = "Doomed", Slug = "doomed", CreatedAt = Now,
            });
            await db.SaveChangesAsync(Ct);
        }

        var (recipeId, versionId, proposalId) = await SeedLineageAsync(workspaceId);

        await using (var write = ScopeFor(workspaceId, "doomed"))
        {
            var db = write.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var record = NewRecord();
            record.RecipeId = recipeId;
            record.RecipeVersionId = versionId;
            record.AiProposalId = proposalId;
            record.Source = PromptRecordSource.ImagePromptComposition;
            record.GeneratedText = record.Text;
            record.PromptTemplateId = "image.prompt";
            record.PromptTemplateVersion = "1.0.0";
            record.PromptTemplateBodyChecksum = $"sha256:{new string('a', 64)}";
            db.PromptRecords.Add(record);
            await db.SaveChangesAsync(Ct);
        }

        // The workspace row is not workspace-owned, so it is removed from an unresolved scope — and the prompt is
        // never materialised by EF, which is the documented carve-out that lets an erasure erase an immutable row.
        await using var scope = _provider!.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        context.Workspaces.Remove(await context.Workspaces.SingleAsync(item => item.Id == workspaceId, Ct));
        await context.SaveChangesAsync(Ct);

        Assert.False(await context.Workspaces.AnyAsync(item => item.Id == workspaceId, Ct));

        // And the prompt went with it rather than being left behind pointing at nothing.
        await using var after = ScopeFor(WorkspaceA);
        var remaining = after.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        Assert.Empty(await remaining.PromptRecords.IgnoreQueryFilters().Where(r => r.WorkspaceId == workspaceId).ToListAsync(Ct));
    }

    /// <summary>A recipe, a version of it, and a proposal — the three Restrict pins a prompt can hold.</summary>
    private async Task<(Guid RecipeId, Guid VersionId, Guid ProposalId)> SeedLineageAsync(Guid workspaceId)
    {
        await using var scope = ScopeFor(workspaceId, "doomed");
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Soda bread",
            Status = RecipeStatus.Draft,
            CreatedByMembershipId = Guid.NewGuid(),
            UpdatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(Ct);

        var version = new RecipeVersion
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            VersionNumber = 1,
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
        };
        db.RecipeVersions.Add(version);

        var operationId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        db.AiOperations.Add(new AiOperation
        {
            Id = operationId,
            TaskType = AiTaskType.RecipeConcepts,
            Scope = AiOperationScope.NotApplicable,
            Status = AiOperationStatus.Proposed,
            IdempotencyKey = $"prompt-{operationId}",
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = Now,
            StatusChangedAt = Now,
            AvailableAt = Now,
        });
        db.AiProposals.Add(new AiProposal
        {
            Id = proposalId,
            AiOperationId = operationId,
            OutputSchemaVersion = "image.prompt.v1",
            PromptTemplateId = "image.prompt",
            PromptTemplateVersion = "1.0.0",
            PromptTemplateBodyChecksum = "sha256:seed",
            ProviderName = "test-provider",
            ModelName = "test-model",
            CreatedAt = Now,
        });

        await db.SaveChangesAsync(Ct);

        return (recipe.Id, version.Id, proposalId);
    }

    [Fact]
    public async Task The_newest_first_index_orders_the_way_the_library_reads()
    {
        // A descending composite index is provider-specific DDL, so this proves SQL Server both accepted it and
        // can serve the ordering PRM-002 will default to.
        await using var scope = ScopeFor(WorkspaceB);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        foreach (var day in Enumerable.Range(0, 4))
        {
            var record = NewRecord(text: $"Prompt {day}.");
            record.CreatedAt = Now.AddDays(day);
            db.PromptRecords.Add(record);
        }

        await db.SaveChangesAsync(Ct);

        var newestFirst = await db.PromptRecords
            .AsNoTracking()
            .OrderByDescending(record => record.CreatedAt)
            .ThenByDescending(record => record.Id)
            .Select(record => record.Text)
            .ToListAsync(Ct);

        Assert.Equal(["Prompt 3.", "Prompt 2.", "Prompt 1.", "Prompt 0."], newestFirst);
    }

    /// <summary>
    /// PRM-002's projection against the real engine: the preview is truncated by SQL Server rather than in
    /// memory, and the full length comes back beside it.
    /// </summary>
    /// <remarks>
    /// Worth proving here rather than only on SQLite, because the two providers translate this differently —
    /// <c>SUBSTRING</c> against <c>substr</c>, <c>LEN</c> against <c>length</c> — and <c>LEN</c> in particular
    /// has a documented quirk SQLite's does not: it does not count trailing spaces. The prompt text is trimmed
    /// on the way in, so that cannot bite a stored row, and this is where that stops being an assumption.
    /// </remarks>
    [Fact]
    public async Task The_summary_projection_truncates_in_the_engine_and_reports_the_real_length()
    {
        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var longPrompt = NewRecord(text: new string('a', ContentPolicy.PromptTextMaxLength));
        var shortPrompt = NewRecord(text: "Overhead shot.");
        shortPrompt.CreatedAt = Now.AddMinutes(-1);
        db.PromptRecords.AddRange(longPrompt, shortPrompt);
        await db.SaveChangesAsync(Ct);

        var (rows, hasMore) = await Search(db, new PromptSearchCriteria(new PromptSearchFilters(), "scope"), Ct);

        Assert.False(hasMore);
        Assert.Equal(2, rows.Count);

        var first = rows.Single(row => row.Id == longPrompt.Id);
        Assert.Equal(ContentPolicy.PromptPreviewMaxLength, first.TextPreview.Length);
        Assert.Equal(ContentPolicy.PromptTextMaxLength, first.TextLength);

        var second = rows.Single(row => row.Id == shortPrompt.Id);
        Assert.Equal("Overhead shot.", second.TextPreview);
        Assert.Equal("Overhead shot.".Length, second.TextLength);
    }

    /// <summary>
    /// The keyset pages the same way on the real engine, and the channel filter and the search narrow it.
    /// </summary>
    /// <remarks>
    /// The search is the part that cannot be taken on trust from SQLite: the term and the columns are both
    /// lowered in C# and SQL precisely because SQL Server's default collation is case-insensitive and SQLite's
    /// is not, so "the same search means the same thing in both" is a claim only a test on both can make.
    /// </remarks>
    [Fact]
    public async Task The_keyset_the_channel_filter_and_the_search_work_on_the_real_engine()
    {
        await using var scope = ScopeFor(WorkspaceB, slug: "workspace-b");
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        for (var index = 0; index < 5; index++)
        {
            var record = NewRecord(
                text: index == 0 ? "Overhead shot of SODA bread." : $"Prompt {index}.",
                channelKey: index % 2 == 0 ? "instagram" : "blog");
            record.CreatedAt = Now.AddMinutes(index);

            // Labels cleared except on one row, so each search below has exactly one thing it can match. The
            // shared NewRecord labels everything "Soda bread hero", which would make a text search for "soda"
            // match every row through its label and prove nothing about either.
            record.Label = index == 4 ? "Crumb DETAIL" : null;

            db.PromptRecords.Add(record);
        }

        await db.SaveChangesAsync(Ct);

        // Two pages of two, then one: no row repeated and none skipped.
        var seen = new List<Guid>();
        PromptSearchPosition? position = null;

        for (var page = 0; page < 3; page++)
        {
            var criteria = new PromptSearchCriteria(new PromptSearchFilters(), "scope", position, RequestedLimit: 2);
            var (rows, hasMore) = await Search(db, criteria, Ct);

            seen.AddRange(rows.Select(row => row.Id));
            Assert.Equal(page < 2, hasMore);

            if (!hasMore)
            {
                break;
            }

            Assert.True(PromptSearchPosition.TryCreate(
                new Domain.Managers.Paging.ReferenceCursor(rows[^1].SortValue, rows[^1].TieBreaker, "unused"),
                out position));
        }

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());

        var byChannel = new PromptSearchCriteria(new PromptSearchFilters(ChannelKey: "instagram"), "scope");
        Assert.Equal(3, await Count(db, byChannel, Ct));

        // Lowercase terms against mixed-case stored values, which is the collation claim — once against the
        // prompt text and once against the creator's label, because the filter ORs the two.
        var byText = new PromptSearchCriteria(new PromptSearchFilters(Search: "soda"), "scope");
        Assert.Equal(1, await Count(db, byText, Ct));

        var byLabel = new PromptSearchCriteria(new PromptSearchFilters(Search: "detail"), "scope");
        Assert.Equal(1, await Count(db, byLabel, Ct));

        var byNothing = new PromptSearchCriteria(new PromptSearchFilters(Search: "risotto"), "scope");
        Assert.Equal(0, await Count(db, byNothing, Ct));
    }

    /// <summary>
    /// PRM-002's search, its count and its keyset stay inside the resolved workspace on the real engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The existing <c>Each_workspace_sees_only_its_own_library_on_the_real_engine</c> proves the query filter
    /// over the bare <c>DbSet</c>. This proves it over the <em>search</em>, which is a different query: a
    /// lowered <c>LIKE</c> against two columns, a separate <c>COUNT</c> statement, and a keyset predicate — any
    /// of which a hand-written <c>WorkspaceId</c> predicate or a stray <c>IgnoreQueryFilters</c> could widen
    /// without the simpler test noticing.
    /// </para>
    /// <para>
    /// The position handed in is deliberately <strong>the other workspace's row</strong>. A cursor cannot
    /// legitimately carry one — the scope fingerprint refuses it long before here — so this is the forged case,
    /// and it must reach none of their prompts. It is safe by construction, because a position is only ever a
    /// <c>WHERE</c> predicate over a set the filter has already scoped; that is precisely why it is worth
    /// pinning on the engine that evaluates both.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_search_the_count_and_the_keyset_stay_inside_the_workspace_on_the_real_engine()
    {
        Guid theirPosition;
        DateTimeOffset theirCreatedAt;

        await using (var theirs = ScopeFor(WorkspaceB, slug: "workspace-b"))
        {
            var db = theirs.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

            // The same searchable word in both libraries, so a leak would be visible as a count rather than as
            // an absence — and a label only they have.
            var newest = NewRecord(text: "Overhead shot of soda bread.", channelKey: "instagram");
            newest.Label = "Theirs only";
            newest.CreatedAt = Now.AddHours(1);
            db.PromptRecords.Add(newest);
            await db.SaveChangesAsync(Ct);

            theirPosition = newest.Id;
            theirCreatedAt = newest.CreatedAt;
        }

        await using var scope = ScopeFor(WorkspaceA);
        var mine = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var record = NewRecord(text: "Overhead shot of soda bread.", channelKey: "instagram");
        record.Label = null;
        record.CreatedAt = Now;
        mine.PromptRecords.Add(record);
        await mine.SaveChangesAsync(Ct);

        var bySearch = new PromptSearchCriteria(new PromptSearchFilters(Search: "soda"), "scope");
        var (rows, _) = await Search(mine, bySearch, Ct);

        Assert.Equal(record.Id, Assert.Single(rows).Id);
        Assert.Equal(1, await Count(mine, bySearch, Ct));

        // Their label is not reachable from here at all.
        var byTheirLabel = new PromptSearchCriteria(new PromptSearchFilters(Search: "theirs only"), "scope");
        Assert.Equal(0, await Count(mine, byTheirLabel, Ct));

        var byChannel = new PromptSearchCriteria(new PromptSearchFilters(ChannelKey: "instagram"), "scope");
        Assert.Equal(1, await Count(mine, byChannel, Ct));

        // Positioned on their row, which is newer than mine: mine still follows it, theirs is not there to.
        Assert.True(PromptSearchPosition.TryCreate(
            new Domain.Managers.Paging.ReferenceCursor(
                PromptSearchPosition.FormatTimestamp(theirCreatedAt),
                theirPosition.ToString("D"),
                "unused"),
            out var position));

        var (afterTheirs, _) = await Search(
            mine, new PromptSearchCriteria(new PromptSearchFilters(), "scope", position), Ct);

        Assert.Equal(record.Id, Assert.Single(afterTheirs).Id);
        Assert.DoesNotContain(afterTheirs, row => row.Id == theirPosition);
    }

    /// <summary>
    /// The detail read is a primary-key seek, and a key seek is exactly the query a hand-written
    /// <c>WorkspaceId</c> predicate would look unnecessary on — so the real engine is asked whether the global
    /// filter is in the generated SQL.
    /// </summary>
    /// <remarks>
    /// A key lookup is the shape most likely to be "optimised" into <c>FindAsync</c> or a raw
    /// <c>SingleAsync(id)</c> at some later date, either of which would read across the boundary while still
    /// passing a single-workspace test. The row is proved present in its own workspace in the same test, so a
    /// null here cannot be a seeding mistake reading as isolation.
    /// </remarks>
    [Fact]
    public async Task One_prompt_cannot_be_read_by_id_from_the_other_workspace_on_the_real_engine()
    {
        Guid theirId;

        await using (var theirs = ScopeFor(WorkspaceB, slug: "workspace-b"))
        {
            var db = theirs.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var record = NewRecord(text: "Theirs, about risotto.");
            db.PromptRecords.Add(record);
            await db.SaveChangesAsync(Ct);

            theirId = record.Id;

            // Present and readable by id in its own workspace, so the null below is the filter rather than a
            // row that was never written.
            Assert.NotNull(await new PromptRecordRepository(db).FindAsync(theirId, Ct));
        }

        await using var scope = ScopeFor(WorkspaceA);
        var mine = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        Assert.Null(await new PromptRecordRepository(mine).FindAsync(theirId, Ct));

        // And an id that exists nowhere answers identically, which is what makes the two one case.
        Assert.Null(await new PromptRecordRepository(mine).FindAsync(Guid.NewGuid(), Ct));
    }

    private static Task<(IReadOnlyList<PromptSummaryRecord> Rows, bool HasMore)> Search(
        CreatorPantryDbContext db, PromptSearchCriteria criteria, CancellationToken cancellationToken) =>
        new PromptRecordSearchRepository(db).SearchAsync(criteria, cancellationToken);

    private static Task<int> Count(
        CreatorPantryDbContext db, PromptSearchCriteria criteria, CancellationToken cancellationToken) =>
        new PromptRecordSearchRepository(db).CountAsync(criteria, cancellationToken);
}
