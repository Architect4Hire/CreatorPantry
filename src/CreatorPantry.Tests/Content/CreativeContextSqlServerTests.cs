using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The creative context against a real SQL Server, for the things SQLite cannot answer: that the migration's DDL
/// is accepted at all — three tables, eight restricted keys and one cascade, all reachable from one workspace —
/// that the checks and filtered indexes are written in a dialect SQL Server parses, and that the row version
/// actually moves.
/// </summary>
/// <remarks>
/// The cascade question is the reason this exists rather than being folded into the SQLite aggregate tests.
/// SQLite accepts any number of cascade paths into a table; SQL Server refuses the DDL outright when there are
/// two. A reference row hangs off a context that cascades from the workspace while naming six tables that
/// cascade from that same workspace, which is exactly the shape that goes wrong.
/// </remarks>
public sealed class CreativeContextSqlServerTests : IAsyncLifetime
{
    private static readonly Guid WorkspaceA = Guid.NewGuid();

    private static readonly Guid WorkspaceB = Guid.NewGuid();

    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

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

    private async Task<T> InAsync<T>(Guid workspaceId, Func<CreatorPantryDbContext, Task<T>> work, string? slug = null)
    {
        await using var scope = _provider!.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            slug ?? (workspaceId == WorkspaceA ? "workspace-a" : "workspace-b"),
            Guid.NewGuid(),
            WorkspaceRole.Owner,
            "user-1");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    private Task<CreativeContext> SeedContextAsync(Guid workspaceId, string? title = "Soda bread, autumn", string? slug = null) =>
        InAsync(workspaceId, async db =>
        {
            var context = CreativeContextSeeds.NewContext(Now, title);
            db.CreativeContexts.Add(context);
            await db.SaveChangesAsync(Ct);

            return context;
        }, slug);

    private Task SaveReferenceAsync(
        Guid workspaceId,
        CreativeContext context,
        CreativeContextReferenceKind kind,
        Action<CreativeContextReference> shape,
        int sortOrder = 0,
        string? slug = null) =>
        InAsync(workspaceId, async db =>
        {
            var reference = CreativeContextSeeds.Reference(context, kind, sortOrder, Now);
            shape(reference);
            db.CreativeContextReferences.Add(reference);
            await db.SaveChangesAsync(Ct);

            return 0;
        }, slug);

    /// <summary>One reference of every kind that has a foreign key, plus the two that do not.</summary>
    private async Task ReferenceEverythingAsync(Guid workspaceId, CreativeContext context, string? slug = null)
    {
        var (recipeId, versionId) = await InAsync(workspaceId, db => CreativeContextSeeds.RecipeAsync(db, Now, Ct), slug);
        var requestId = await InAsync(workspaceId, db => CreativeContextSeeds.ConceptRequestAsync(db, Now, Ct), slug);
        var assetId = await InAsync(workspaceId, db => CreativeContextSeeds.MediaAssetAsync(db, workspaceId, Now, Ct), slug);
        var imageId = await InAsync(workspaceId, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct), slug);
        var promptId = await InAsync(workspaceId, db => CreativeContextSeeds.PromptRecordAsync(db, Now, Ct), slug);

        await SaveReferenceAsync(workspaceId, context, CreativeContextReferenceKind.Recipe, reference =>
        {
            reference.RecipeId = recipeId;
            reference.RecipeVersionId = versionId;
        }, 0, slug);
        await SaveReferenceAsync(workspaceId, context, CreativeContextReferenceKind.RecipeConcept, reference =>
        {
            reference.ConceptRequestId = requestId;
            reference.ConceptId = Guid.NewGuid();
        }, 1, slug);
        await SaveReferenceAsync(workspaceId, context, CreativeContextReferenceKind.DamAsset, reference =>
        {
            reference.MediaAssetId = assetId;
            reference.MediaAssetVersionNumber = 1;
        }, 2, slug);
        await SaveReferenceAsync(workspaceId, context, CreativeContextReferenceKind.GeneratedImage,
            reference => reference.GeneratedImageId = imageId, 3, slug);
        await SaveReferenceAsync(workspaceId, context, CreativeContextReferenceKind.PromptRecord,
            reference => reference.PromptRecordId = promptId, 4, slug);
        await SaveReferenceAsync(workspaceId, context, CreativeContextReferenceKind.SocialPackage,
            reference => reference.SocialPackageId = Guid.NewGuid(), 5, slug);
    }

    [Fact]
    public async Task A_context_with_one_of_every_reference_round_trips_through_the_real_engine()
    {
        var context = await SeedContextAsync(WorkspaceA);

        await InAsync(WorkspaceA, async db =>
        {
            db.CreativeContextChannels.AddRange(
                CreativeContextSeeds.Channel(context, "blog", 0),
                CreativeContextSeeds.Channel(context, "instagram", 1));
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        await ReferenceEverythingAsync(WorkspaceA, context);

        var loaded = await InAsync(WorkspaceA, db => db.CreativeContexts.AsNoTracking()
            .Include(item => item.Channels)
            .Include(item => item.References)
            .SingleAsync(item => item.Id == context.Id, Ct));

        // Stamped by the ownership interceptor from the resolved context, never set by hand.
        Assert.Equal(WorkspaceA, loaded.WorkspaceId);
        Assert.All(loaded.References, reference => Assert.Equal(WorkspaceA, reference.WorkspaceId));
        Assert.Equal(["blog", "instagram"], loaded.Channels.OrderBy(channel => channel.SortOrder).Select(channel => channel.ChannelKey));
        Assert.Equal(
            Enum.GetValues<CreativeContextReferenceKind>(),
            loaded.References.OrderBy(reference => reference.SortOrder).Select(reference => reference.Kind));
        Assert.Equal(DayOfWeek.Monday, loaded.Day);
    }

    [Fact]
    public async Task A_context_cannot_name_another_workspaces_records_even_round_the_seam()
    {
        var context = await SeedContextAsync(WorkspaceA, title: "Borrowing");

        var (recipeId, _) = await InAsync(WorkspaceB, db => CreativeContextSeeds.RecipeAsync(db, Now, Ct));
        var requestId = await InAsync(WorkspaceB, db => CreativeContextSeeds.ConceptRequestAsync(db, Now, Ct));
        var assetId = await InAsync(WorkspaceB, db => CreativeContextSeeds.MediaAssetAsync(db, WorkspaceB, Now, Ct));
        var imageId = await InAsync(WorkspaceB, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));
        var promptId = await InAsync(WorkspaceB, db => CreativeContextSeeds.PromptRecordAsync(db, Now, Ct));

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            WorkspaceA, context, CreativeContextReferenceKind.Recipe, reference => reference.RecipeId = recipeId));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            WorkspaceA, context, CreativeContextReferenceKind.RecipeConcept, reference =>
            {
                reference.ConceptRequestId = requestId;
                reference.ConceptId = Guid.NewGuid();
            }));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            WorkspaceA, context, CreativeContextReferenceKind.DamAsset, reference => reference.MediaAssetId = assetId));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            WorkspaceA, context, CreativeContextReferenceKind.GeneratedImage,
            reference => reference.GeneratedImageId = imageId));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            WorkspaceA, context, CreativeContextReferenceKind.PromptRecord,
            reference => reference.PromptRecordId = promptId));
    }

    [Fact]
    public async Task The_kind_and_column_rule_is_written_in_a_dialect_SQL_Server_enforces()
    {
        var context = await SeedContextAsync(WorkspaceA, title: "Misshapen");

        // A kind with no column, a kind with a neighbour's column, and a number that is not a kind.
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            WorkspaceA, context, CreativeContextReferenceKind.GeneratedImage, _ => { }));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            WorkspaceA, context, CreativeContextReferenceKind.SocialPackage, reference =>
            {
                reference.SocialPackageId = Guid.NewGuid();
                reference.ConceptId = Guid.NewGuid();
            }));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            WorkspaceA, context, (CreativeContextReferenceKind)7,
            reference => reference.SocialPackageId = Guid.NewGuid()));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("brief")]
    [InlineData("theme")]
    [InlineData("day")]
    public async Task The_blank_and_range_checks_are_enforced_by_the_engine(string field) =>
        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(WorkspaceA, async db =>
        {
            var context = CreativeContextSeeds.NewContext(Now);

            switch (field)
            {
                case "title":
                    context.WorkingTitle = "   ";
                    break;
                case "brief":
                    context.PictureBrief = string.Empty;
                    break;
                case "theme":
                    context.WeeklyThemeKey = " ";
                    break;
                case "day":
                    context.Day = (DayOfWeek)7;
                    break;
            }

            db.CreativeContexts.Add(context);
            await db.SaveChangesAsync(Ct);

            return 0;
        }));

    [Fact]
    public async Task The_filtered_unique_indexes_refuse_a_repeat_and_ignore_each_others_nulls()
    {
        var context = await SeedContextAsync(WorkspaceA, title: "Repeats");
        var imageId = await InAsync(WorkspaceA, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));

        await SaveReferenceAsync(WorkspaceA, context, CreativeContextReferenceKind.GeneratedImage,
            reference => reference.GeneratedImageId = imageId);

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            WorkspaceA, context, CreativeContextReferenceKind.GeneratedImage,
            reference => reference.GeneratedImageId = imageId, sortOrder: 1));

        // SQL Server treats NULLs as equal in a unique index, so without the filters these three — each null in
        // every other kind's column — would collide with one another.
        foreach (var order in Enumerable.Range(1, 3))
        {
            await SaveReferenceAsync(WorkspaceA, context, CreativeContextReferenceKind.SocialPackage,
                reference => reference.SocialPackageId = Guid.NewGuid(), sortOrder: order);
        }

        Assert.Equal(4, await InAsync(WorkspaceA, db => db.CreativeContextReferences
            .CountAsync(reference => reference.CreativeContextId == context.Id, Ct)));
    }

    [Fact]
    public async Task A_stale_edit_is_refused_and_the_row_version_moves_on_every_write()
    {
        var context = await SeedContextAsync(WorkspaceA, title: "Contended");

        await using var firstScope = _provider!.CreateAsyncScope();
        await using var secondScope = _provider!.CreateAsyncScope();

        foreach (var scope in new[] { firstScope, secondScope })
        {
            scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
                WorkspaceA, "workspace-a", Guid.NewGuid(), WorkspaceRole.Owner, "user-1");
        }

        var firstDb = firstScope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var first = await firstDb.CreativeContexts.SingleAsync(item => item.Id == context.Id, Ct);
        var second = await secondDb.CreativeContexts.SingleAsync(item => item.Id == context.Id, Ct);
        var readWith = first.RowVersion.ToArray();

        first.WorkingTitle = "First writer";
        await firstDb.SaveChangesAsync(Ct);

        Assert.NotEqual(readWith, first.RowVersion);

        // Read before the first write landed, so its UPDATE quotes a version the row no longer has.
        second.WorkingTitle = "Second writer";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync(Ct));

        Assert.Equal(
            "First writer",
            (await InAsync(WorkspaceA, db => db.CreativeContexts.AsNoTracking()
                .SingleAsync(item => item.Id == context.Id, Ct))).WorkingTitle);
    }

    /// <summary>
    /// The write seam's own stale path, which only a real <c>rowversion</c> can exercise: two editors read the
    /// same context, and the second one's change to a <em>child</em> row is refused along with the root.
    /// </summary>
    /// <remarks>
    /// The child case is the one worth proving. Adding a reference touches no column of the context itself, so
    /// without Business moving <c>UpdatedAt</c> the root would not be written, its row version would not be in
    /// any <c>WHERE</c> clause, and two editors would both succeed.
    /// </remarks>
    [Fact]
    public async Task The_data_layer_refuses_a_second_editors_reference_as_stale()
    {
        var context = await SeedContextAsync(WorkspaceA, title: "Two editors");

        await using var firstScope = _provider!.CreateAsyncScope();
        await using var secondScope = _provider!.CreateAsyncScope();

        var layers = new List<(CreatorPantryDbContext Db, CreativeContextDataLayer Layer)>();

        foreach (var scope in new[] { firstScope, secondScope })
        {
            scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
                WorkspaceA, "workspace-a", Guid.NewGuid(), WorkspaceRole.Owner, "user-1");

            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            layers.Add((db, new CreativeContextDataLayer(
                new CreativeContextRepository(db), scope.ServiceProvider.GetRequiredService<IAuditWriter>(), db)));
        }

        var first = (await layers[0].Layer.FindForUpdateAsync(context.Id, Ct))!;
        var second = (await layers[1].Layer.FindForUpdateAsync(context.Id, Ct))!;

        foreach (var (editor, order) in new[] { (first, 0), (second, 1) })
        {
            var reference = CreativeContextSeeds.Reference(editor, CreativeContextReferenceKind.SocialPackage, order, Now);
            reference.SocialPackageId = Guid.NewGuid();
            editor.References.Add(reference);
            editor.UpdatedAt = Now.AddMinutes(order + 1);
        }

        Assert.Equal(CreativeContextWrite.Saved, await layers[0].Layer.SaveAsync(first, audit: null, Ct));
        Assert.Equal(CreativeContextWrite.Stale, await layers[1].Layer.SaveAsync(second, audit: null, Ct));

        // One reference, the first editor's: the second's insert went with its refused root update.
        Assert.Equal(1, await InAsync(WorkspaceA, db => db.CreativeContextReferences
            .CountAsync(reference => reference.CreativeContextId == context.Id, Ct)));
    }

    [Fact]
    public async Task The_recent_list_index_orders_live_contexts_newest_first()
    {
        // A filtered, descending composite index is provider-specific DDL twice over.
        var ids = new List<Guid>();

        await InAsync(WorkspaceB, async db =>
        {
            foreach (var day in Enumerable.Range(0, 4))
            {
                var context = CreativeContextSeeds.NewContext(Now, $"Piece {day}");
                context.UpdatedAt = Now.AddDays(day);
                context.ArchivedAt = day == 3 ? Now.AddDays(5) : null;
                db.CreativeContexts.Add(context);
                ids.Add(context.Id);
            }

            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var recent = await InAsync(WorkspaceB, db => db.CreativeContexts
            .Where(context => context.ArchivedAt == null && ids.Contains(context.Id))
            .OrderByDescending(context => context.UpdatedAt)
            .ThenByDescending(context => context.Id)
            .Select(context => context.WorkingTitle)
            .ToListAsync(Ct));

        Assert.Equal(["Piece 2", "Piece 1", "Piece 0"], recent);
    }

    [Fact]
    public async Task Each_workspace_sees_only_its_own_contexts_on_the_real_engine()
    {
        var a = await SeedContextAsync(WorkspaceA, title: "A's piece");
        var b = await SeedContextAsync(WorkspaceB, title: "B's piece");

        Assert.Null(await InAsync(WorkspaceA, db => db.CreativeContexts.AsNoTracking()
            .FirstOrDefaultAsync(context => context.Id == b.Id, Ct)));
        Assert.Null(await InAsync(WorkspaceB, db => db.CreativeContexts.AsNoTracking()
            .FirstOrDefaultAsync(context => context.Id == a.Id, Ct)));
        Assert.DoesNotContain(
            "A's piece",
            await InAsync(WorkspaceB, db => db.CreativeContexts.Select(context => context.WorkingTitle).ToListAsync(Ct)));
    }

    [Fact]
    public async Task Deleting_a_workspace_still_succeeds_with_a_fully_referenced_context_in_it()
    {
        // The question only a real engine answers. A reference cascades from Workspace through its context
        // while holding restricted keys to six tables that cascade from that same workspace. If SQL Server
        // checked those NO ACTION constraints mid-cascade rather than at the end of the statement, a workspace
        // holding one context could never be erased.
        var workspaceId = Guid.NewGuid();

        await using (var seed = _provider!.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Doomed", Slug = "doomed", CreatedAt = Now });
            await db.SaveChangesAsync(Ct);
        }

        var context = await SeedContextAsync(workspaceId, slug: "doomed");
        await InAsync(workspaceId, async db =>
        {
            db.CreativeContextChannels.Add(CreativeContextSeeds.Channel(context, "instagram", 0));
            await db.SaveChangesAsync(Ct);

            return 0;
        }, "doomed");
        await ReferenceEverythingAsync(workspaceId, context, "doomed");

        // The workspace row is not workspace-owned, so it is removed from an unresolved scope.
        await using var scope = _provider!.CreateAsyncScope();
        var unresolved = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        unresolved.Workspaces.Remove(await unresolved.Workspaces.SingleAsync(item => item.Id == workspaceId, Ct));
        await unresolved.SaveChangesAsync(Ct);

        // Counted with raw SQL rather than IgnoreQueryFilters: the erasure is what is under test, and nothing
        // here needs a filtered set to see across workspaces.
        foreach (var table in new[] { "CreativeContexts", "CreativeContextChannels", "CreativeContextReferences" })
        {
            var remaining = await unresolved.Database
                .SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM {table} WHERE WorkspaceId = {{0}}", workspaceId)
                .SingleAsync(Ct);

            Assert.Equal(0, remaining);
        }
    }
}
