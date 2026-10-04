using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Content;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// What a week replace does: what it creates, what it keeps, what it retires rather than deletes, and what it
/// refuses. Over a real database rather than a fake data layer, because the ordering a replace needs and the
/// indexes that enforce one-theme-per-day are the behaviour under test.
/// </summary>
public sealed class WorkspaceWeeklyThemeBusinessTests : IAsyncLifetime
{
    private static readonly Guid WorkspaceA = Guid.NewGuid();

    private static readonly Guid WorkspaceB = Guid.NewGuid();

    private const string Actor = "user-1";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero));

    private ServiceProvider _provider = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync(Ct);

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddIdempotency(new ConfigurationBuilder().Build())
            .AddContentModule()
            .AddSingleton<IClock>(_clock)
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        await db.Database.EnsureCreatedAsync(Ct);

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = _clock.UtcNow },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "workspace-b", CreatedAt = _clock.UtcNow });
        await db.SaveChangesAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private AsyncServiceScope ScopeFor(Guid workspaceId)
    {
        var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner,
            Actor);

        return scope;
    }

    private static WeeklyThemeInput Theme(DayOfWeek day, string key, string name, string? description = null) =>
        new() { Day = day, Key = key, DisplayName = name, Description = description };

    /// <summary>One replace in its own scope, the way a request arrives.</summary>
    private async Task<WeeklyThemeWeekServiceModel> ReplaceAsync(Guid workspaceId, params WeeklyThemeInput[] themes)
    {
        await using var scope = ScopeFor(workspaceId);
        var business = scope.ServiceProvider.GetRequiredService<IWorkspaceWeeklyThemeBusiness>();
        var result = await business.ReplaceAsync(Actor, new ReplaceWeeklyThemesViewModel { Themes = themes }, Ct);

        Assert.True(result.Succeeded, result.Error?.Code);

        return result.Value!;
    }

    private async Task<WeeklyThemeWeekServiceModel> ReadAsync(Guid workspaceId)
    {
        await using var scope = ScopeFor(workspaceId);
        var business = scope.ServiceProvider.GetRequiredService<IWorkspaceWeeklyThemeBusiness>();

        return (await business.GetAsync(Ct)).Value!;
    }

    private static WeeklyThemeServiceModel? Find(WeeklyThemeWeekServiceModel week, string key) =>
        week.Themes.SingleOrDefault(theme => theme.Key == key);

    [Fact]
    public async Task A_workspace_starts_with_an_empty_week()
    {
        // Not a 404: an empty week is a real state, and a consumer reading it has no theme for the day.
        Assert.Empty((await ReadAsync(WorkspaceA)).Themes);
    }

    [Fact]
    public async Task A_replace_writes_the_week_the_creator_submitted()
    {
        var week = await ReplaceAsync(
            WorkspaceA,
            Theme(DayOfWeek.Friday, "fakeaway-friday", "Fakeaway Friday", "Takeaway favourites, made at home."),
            Theme(DayOfWeek.Monday, "meat-free-monday", "Meat-free Monday"));

        // Monday first: DayOfWeek starts at Sunday, an editorial week does not.
        Assert.Equal(["meat-free-monday", "fakeaway-friday"], week.Themes.Select(theme => theme.Key));
        Assert.All(week.Themes, theme => Assert.Null(theme.RetiredAt));
        Assert.All(week.Themes, theme => Assert.Equal(1, theme.Revision));
        Assert.Equal("Takeaway favourites, made at home.", Find(week, "fakeaway-friday")!.Description);
    }

    [Fact]
    public async Task A_replace_that_asks_for_the_week_already_stored_writes_nothing()
    {
        await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Monday, "meat-free-monday", "Meat-free Monday"));
        var before = await ReadAsync(WorkspaceA);

        _clock.Advance(TimeSpan.FromHours(1));
        var after = await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Monday, "meat-free-monday", "Meat-free Monday"));

        Assert.Equal(1, Find(after, "meat-free-monday")!.Revision);
        Assert.Equal(before.Themes[0].UpdatedAt, after.Themes[0].UpdatedAt);
        Assert.Empty(await AuditedAsync(WorkspaceA, ContentAuditActions.WeeklyThemesReplaced, since: 1));
    }

    [Fact]
    public async Task Leaving_a_theme_out_retires_it_and_its_key_still_resolves()
    {
        // The rule the whole entity is shaped around: a record that stored "taco-tuesday" last March keeps
        // reading as Taco Tuesday after the creator drops it from the week.
        await ReplaceAsync(
            WorkspaceA,
            Theme(DayOfWeek.Monday, "meat-free-monday", "Meat-free Monday"),
            Theme(DayOfWeek.Tuesday, "taco-tuesday", "Taco Tuesday"));

        _clock.Advance(TimeSpan.FromDays(1));
        var week = await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Monday, "meat-free-monday", "Meat-free Monday"));

        var retired = Find(week, "taco-tuesday")!;
        Assert.Equal(_clock.UtcNow, retired.RetiredAt);
        Assert.Equal("Taco Tuesday", retired.DisplayName);
        Assert.Equal(DayOfWeek.Tuesday, retired.Day);
        Assert.Equal(2, retired.Revision);

        // Live themes lead; the retired one follows.
        Assert.Equal(["meat-free-monday", "taco-tuesday"], week.Themes.Select(theme => theme.Key));

        await using var scope = ScopeFor(WorkspaceA);
        var found = await scope.ServiceProvider.GetRequiredService<IWorkspaceWeeklyThemeBusiness>()
            .FindAsync("taco-tuesday", Ct);
        Assert.True(found.Succeeded);
        Assert.Equal("Taco Tuesday", found.Value!.DisplayName);
    }

    [Fact]
    public async Task Submitting_a_retired_key_brings_that_theme_back()
    {
        await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Tuesday, "taco-tuesday", "Taco Tuesday"));
        await ReplaceAsync(WorkspaceA);

        _clock.Advance(TimeSpan.FromDays(7));
        var week = await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Tuesday, "taco-tuesday", "Taco Tuesday again"));

        var revived = Find(week, "taco-tuesday")!;
        Assert.Null(revived.RetiredAt);
        Assert.Equal("Taco Tuesday again", revived.DisplayName);
        Assert.Equal(3, revived.Revision);
        Assert.Single(week.Themes);
    }

    [Fact]
    public async Task A_new_theme_may_take_a_day_an_old_one_is_leaving()
    {
        // The case a single statement batch cannot do: Monday changes hands, so its old theme has to release the
        // day before the new one claims it or the live-day index fires.
        await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Monday, "meat-free-monday", "Meat-free Monday"));

        var week = await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Monday, "pasta-monday", "Pasta Monday"));

        Assert.Null(Find(week, "pasta-monday")!.RetiredAt);
        Assert.NotNull(Find(week, "meat-free-monday")!.RetiredAt);
        Assert.Single(week.Themes, theme => theme.RetiredAt is null);
    }

    [Fact]
    public async Task Two_themes_may_swap_days_in_one_replace()
    {
        // The case that defeats statement ordering entirely: both writes are updates, and either one alone
        // collides with the other's day.
        await ReplaceAsync(
            WorkspaceA,
            Theme(DayOfWeek.Monday, "soup-day", "Soup day"),
            Theme(DayOfWeek.Tuesday, "pasta-day", "Pasta day"));

        var week = await ReplaceAsync(
            WorkspaceA,
            Theme(DayOfWeek.Tuesday, "soup-day", "Soup day"),
            Theme(DayOfWeek.Monday, "pasta-day", "Pasta day"));

        Assert.Equal(DayOfWeek.Tuesday, Find(week, "soup-day")!.Day);
        Assert.Equal(DayOfWeek.Monday, Find(week, "pasta-day")!.Day);
        Assert.All(week.Themes, theme => Assert.Null(theme.RetiredAt));
        Assert.Equal(2, week.Themes.Count);
    }

    [Fact]
    public async Task A_rename_keeps_the_key_and_the_row()
    {
        await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Monday, "meat-free-monday", "Meat-free Monday"));

        var week = await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Monday, "meat-free-monday", "Meatless Monday"));

        var theme = Assert.Single(week.Themes);
        Assert.Equal("meat-free-monday", theme.Key);
        Assert.Equal("Meatless Monday", theme.DisplayName);
        Assert.Equal(2, theme.Revision);
    }

    [Fact]
    public async Task An_unknown_key_is_not_found_and_so_is_another_workspaces()
    {
        await ReplaceAsync(WorkspaceB, Theme(DayOfWeek.Tuesday, "taco-tuesday", "Taco Tuesday"));

        await using var scope = ScopeFor(WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IWorkspaceWeeklyThemeBusiness>();

        foreach (var key in new[] { "taco-tuesday", "never-existed", "NOT A KEY", "" })
        {
            var result = await business.FindAsync(key, Ct);
            Assert.False(result.Succeeded);
            Assert.Equal(ContentErrorCodes.WeeklyThemeNotFound, result.Error!.Code);
        }
    }

    [Fact]
    public async Task A_key_both_workspaces_hold_resolves_to_the_callers_own_theme()
    {
        // The repository looks a theme up by key alone, so this is the case where only the query filter decides
        // which of two identically keyed themes a caller gets.
        await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Monday, "meat-free-monday", "A's Monday"));
        await ReplaceAsync(WorkspaceB, Theme(DayOfWeek.Monday, "meat-free-monday", "B's Monday"));

        await using var scopeA = ScopeFor(WorkspaceA);
        var fromA = await scopeA.ServiceProvider.GetRequiredService<IWorkspaceWeeklyThemeBusiness>()
            .FindAsync("meat-free-monday", Ct);
        Assert.Equal("A's Monday", fromA.Value!.DisplayName);

        await using var scopeB = ScopeFor(WorkspaceB);
        var business = scopeB.ServiceProvider.GetRequiredService<IWorkspaceWeeklyThemeBusiness>();
        Assert.Equal("B's Monday", (await business.FindAsync("meat-free-monday", Ct)).Value!.DisplayName);

        // And deleting it in B leaves A's alone, under the same key.
        Assert.True((await business.DeleteAsync(Actor, "meat-free-monday", Ct)).Succeeded);
        Assert.Equal("A's Monday", Assert.Single((await ReadAsync(WorkspaceA)).Themes).DisplayName);
        Assert.Empty((await ReadAsync(WorkspaceB)).Themes);
    }

    [Fact]
    public async Task One_workspaces_week_is_invisible_to_the_other()
    {
        await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Monday, "meat-free-monday", "A's Monday"));
        await ReplaceAsync(WorkspaceB, Theme(DayOfWeek.Monday, "meat-free-monday", "B's Monday"));

        Assert.Equal("A's Monday", Assert.Single((await ReadAsync(WorkspaceA)).Themes).DisplayName);
        Assert.Equal("B's Monday", Assert.Single((await ReadAsync(WorkspaceB)).Themes).DisplayName);

        // And a replace in one does not reach into the other: A clearing its week leaves B's live.
        await ReplaceAsync(WorkspaceA);
        Assert.Null(Assert.Single((await ReadAsync(WorkspaceB)).Themes).RetiredAt);
        Assert.NotNull(Assert.Single((await ReadAsync(WorkspaceA)).Themes).RetiredAt);
    }

    [Fact]
    public async Task Deleting_a_theme_leaves_its_key_resolving_to_nothing()
    {
        await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Tuesday, "taco-tuesday", "Taco Tuesday"));

        await using var scope = ScopeFor(WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IWorkspaceWeeklyThemeBusiness>();
        var result = await business.DeleteAsync(Actor, "taco-tuesday", Ct);

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Empty(result.Value!.Themes);

        // A record that stored the key keeps its text and gets no theme back — never an error, never a cascade.
        var found = await business.FindAsync("taco-tuesday", Ct);
        Assert.False(found.Succeeded);
        Assert.Equal(ContentErrorCodes.WeeklyThemeNotFound, found.Error!.Code);

        // Deleting it again is the same answer a key that never existed gets.
        var again = await business.DeleteAsync(Actor, "taco-tuesday", Ct);
        Assert.Equal(ContentErrorCodes.WeeklyThemeNotFound, again.Error!.Code);
    }

    [Fact]
    public async Task A_deleted_key_may_be_written_again()
    {
        await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Tuesday, "taco-tuesday", "Taco Tuesday"));

        await using (var scope = ScopeFor(WorkspaceA))
        {
            await scope.ServiceProvider.GetRequiredService<IWorkspaceWeeklyThemeBusiness>()
                .DeleteAsync(Actor, "taco-tuesday", Ct);
        }

        var week = await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Tuesday, "taco-tuesday", "Taco Tuesday, take two"));

        var theme = Assert.Single(week.Themes);
        Assert.Equal("Taco Tuesday, take two", theme.DisplayName);
        Assert.Equal(1, theme.Revision);
    }

    [Fact]
    public async Task A_workspace_cannot_hold_more_rows_than_the_cap()
    {
        // Retirement keeps rows, so the tail is what the cap counts. Reached here by cycling one day's theme.
        for (var index = 0; index < WeeklyThemePolicy.MaxThemes; index++)
        {
            await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Monday, $"monday-{index}", $"Monday {index}"));
        }

        await using var scope = ScopeFor(WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IWorkspaceWeeklyThemeBusiness>();
        var refused = await business.ReplaceAsync(
            Actor,
            new ReplaceWeeklyThemesViewModel { Themes = [Theme(DayOfWeek.Monday, "one-too-many", "One too many")] },
            Ct);

        Assert.False(refused.Succeeded);
        Assert.Equal(ContentErrorCodes.WeeklyThemesLimit, refused.Error!.Code);

        // The cap is on new rows only: the creator can still rework the themes they already have.
        var kept = await business.ReplaceAsync(
            Actor,
            new ReplaceWeeklyThemesViewModel
            {
                Themes = [Theme(DayOfWeek.Monday, $"monday-{WeeklyThemePolicy.MaxThemes - 1}", "Renamed")],
            },
            Ct);
        Assert.True(kept.Succeeded, kept.Error?.Code);
    }

    [Fact]
    public async Task Business_refuses_a_malformed_week_even_without_the_facades_validator()
    {
        // The backstop for a worker or plugin that reaches Business directly.
        await using var scope = ScopeFor(WorkspaceA);
        var business = scope.ServiceProvider.GetRequiredService<IWorkspaceWeeklyThemeBusiness>();

        var result = await business.ReplaceAsync(
            Actor,
            new ReplaceWeeklyThemesViewModel { Themes = [Theme(DayOfWeek.Monday, "NOT A KEY", "Monday")] },
            Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(ContentErrorCodes.WeeklyThemesInvalid, result.Error!.Code);
        Assert.Contains("themes[0].Key", result.Error.FieldErrors.Keys);
    }

    [Fact]
    public async Task A_replace_is_audited_with_counts_and_never_with_the_creators_words()
    {
        await ReplaceAsync(
            WorkspaceA,
            Theme(DayOfWeek.Monday, "meat-free-monday", "Meat-free Monday"),
            Theme(DayOfWeek.Tuesday, "taco-tuesday", "Taco Tuesday"));

        // Both rows are stamped from the clock, so the two replaces need different instants for the assertion
        // below to be about the entries rather than about which of two equal keys sorted first.
        _clock.Advance(TimeSpan.FromMinutes(5));
        await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Monday, "meat-free-monday", "Meat-free Monday"));

        var entries = await AuditedAsync(WorkspaceA, ContentAuditActions.WeeklyThemesReplaced);

        Assert.Equal(2, entries.Count);
        Assert.Equal(["0", "2"], entries.Select(entry => entry.BeforeReference));
        Assert.Equal(["2", "1"], entries.Select(entry => entry.AfterReference));
        Assert.All(entries, entry => Assert.Equal(WorkspaceA.ToString("D"), entry.ResourceId));
        Assert.All(entries, entry => Assert.DoesNotContain("Monday", entry.Summary, StringComparison.OrdinalIgnoreCase));
        Assert.All(entries, entry => Assert.DoesNotContain("taco", entry.Summary, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_deletion_is_audited_against_the_row_that_went()
    {
        await ReplaceAsync(WorkspaceA, Theme(DayOfWeek.Tuesday, "taco-tuesday", "Taco Tuesday"));

        await using (var scope = ScopeFor(WorkspaceA))
        {
            await scope.ServiceProvider.GetRequiredService<IWorkspaceWeeklyThemeBusiness>()
                .DeleteAsync(Actor, "taco-tuesday", Ct);
        }

        var entry = Assert.Single(await AuditedAsync(WorkspaceA, ContentAuditActions.WeeklyThemeDeleted));
        Assert.Equal(ContentAuditActions.WeeklyThemeResourceType, entry.ResourceType);
        Assert.True(Guid.TryParseExact(entry.ResourceId, "D", out _));
        Assert.Null(entry.AfterReference);
        Assert.DoesNotContain("taco", entry.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<AuditLog>> AuditedAsync(Guid workspaceId, string action, int since = 0)
    {
        await using var scope = ScopeFor(workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.AuditLogs
            .AsNoTracking()
            .Where(entry => entry.Action == action)
            .OrderBy(entry => entry.OccurredAt)
            .ThenBy(entry => entry.Id)
            .Skip(since)
            .ToListAsync(Ct);
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
