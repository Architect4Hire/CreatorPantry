using CreatorPantry.Domain.Data;
using CreatorPantry.Domain.Tenancy;
using CreatorPantry.Tests.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Tenancy;

/// <summary>
/// <see cref="WorkspaceMembership"/> is the only <see cref="IWorkspaceOwned"/> entity in the production model
/// today, and it is the documented exception to the convention (it must be readable before a workspace is
/// resolved, since resolving one means querying it). Exercised directly against the <c>DbSet</c> — no
/// repository, no explicit <c>Where</c> — so the filter itself is what is under test. Strict (non-exception)
/// behavior for a typical workspace-owned entity is covered separately in
/// <see cref="WorkspaceOwnershipConventionAppliesToNewEntitiesTests"/>, since no second production entity
/// exists yet to exercise it against.
/// </summary>
public sealed class WorkspaceOwnershipConventionTests : IDisposable
{
    private readonly SqliteAuthServices _services = new(services => services.AddTenancy());

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task Workspace_membership_is_the_one_entity_readable_before_resolution()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var userId = await _services.CreateConfirmedUserAsync("cook@example.com", "correct horse battery");

        var workspaceA = new Workspace { Id = Guid.NewGuid(), Name = "A", Slug = "workspace-a", CreatedAt = SqliteAuthServices.Now };
        var workspaceB = new Workspace { Id = Guid.NewGuid(), Name = "B", Slug = "workspace-b", CreatedAt = SqliteAuthServices.Now };
        db.Workspaces.AddRange(workspaceA, workspaceB);
        db.WorkspaceMemberships.AddRange(
            NewMembership(workspaceA.Id, userId),
            NewMembership(workspaceB.Id, userId));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Resolution hasn't happened yet in this scope. A plain, unqualified query against the DbSet — no
        // repository, no explicit Where — must still see both rows, or WorkspaceRepository's own
        // pre-resolution lookup (which relies on exactly this) would break.
        var beforeResolution = await db.WorkspaceMemberships.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, beforeResolution.Count);

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>()
            .Resolve(workspaceA.Id, "workspace-a", Guid.NewGuid(), WorkspaceRole.Owner);

        // Once a workspace is resolved, WorkspaceMembership is scoped exactly like every other
        // workspace-owned entity — the exception applies only pre-resolution.
        var afterResolution = await db.WorkspaceMemberships.ToListAsync(TestContext.Current.CancellationToken);
        var only = Assert.Single(afterResolution);
        Assert.Equal(workspaceA.Id, only.WorkspaceId);
    }

    private static WorkspaceMembership NewMembership(Guid workspaceId, string userId) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        UserId = userId,
        Role = WorkspaceRole.Owner,
        Status = WorkspaceMembershipStatus.Active,
        JoinedAt = SqliteAuthServices.Now,
    };
}
