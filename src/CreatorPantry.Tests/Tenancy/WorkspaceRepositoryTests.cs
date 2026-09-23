using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data;
using CreatorPantry.Domain.Modules.Auth.Data;
using CreatorPantry.Domain.Modules.Measurement.Data;
using CreatorPantry.Domain.Modules.Vocabulary.Data;
using CreatorPantry.Domain.Modules.Ingredients.Data;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Tests.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Tenancy;

/// <summary>Exercises the actual EF query against SQLite, complementing the fake-backed business tests.</summary>
public sealed class WorkspaceRepositoryTests : IDisposable
{
    private readonly SqliteAuthServices _services = new(services => services.AddTenancy());

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task An_unknown_slug_returns_neither_workspace_nor_membership()
    {
        var lookup = await FindAsync("no-such-workspace", "irrelevant-user");

        Assert.Null(lookup.Workspace);
        Assert.Null(lookup.Membership);
    }

    [Fact]
    public async Task A_known_workspace_with_no_membership_for_this_user_returns_the_workspace_only()
    {
        var userId = await _services.CreateConfirmedUserAsync("cook@example.com", "correct horse battery");
        var workspaceId = await SeedWorkspaceAsync("sams-kitchen");
        await SeedMembershipAsync(workspaceId, userId, WorkspaceRole.Owner, WorkspaceMembershipStatus.Active);

        var lookup = await FindAsync("sams-kitchen", "a-different-user-id");

        Assert.NotNull(lookup.Workspace);
        Assert.Equal(workspaceId, lookup.Workspace!.Id);
        Assert.Null(lookup.Membership);
    }

    [Fact]
    public async Task A_member_returns_both_the_workspace_and_their_own_membership()
    {
        var userId = await _services.CreateConfirmedUserAsync("cook@example.com", "correct horse battery");
        var workspaceId = await SeedWorkspaceAsync("sams-kitchen");
        var membershipId = await SeedMembershipAsync(workspaceId, userId, WorkspaceRole.Contributor, WorkspaceMembershipStatus.Invited);

        var lookup = await FindAsync("sams-kitchen", userId);

        Assert.Equal(workspaceId, lookup.Workspace!.Id);
        Assert.Equal("sams-kitchen", lookup.Workspace.Slug);
        Assert.Equal(membershipId, lookup.Membership!.Id);
        Assert.Equal(WorkspaceRole.Contributor, lookup.Membership.Role);

        // The repository reports raw status; collapsing Invited into "not found" is Business's job.
        Assert.Equal(WorkspaceMembershipStatus.Invited, lookup.Membership.Status);
    }

    [Fact]
    public async Task A_users_membership_in_one_workspace_does_not_leak_into_another_workspaces_lookup()
    {
        var userId = await _services.CreateConfirmedUserAsync("cook@example.com", "correct horse battery");
        var workspaceAId = await SeedWorkspaceAsync("workspace-a");
        var workspaceBId = await SeedWorkspaceAsync("workspace-b");
        await SeedMembershipAsync(workspaceAId, userId, WorkspaceRole.Owner, WorkspaceMembershipStatus.Active);

        var lookup = await FindAsync("workspace-b", userId);

        Assert.Equal(workspaceBId, lookup.Workspace!.Id);
        Assert.Null(lookup.Membership);
    }

    [Fact]
    public async Task SlugExists_reports_true_only_for_a_taken_slug()
    {
        await SeedWorkspaceAsync("sams-kitchen");

        await using var scope = _services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();

        Assert.True(await repository.SlugExistsAsync("sams-kitchen", TestContext.Current.CancellationToken));
        Assert.False(await repository.SlugExistsAsync("no-such-slug", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateWithOwner_atomically_inserts_the_workspace_and_exactly_one_owner_membership()
    {
        var userId = await _services.CreateConfirmedUserAsync("cook@example.com", "correct horse battery");

        await using var scope = _services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var created = await repository.CreateWithOwnerAsync(
            "Sam's Kitchen", "sams-kitchen", userId, SqliteAuthServices.Now, TestContext.Current.CancellationToken);

        Assert.Equal("Sam's Kitchen", created.Workspace.Name);
        Assert.Equal("sams-kitchen", created.Workspace.Slug);
        Assert.Equal(WorkspaceRole.Owner, created.Role);

        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        Assert.NotNull(await db.Workspaces.FindAsync([created.Workspace.Id], TestContext.Current.CancellationToken));
        var membership = await db.WorkspaceMemberships.SingleAsync(
            m => m.WorkspaceId == created.Workspace.Id, TestContext.Current.CancellationToken);
        Assert.Equal(created.MembershipId, membership.Id);
        Assert.Equal(userId, membership.UserId);
        Assert.Equal(WorkspaceRole.Owner, membership.Role);
        Assert.Equal(WorkspaceMembershipStatus.Active, membership.Status);
    }

    [Fact]
    public async Task FindById_returns_null_for_an_unknown_workspace()
    {
        await using var scope = _services.CreateScope();
        var found = await scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>()
            .FindByIdAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Null(found);
    }

    [Fact]
    public async Task Rename_updates_the_name_and_leaves_the_slug_unchanged()
    {
        var workspaceId = await SeedWorkspaceAsync("sams-kitchen");

        await using var scope = _services.CreateScope();
        var renamed = await scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>()
            .RenameAsync(workspaceId, "New Name", TestContext.Current.CancellationToken);

        Assert.Equal("New Name", renamed.Name);
        Assert.Equal("sams-kitchen", renamed.Slug);
    }

    [Fact]
    public async Task FindMembershipsForUser_returns_every_workspace_the_user_belongs_to_and_no_others()
    {
        var userId = await _services.CreateConfirmedUserAsync("cook@example.com", "correct horse battery");
        var otherUserId = await _services.CreateConfirmedUserAsync("other@example.com", "correct horse battery");
        var workspaceAId = await SeedWorkspaceAsync("workspace-a");
        var workspaceBId = await SeedWorkspaceAsync("workspace-b");
        await SeedMembershipAsync(workspaceAId, userId, WorkspaceRole.Owner, WorkspaceMembershipStatus.Active);
        await SeedMembershipAsync(workspaceBId, otherUserId, WorkspaceRole.Owner, WorkspaceMembershipStatus.Active);

        await using var scope = _services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>()
            .FindMembershipsForUserAsync(userId, TestContext.Current.CancellationToken);

        var only = Assert.Single(rows);
        Assert.Equal(workspaceAId, only.Workspace.Id);
        Assert.Equal("workspace-a", only.Workspace.Slug);
        Assert.Equal(WorkspaceRole.Owner, only.Role);
    }

    private async Task<WorkspaceMembershipLookup> FindAsync(string slug, string userId)
    {
        await using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>()
            .FindBySlugAsync(slug, userId, TestContext.Current.CancellationToken);
    }

    private async Task<Guid> SeedWorkspaceAsync(string slug)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = slug, Slug = slug, CreatedAt = SqliteAuthServices.Now };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return workspace.Id;
    }

    private async Task<Guid> SeedMembershipAsync(Guid workspaceId, string userId, WorkspaceRole role, WorkspaceMembershipStatus status)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var membership = new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = role,
            Status = status,
            JoinedAt = SqliteAuthServices.Now,
        };
        db.WorkspaceMemberships.Add(membership);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return membership.Id;
    }
}
