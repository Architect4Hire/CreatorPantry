using System.Reflection;
using CreatorPantry.Domain.Data;
using CreatorPantry.Domain.Tenancy;
using CreatorPantry.Tests.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Tenancy;

/// <summary>
/// Exercises the EF configuration for <see cref="Workspace"/> and <see cref="WorkspaceMembership"/> against
/// an in-memory SQLite database, the same pattern <c>UserRepositoryTests</c> uses for Identity. This
/// verifies the constraints the migration also creates, without applying that migration anywhere.
/// </summary>
public sealed class WorkspaceConstraintTests : IDisposable
{
    private readonly SqliteAuthServices _services = new();

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task Workspace_slug_must_be_unique()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.Workspaces.Add(NewWorkspace("Sam's Kitchen", "sams-kitchen"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Workspaces.Add(NewWorkspace("Someone Else's Kitchen", "sams-kitchen"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_user_cannot_have_two_memberships_in_the_same_workspace()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var userId = await _services.CreateConfirmedUserAsync("cook@example.com", "correct horse battery");
        var workspace = NewWorkspace("Sam's Kitchen", "sams-kitchen");
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.WorkspaceMemberships.Add(NewMembership(workspace.Id, userId, WorkspaceRole.Owner));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A role change updates the existing row; it never adds a second membership for the same pair.
        db.WorkspaceMemberships.Add(NewMembership(workspace.Id, userId, WorkspaceRole.Viewer));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_user_can_belong_to_two_workspaces_with_different_roles()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var userId = await _services.CreateConfirmedUserAsync("cook@example.com", "correct horse battery");

        var workspaceA = NewWorkspace("Workspace A", "workspace-a");
        var workspaceB = NewWorkspace("Workspace B", "workspace-b");
        db.Workspaces.AddRange(workspaceA, workspaceB);
        db.WorkspaceMemberships.AddRange(
            NewMembership(workspaceA.Id, userId, WorkspaceRole.Owner),
            NewMembership(workspaceB.Id, userId, WorkspaceRole.Viewer));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var memberships = await db.WorkspaceMemberships
            .Where(membership => membership.UserId == userId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, memberships.Count);
        Assert.Equal(WorkspaceRole.Owner, memberships.Single(m => m.WorkspaceId == workspaceA.Id).Role);
        Assert.Equal(WorkspaceRole.Viewer, memberships.Single(m => m.WorkspaceId == workspaceB.Id).Role);
    }

    [Fact]
    public async Task Deleting_a_workspace_cascades_only_to_its_own_memberships()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var userId = await _services.CreateConfirmedUserAsync("cook@example.com", "correct horse battery");

        var workspaceA = NewWorkspace("Workspace A", "workspace-a");
        var workspaceB = NewWorkspace("Workspace B", "workspace-b");
        db.Workspaces.AddRange(workspaceA, workspaceB);
        db.WorkspaceMemberships.AddRange(
            NewMembership(workspaceA.Id, userId, WorkspaceRole.Owner),
            NewMembership(workspaceB.Id, userId, WorkspaceRole.Owner));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Workspaces.Remove(workspaceA);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var remaining = await db.WorkspaceMemberships.ToListAsync(TestContext.Current.CancellationToken);
        var remainingMembership = Assert.Single(remaining);
        Assert.Equal(workspaceB.Id, remainingMembership.WorkspaceId);
    }

    [Fact]
    public async Task Deleting_a_user_cascades_to_their_memberships()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var userId = await _services.CreateConfirmedUserAsync("cook@example.com", "correct horse battery");
        var workspace = NewWorkspace("Sam's Kitchen", "sams-kitchen");
        db.Workspaces.Add(workspace);
        db.WorkspaceMemberships.Add(NewMembership(workspace.Id, userId, WorkspaceRole.Owner));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var user = await db.Users.SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        db.Users.Remove(user);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await db.WorkspaceMemberships.ToListAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(await db.Workspaces.FindAsync([workspace.Id], TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(typeof(WorkspaceRole))]
    [InlineData(typeof(WorkspaceMembershipStatus))]
    public void Workspace_enums_are_ordered_not_flags(Type enumType)
    {
        Assert.Null(enumType.GetCustomAttribute<FlagsAttribute>());

        var values = Enum.GetValues(enumType).Cast<int>().ToList();
        Assert.Equal(values.OrderBy(value => value), values);
        Assert.Equal(values.Distinct(), values);
    }

    [Fact]
    public void WorkspaceRole_orders_viewer_below_owner()
    {
        Assert.True(WorkspaceRole.Viewer < WorkspaceRole.Contributor);
        Assert.True(WorkspaceRole.Contributor < WorkspaceRole.Editor);
        Assert.True(WorkspaceRole.Editor < WorkspaceRole.Owner);
    }

    private static Workspace NewWorkspace(string name, string slug) =>
        new() { Id = Guid.NewGuid(), Name = name, Slug = slug, CreatedAt = SqliteAuthServices.Now };

    private static WorkspaceMembership NewMembership(Guid workspaceId, string userId, WorkspaceRole role) =>
        new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = role,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = SqliteAuthServices.Now,
        };
}
