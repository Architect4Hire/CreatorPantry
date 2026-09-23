using CreatorPantry.Domain.Modules.Auth;
using CreatorPantry.Domain.Modules.Auth.Managers;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.MigrationService;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CreatorPantry.Tests.Auth;

/// <summary>
/// Runs the real migration-service host (worker, seeders, Identity role store) against one SQLite database,
/// the way repeated deployments run it against SQL Server.
/// </summary>
public sealed class PlatformRoleSeedingTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();

        // Schema via EnsureCreated: the initial migration arrives in prompt 1.5.
        await using var context = new CreatorPantryDbContext(
            new DbContextOptionsBuilder<CreatorPantryDbContext>().UseSqlite(_connection).Options);
        await context.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Repeated_startup_seeds_platform_admin_exactly_once()
    {
        Assert.Equal(MigrationOutcome.Succeeded, await RunMigrationHostAsync());
        var firstId = Assert.Single(await ReadRolesAsync()).Id;

        Assert.Equal(MigrationOutcome.Succeeded, await RunMigrationHostAsync());
        Assert.Equal(MigrationOutcome.Succeeded, await RunMigrationHostAsync());

        var role = Assert.Single(await ReadRolesAsync());
        Assert.Equal(PlatformRoles.PlatformAdmin, role.Name);
        Assert.Equal("PLATFORMADMIN", role.NormalizedName);
        Assert.Equal(firstId, role.Id); // the original row is kept, not replaced
    }

    [Fact]
    public async Task Workspace_membership_roles_are_never_seeded_into_identity()
    {
        await RunMigrationHostAsync();

        var names = (await ReadRolesAsync()).Select(role => role.Name).ToList();

        Assert.Equal([PlatformRoles.PlatformAdmin], names);
        Assert.All(["Viewer", "Contributor", "Editor", "Owner"], workspaceRole => Assert.DoesNotContain(workspaceRole, names));
    }

    [Fact]
    public async Task Existing_role_row_is_left_untouched()
    {
        await using (var context = CreateContext())
        {
            context.Roles.Add(new IdentityRole(PlatformRoles.PlatformAdmin)
            {
                Id = "pre-existing", NormalizedName = "PLATFORMADMIN", ConcurrencyStamp = "stamp",
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await RunMigrationHostAsync();

        var role = Assert.Single(await ReadRolesAsync());
        Assert.Equal(("pre-existing", "stamp"), (role.Id, role.ConcurrencyStamp));
    }

    [Fact]
    public void Schema_registration_adds_one_migration_target_and_both_seeders()
    {
        var services = new ServiceCollection().AddCreatorPantrySchema();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IMigrationTarget));

        // The role seeder and the reference-catalogue seeder. MigrationWorker resolves every IDataSeeder, so a
        // seeder that is registered but never run would show up here as a count mismatch.
        Assert.Equal(2, services.Count(descriptor => descriptor.ServiceType == typeof(IDataSeeder)));
    }

    private async Task<MigrationOutcome> RunMigrationHostAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDbContext<CreatorPantryDbContext>(options => options.UseSqlite(_connection));
        builder.Services.AddPlatformRoleSeeding();
        builder.Services.AddMigrationHost();

        using var host = builder.Build();
        var state = host.Services.GetRequiredService<MigrationRunState>();
        await host.RunAsync(TestContext.Current.CancellationToken);

        return state.Outcome;
    }

    private async Task<List<IdentityRole>> ReadRolesAsync()
    {
        await using var context = CreateContext();
        return await context.Roles.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private CreatorPantryDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CreatorPantryDbContext>().UseSqlite(_connection).Options);
}
