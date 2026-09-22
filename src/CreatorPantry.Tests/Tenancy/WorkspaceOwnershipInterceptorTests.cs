using CreatorPantry.Domain.Data;
using CreatorPantry.Domain.Tenancy;
using CreatorPantry.Tests.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Tenancy;

/// <summary>
/// Exercises <see cref="WorkspaceOwnershipInterceptor"/> against <see cref="WorkspaceMembership"/> — the
/// only production <see cref="IWorkspaceOwned"/> entity today — the same way
/// <see cref="WorkspaceOwnershipConventionTests"/> exercises the read-side filter against it.
/// </summary>
public sealed class WorkspaceOwnershipInterceptorTests : IDisposable
{
    private readonly SqliteAuthServices _services = new(services => services.AddTenancy());

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task Stamps_WorkspaceId_on_an_added_entity_from_the_resolved_context()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var userId = await _services.CreateConfirmedUserAsync("cook@example.com", "correct horse battery");
        var workspace = NewWorkspace();
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>()
            .Resolve(workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner);

        // Feature code never assigns WorkspaceId (tenancy.md): left unset here, exactly as Business/DataLayer
        // code would construct it.
        var membership = new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Role = WorkspaceRole.Contributor,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = SqliteAuthServices.Now,
        };
        db.WorkspaceMemberships.Add(membership);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(workspace.Id, membership.WorkspaceId);
    }

    [Fact]
    public async Task Rejects_an_insert_already_carrying_another_workspaces_id()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var userId = await _services.CreateConfirmedUserAsync("cook@example.com", "correct horse battery");
        var workspaceA = NewWorkspace();
        var workspaceB = NewWorkspace();
        db.Workspaces.AddRange(workspaceA, workspaceB);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>()
            .Resolve(workspaceA.Id, workspaceA.Slug, Guid.NewGuid(), WorkspaceRole.Owner);

        // Scope is resolved to workspace A, but the entity arrives already stamped for workspace B.
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceB.Id,
            UserId = userId,
            Role = WorkspaceRole.Contributor,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = SqliteAuthServices.Now,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_an_update_that_changes_WorkspaceId()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var userId = await _services.CreateConfirmedUserAsync("cook@example.com", "correct horse battery");
        var workspaceA = NewWorkspace();
        var workspaceB = NewWorkspace();
        db.Workspaces.AddRange(workspaceA, workspaceB);
        var membership = new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceA.Id,
            UserId = userId,
            Role = WorkspaceRole.Contributor,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = SqliteAuthServices.Now,
        };
        db.WorkspaceMemberships.Add(membership);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // No workspace resolved for this attempt — WorkspaceMembership is readable pre-resolution, and
        // ownership must stay immutable regardless of whether a workspace scope is active.
        membership.WorkspaceId = workspaceB.Id;

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unresolved_scope_leaves_an_explicit_WorkspaceId_on_insert_untouched()
    {
        // The one path that runs before any workspace is resolved: creating the very first Workspace and
        // its owner WorkspaceMembership. There is no ambient workspace to stamp with or compare against.
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var userId = await _services.CreateConfirmedUserAsync("cook@example.com", "correct horse battery");
        var workspace = NewWorkspace();
        db.Workspaces.Add(workspace);
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = userId,
            Role = WorkspaceRole.Owner,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = SqliteAuthServices.Now,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var saved = await db.WorkspaceMemberships.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(workspace.Id, saved.WorkspaceId);
    }

    private static Workspace NewWorkspace() =>
        new() { Id = Guid.NewGuid(), Name = "Kitchen", Slug = $"kitchen-{Guid.NewGuid():n}", CreatedAt = SqliteAuthServices.Now };
}
