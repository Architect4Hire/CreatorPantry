using CreatorPantry.Domain.Audit;
using CreatorPantry.Domain.Data;
using CreatorPantry.Domain.Data.Audit;
using CreatorPantry.Domain.Tenancy;
using CreatorPantry.Tests.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Audit;

/// <summary>
/// Immutability (an ordinary update or delete throws), workspace isolation (the existing query filter, since
/// <see cref="AuditLog"/> is <see cref="IWorkspaceOwned"/>), and the injectable writer stamping WorkspaceId
/// automatically the same way any other workspace-owned insert does.
/// </summary>
public sealed class AuditLogTests : IDisposable
{
    private static readonly Guid CorrelationId = Guid.NewGuid();

    private readonly SqliteAuthServices _services = new(services => services.AddTenancy().AddAudit());

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task The_writer_stages_a_row_that_commits_with_the_callers_own_SaveChanges()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var workspace = await SeedResolvedWorkspaceAsync(scope);
        var writer = scope.ServiceProvider.GetRequiredService<IAuditWriter>();

        writer.Record(new AuditEntry("u1", "workspace.renamed", "Workspace", workspace.ToString(), CorrelationId, "Renamed workspace."));
        // Not saved yet: staged on the same context the caller's own domain write would also be on.
        Assert.Empty(await db.AuditLogs.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var saved = await db.AuditLogs.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(workspace, saved.WorkspaceId); // stamped by WorkspaceOwnershipInterceptor, not the writer
        Assert.Equal("u1", saved.ActorUserId);
        Assert.Equal("workspace.renamed", saved.Action);
        Assert.Equal(CorrelationId, saved.CorrelationId);
    }

    [Fact]
    public async Task A_system_initiated_entry_has_no_actor()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        await SeedResolvedWorkspaceAsync(scope);

        scope.ServiceProvider.GetRequiredService<IAuditWriter>()
            .Record(new AuditEntry(null, "outbox.dispatched", "OutboxMessage", Guid.NewGuid().ToString(), CorrelationId, "Dispatched."));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var saved = await db.AuditLogs.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Null(saved.ActorUserId);
    }

    [Fact]
    public async Task Updating_an_existing_row_throws()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        await SeedResolvedWorkspaceAsync(scope);
        scope.ServiceProvider.GetRequiredService<IAuditWriter>()
            .Record(new AuditEntry("u1", "workspace.renamed", "Workspace", "irrelevant", CorrelationId, "Renamed workspace."));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var row = await db.AuditLogs.SingleAsync(TestContext.Current.CancellationToken);
        row.Summary = "Tampered.";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Contains("modified", exception.Message);
    }

    [Fact]
    public async Task Deleting_an_existing_row_throws()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        await SeedResolvedWorkspaceAsync(scope);
        scope.ServiceProvider.GetRequiredService<IAuditWriter>()
            .Record(new AuditEntry("u1", "workspace.renamed", "Workspace", "irrelevant", CorrelationId, "Renamed workspace."));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var row = await db.AuditLogs.SingleAsync(TestContext.Current.CancellationToken);
        db.AuditLogs.Remove(row);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Contains("deleted", exception.Message);
    }

    [Fact]
    public async Task A_workspaces_audit_rows_are_invisible_from_another_workspaces_scope()
    {
        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid();

        await using (var seedScope = _services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.Workspaces.AddRange(
                new Workspace { Id = workspaceA, Name = "A", Slug = "workspace-a", CreatedAt = SqliteAuthServices.Now },
                new Workspace { Id = workspaceB, Name = "B", Slug = "workspace-b", CreatedAt = SqliteAuthServices.Now });
            // WorkspaceId set explicitly here (not via the writer/interceptor): no workspace is resolved yet
            // in this seeding scope, the same genesis path WorkspaceOwnershipInterceptorTests covers.
            db.AuditLogs.AddRange(
                new AuditLog { Id = Guid.NewGuid(), WorkspaceId = workspaceA, Action = "a.action", ResourceType = "X", ResourceId = "1", CorrelationId = CorrelationId, OccurredAt = SqliteAuthServices.Now, Summary = "A" },
                new AuditLog { Id = Guid.NewGuid(), WorkspaceId = workspaceB, Action = "b.action", ResourceType = "X", ResourceId = "1", CorrelationId = CorrelationId, OccurredAt = SqliteAuthServices.Now, Summary = "B" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>()
            .Resolve(workspaceA, "workspace-a", Guid.NewGuid(), WorkspaceRole.Owner);

        var visible = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AuditLogs.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

        var only = Assert.Single(visible);
        Assert.Equal("a.action", only.Action);
    }

    [Fact]
    public async Task Querying_audit_logs_before_a_workspace_is_resolved_throws()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.AuditLogs.ToListAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<Guid> SeedResolvedWorkspaceAsync(AsyncServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Sam's Kitchen", Slug = "sams-kitchen", CreatedAt = SqliteAuthServices.Now };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>()
            .Resolve(workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner);
        return workspace.Id;
    }
}
