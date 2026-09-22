using CreatorPantry.Domain.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Data;

/// <summary>The single DbContext for the modular monolith.</summary>
/// <param name="workspaceContext">
/// Optional: unavailable outside a request/operation scope (migrations, unrelated hosts). Every
/// <see cref="Tenancy.IWorkspaceOwned"/> entity is filtered by it via <see cref="WorkspaceOwnershipConvention"/>.
/// </param>
public class CreatorPantryDbContext(DbContextOptions<CreatorPantryDbContext> options, IWorkspaceContext? workspaceContext = null)
    : IdentityDbContext<ApplicationUser, IdentityRole, string>(options), IWorkspaceIdSource
{
    /// <summary>The Aspire connection name; matches the database resource in the AppHost.</summary>
    public const string ConnectionName = "creatorpantrydb";

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    /// <remarks>
    /// Unfiltered before a workspace is resolved (see <see cref="WorkspaceMembership"/>'s remarks) — a
    /// query here before resolution must supply its own explicit <c>WorkspaceId</c>/<c>UserId</c>
    /// predicate, the way <see cref="Repositories.WorkspaceRepository"/> does, or it reads every workspace.
    /// </remarks>
    public DbSet<WorkspaceMembership> WorkspaceMemberships => Set<WorkspaceMembership>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    Guid? IWorkspaceIdSource.CurrentWorkspaceIdOrNull => workspaceContext is { IsResolved: true } context ? context.WorkspaceId : null;

    /// <remarks>
    /// Added here rather than at each <c>AddDbContext</c> call site (API host, migration service, tests) so
    /// every instance enforces <see cref="WorkspaceOwnershipInterceptor"/> and
    /// <see cref="AuditLogImmutabilityInterceptor"/> on save with nothing to forget. Additive over whatever
    /// provider/options DI already configured.
    /// </remarks>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        optionsBuilder.AddInterceptors(new WorkspaceOwnershipInterceptor(), new AuditLogImmutabilityInterceptor());
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(CreatorPantryDbContext).Assembly);

        WorkspaceOwnershipConvention.Apply(builder, this);
    }
}
