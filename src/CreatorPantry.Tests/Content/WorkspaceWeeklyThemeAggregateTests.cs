using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The weekly theme's schema and isolation, over the same two-workspace SQLite fixture the recipe and brand
/// aggregates use. Proves the EF configuration — the live-day filtered unique index, the per-workspace key
/// index, the check constraints, the query filter — not that SQL Server accepts the DDL, which the migration's
/// own verification covers.
/// </summary>
public sealed class WorkspaceWeeklyThemeAggregateTests : IDisposable
{
    private static readonly DateTimeOffset Now = RecipeAggregateFixture.Now;

    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WorkspaceWeeklyTheme NewTheme(
        Guid workspaceId,
        DayOfWeek day = DayOfWeek.Monday,
        string key = "meat-free-monday",
        string displayName = "Meat-free Monday",
        DateTimeOffset? retiredAt = null) => new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Day = day,
            Key = key,
            DisplayName = displayName,
            RetiredAt = retiredAt,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

    private async Task<WorkspaceWeeklyTheme> SeedAsync(Guid workspaceId, WorkspaceWeeklyTheme theme)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);
        db.WorkspaceWeeklyThemes.Add(theme);
        await db.SaveChangesAsync(Ct);

        return theme;
    }

    [Fact]
    public async Task Each_workspace_writes_its_own_week_and_cannot_see_the_others()
    {
        // The same day and the same key in both workspaces: a theme is creator IP, never deduplicated.
        var a = await SeedAsync(RecipeAggregateFixture.WorkspaceA, NewTheme(RecipeAggregateFixture.WorkspaceA));
        var b = await SeedAsync(RecipeAggregateFixture.WorkspaceB, NewTheme(RecipeAggregateFixture.WorkspaceB,
            displayName: "Meatless Monday"));

        await using var scopeA = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var dbA = RecipeAggregateFixture.Db(scopeA);

        var visible = await dbA.WorkspaceWeeklyThemes.ToListAsync(Ct);
        Assert.Equal([a.Id], visible.Select(theme => theme.Id));
        Assert.Equal("Meat-free Monday", visible[0].DisplayName);
        Assert.Null(await dbA.WorkspaceWeeklyThemes.FirstOrDefaultAsync(theme => theme.Id == b.Id, Ct));

        await using var scopeB = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceB);
        var dbB = RecipeAggregateFixture.Db(scopeB);
        Assert.Equal("Meatless Monday", (await dbB.WorkspaceWeeklyThemes.SingleAsync(Ct)).DisplayName);
    }

    [Fact]
    public async Task A_day_holds_one_live_theme()
    {
        await SeedAsync(RecipeAggregateFixture.WorkspaceA, NewTheme(RecipeAggregateFixture.WorkspaceA));

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.WorkspaceWeeklyThemes.Add(NewTheme(RecipeAggregateFixture.WorkspaceA, key: "pasta-monday", displayName: "Pasta Monday"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_retired_theme_leaves_its_day_free()
    {
        // The reason the day index is filtered: without that, retiring Monday's theme would make Monday
        // unusable forever.
        await SeedAsync(RecipeAggregateFixture.WorkspaceA, NewTheme(RecipeAggregateFixture.WorkspaceA, retiredAt: Now));

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.WorkspaceWeeklyThemes.Add(NewTheme(RecipeAggregateFixture.WorkspaceA, key: "pasta-monday", displayName: "Pasta Monday"));
        await db.SaveChangesAsync(Ct);

        Assert.Equal(2, await db.WorkspaceWeeklyThemes.CountAsync(Ct));
        Assert.Equal(1, await db.WorkspaceWeeklyThemes.CountAsync(theme => theme.RetiredAt == null, Ct));
    }

    [Fact]
    public async Task Many_retired_themes_may_share_a_day()
    {
        await SeedAsync(RecipeAggregateFixture.WorkspaceA, NewTheme(RecipeAggregateFixture.WorkspaceA, retiredAt: Now));

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.WorkspaceWeeklyThemes.Add(
            NewTheme(RecipeAggregateFixture.WorkspaceA, key: "pasta-monday", retiredAt: Now, displayName: "Pasta Monday"));
        await db.SaveChangesAsync(Ct);

        Assert.Equal(2, await db.WorkspaceWeeklyThemes.CountAsync(theme => theme.Day == DayOfWeek.Monday, Ct));
    }

    [Fact]
    public async Task A_key_is_never_reused_in_a_workspace_even_once_retired()
    {
        // The unfiltered key index. It is what makes a stored key resolve to exactly one theme or to nothing,
        // which is the contract every consumer of a theme key relies on.
        await SeedAsync(RecipeAggregateFixture.WorkspaceA, NewTheme(RecipeAggregateFixture.WorkspaceA, retiredAt: Now));

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.WorkspaceWeeklyThemes.Add(NewTheme(RecipeAggregateFixture.WorkspaceA, day: DayOfWeek.Tuesday));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Theory]
    [InlineData("", "Meat-free Monday")]
    [InlineData("   ", "Meat-free Monday")]
    [InlineData("meat-free-monday", "")]
    [InlineData("meat-free-monday", "   ")]
    public async Task A_blank_key_or_name_is_refused(string key, string displayName)
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.WorkspaceWeeklyThemes.Add(
            NewTheme(RecipeAggregateFixture.WorkspaceA, key: key, displayName: displayName));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_day_outside_the_week_is_refused()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.WorkspaceWeeklyThemes.Add(NewTheme(RecipeAggregateFixture.WorkspaceA, day: (DayOfWeek)9));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_revision_below_one_is_refused()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var theme = NewTheme(RecipeAggregateFixture.WorkspaceA);
        theme.Revision = 0;
        db.WorkspaceWeeklyThemes.Add(theme);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Deleting_a_theme_takes_nothing_else_with_it()
    {
        // Nothing holds a foreign key to a theme, which is what lets a consumer keep a key for a theme that is
        // gone. Proved from the other direction: the row leaves and the rest of the week is untouched.
        var monday = await SeedAsync(RecipeAggregateFixture.WorkspaceA, NewTheme(RecipeAggregateFixture.WorkspaceA));
        await SeedAsync(RecipeAggregateFixture.WorkspaceA, NewTheme(
            RecipeAggregateFixture.WorkspaceA, DayOfWeek.Friday, "fakeaway-friday", "Fakeaway Friday"));

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.WorkspaceWeeklyThemes.Remove(await db.WorkspaceWeeklyThemes.SingleAsync(theme => theme.Id == monday.Id, Ct));
        await db.SaveChangesAsync(Ct);

        var remaining = await db.WorkspaceWeeklyThemes.SingleAsync(Ct);
        Assert.Equal("fakeaway-friday", remaining.Key);

        // And the key is free again: a creator may write a new theme under it, which then resolves to that one.
        db.WorkspaceWeeklyThemes.Add(NewTheme(RecipeAggregateFixture.WorkspaceA, displayName: "Meat-free Monday, again"));
        await db.SaveChangesAsync(Ct);
        Assert.Equal(
            "Meat-free Monday, again",
            (await db.WorkspaceWeeklyThemes.SingleAsync(theme => theme.Key == "meat-free-monday", Ct)).DisplayName);
    }

    [Fact]
    public async Task Themes_cannot_be_queried_before_a_workspace_is_resolved()
    {
        await SeedAsync(RecipeAggregateFixture.WorkspaceA, NewTheme(RecipeAggregateFixture.WorkspaceA));

        await using var scope = _fixture.UnresolvedScope();
        var db = RecipeAggregateFixture.Db(scope);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.WorkspaceWeeklyThemes.ToListAsync(Ct));
    }
}
