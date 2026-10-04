using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Content;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The weekly themes against a real SQL Server, for the things SQLite cannot answer: that the migration's DDL is
/// accepted, that the check constraints and the filtered unique index are written in a dialect SQL Server parses
/// — <c>trim([Key])</c> over a reserved word among them — and that the ordered two-batch replace behaves the same
/// way against the real engine.
/// </summary>
/// <remarks>
/// The lost-race path is driven through the data layer with a plan that deliberately double-books a day, rather
/// than by racing two requests. That is the honest test of the guard: the unique index is the authority on
/// one-theme-per-day, and the data layer has to answer a violation with a conflict however it arose. A genuine
/// two-transaction race would be a timing test, which proves less and fails randomly.
/// </remarks>
public sealed class WorkspaceWeeklyThemeSqlServerTests : IAsyncLifetime
{
    private static readonly Guid WorkspaceA = Guid.NewGuid();

    private static readonly Guid WorkspaceB = Guid.NewGuid();

    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private const string Actor = "user-1";

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
            .AddIdempotency(new ConfigurationBuilder().Build())
            .AddContentModule()
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

    private AsyncServiceScope ScopeFor(Guid workspaceId)
    {
        var scope = _provider!.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner,
            Actor);

        return scope;
    }

    private static WeeklyThemeInput Theme(DayOfWeek day, string key, string name) =>
        new() { Day = day, Key = key, DisplayName = name };

    private async Task<WeeklyThemeWeekServiceModel> ReplaceAsync(Guid workspaceId, params WeeklyThemeInput[] themes)
    {
        await using var scope = ScopeFor(workspaceId);
        var result = await scope.ServiceProvider.GetRequiredService<IWorkspaceWeeklyThemeBusiness>()
            .ReplaceAsync(Actor, new ReplaceWeeklyThemesViewModel { Themes = themes }, Ct);

        Assert.True(result.Succeeded, result.Error?.Code);

        return result.Value!;
    }

    [Fact]
    public async Task The_week_round_trips_through_the_real_engine()
    {
        var week = await ReplaceAsync(
            WorkspaceA,
            Theme(DayOfWeek.Sunday, "sunday-supper", "Sunday supper"),
            Theme(DayOfWeek.Monday, "meat-free-monday", "Meat-free Monday"));

        // Monday leads and Sunday closes, which is the one thing SQL Server's own day numbering would get wrong.
        Assert.Equal(["meat-free-monday", "sunday-supper"], week.Themes.Select(theme => theme.Key));
    }

    [Fact]
    public async Task A_day_changing_hands_needs_no_retry_against_a_real_index()
    {
        // The case the two-batch write exists for, against the engine whose index actually enforces it.
        await ReplaceAsync(WorkspaceB, Theme(DayOfWeek.Monday, "meat-free-monday", "Meat-free Monday"));

        var week = await ReplaceAsync(WorkspaceB, Theme(DayOfWeek.Monday, "pasta-monday", "Pasta Monday"));

        Assert.Null(week.Themes.Single(theme => theme.Key == "pasta-monday").RetiredAt);
        Assert.NotNull(week.Themes.Single(theme => theme.Key == "meat-free-monday").RetiredAt);
    }

    [Fact]
    public async Task A_plan_that_double_books_a_day_is_refused_as_a_conflict()
    {
        await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Wednesday, "one-pan-wednesday", "One-pan Wednesday"));

        await using var scope = ScopeFor(WorkspaceA);
        var dataLayer = scope.ServiceProvider.GetRequiredService<IWorkspaceWeeklyThemeDataLayer>();

        // Places a second theme on Wednesday without releasing the first: exactly what a concurrent replace
        // amounts to by the time it reaches the database.
        var saved = await dataLayer.ReplaceAsync(
            new WeeklyThemeWeekPlan(
                Now,
                [new WeeklyThemeChange("sheet-pan-wednesday", DayOfWeek.Wednesday, "Sheet-pan Wednesday", null, Retire: false)]),
            new AuditEntry(
                Actor,
                ContentAuditActions.WeeklyThemesReplaced,
                ContentAuditActions.WeeklyThemesResourceType,
                WorkspaceA.ToString("D"),
                Guid.NewGuid(),
                "Replaced the workspace's weekly themes."),
            Ct);

        Assert.False(saved);

        // And nothing was written: not the second theme, and not the audit row that would have claimed it was.
        await using var after = ScopeFor(WorkspaceA);
        var db = after.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        Assert.Equal(1, await db.WorkspaceWeeklyThemes.CountAsync(Ct));
        Assert.Equal(1, await db.AuditLogs.CountAsync(log => log.Action == ContentAuditActions.WeeklyThemesReplaced, Ct));
    }

    [Fact]
    public async Task The_live_day_index_is_per_workspace_on_the_real_engine()
    {
        // The filtered unique index leads with WorkspaceId, so two workspaces hold a live Monday under the same
        // key at once. Proved here rather than only on SQLite, because this is the index that actually ships.
        await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Monday, "meat-free-monday", "A's Monday"));
        await ReplaceAsync(WorkspaceB, Theme(DayOfWeek.Monday, "meat-free-monday", "B's Monday"));

        await using var scopeA = ScopeFor(WorkspaceA);
        var weekA = await scopeA.ServiceProvider.GetRequiredService<IWorkspaceWeeklyThemeBusiness>().GetAsync(Ct);
        Assert.Equal("A's Monday", Assert.Single(weekA.Value!.Themes).DisplayName);

        await using var scopeB = ScopeFor(WorkspaceB);
        var weekB = await scopeB.ServiceProvider.GetRequiredService<IWorkspaceWeeklyThemeBusiness>().GetAsync(Ct);
        Assert.Equal("B's Monday", Assert.Single(weekB.Value!.Themes).DisplayName);
    }

    [Fact]
    public async Task A_conflict_inside_a_joined_transaction_leaves_the_week_as_it_was()
    {
        // The idempotency-keyed shape: the replace runs inside a transaction it did not open. A failure has to
        // answer false and leave nothing staged for the outer transaction to commit on its way out.
        await ReplaceAsync(WorkspaceB, Theme(DayOfWeek.Tuesday, "taco-tuesday", "Taco Tuesday"));

        await using var scope = ScopeFor(WorkspaceB);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var dataLayer = scope.ServiceProvider.GetRequiredService<IWorkspaceWeeklyThemeDataLayer>();

        await using var transaction = await db.Database.BeginTransactionAsync(Ct);

        var saved = await dataLayer.ReplaceAsync(
            new WeeklyThemeWeekPlan(
                Now,
                [new WeeklyThemeChange("tortilla-tuesday", DayOfWeek.Tuesday, "Tortilla Tuesday", null, Retire: false)]),
            new AuditEntry(
                Actor,
                ContentAuditActions.WeeklyThemesReplaced,
                ContentAuditActions.WeeklyThemesResourceType,
                WorkspaceB.ToString("D"),
                Guid.NewGuid(),
                "Replaced the workspace's weekly themes."),
            Ct);

        Assert.False(saved);

        // Committing the outer transaction must carry nothing the failed replace left behind.
        await transaction.CommitAsync(Ct);

        await using var after = ScopeFor(WorkspaceB);
        var theme = Assert.Single(await after.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .WorkspaceWeeklyThemes.ToListAsync(Ct));
        Assert.Equal("taco-tuesday", theme.Key);
        Assert.Null(theme.RetiredAt);
    }

    [Fact]
    public async Task A_duplicate_key_is_refused_by_the_engine_even_once_retired()
    {
        await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Thursday, "throwback-thursday", "Throwback Thursday"));
        await ReplaceAsync(WorkspaceA);

        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceWeeklyThemes.Add(new WorkspaceWeeklyTheme
        {
            Id = Guid.NewGuid(),
            Day = DayOfWeek.Friday,
            Key = "throwback-thursday",
            DisplayName = "Throwback, moved",
            CreatedAt = Now,
            UpdatedAt = Now,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Theory]
    [InlineData("", "A name")]
    [InlineData("   ", "A name")]
    [InlineData("a-key", "")]
    [InlineData("a-key", "   ")]
    public async Task The_blank_checks_are_written_in_a_dialect_SQL_Server_enforces(string key, string displayName)
    {
        // Specifically that trim([Key]) parses: Key is a reserved word, so the bracket quoting is load-bearing.
        await using var scope = ScopeFor(WorkspaceB);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceWeeklyThemes.Add(new WorkspaceWeeklyTheme
        {
            Id = Guid.NewGuid(),
            Day = DayOfWeek.Saturday,
            Key = key,
            DisplayName = displayName,
            CreatedAt = Now,
            UpdatedAt = Now,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_day_outside_the_week_is_refused_by_the_engine()
    {
        await using var scope = ScopeFor(WorkspaceB);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceWeeklyThemes.Add(new WorkspaceWeeklyTheme
        {
            Id = Guid.NewGuid(),
            Day = (DayOfWeek)7,
            Key = "eighth-day",
            DisplayName = "The eighth day",
            CreatedAt = Now,
            UpdatedAt = Now,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }
}
