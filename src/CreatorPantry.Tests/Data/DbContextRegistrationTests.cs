extern alias ApiService;

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
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Seeding;
using CreatorPantry.Domain.Modules.Vocabulary.Seeding;
using CreatorPantry.Domain.Modules.Ingredients.Seeding;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.MigrationService;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CreatorPantry.Tests.Data;

public class DbContextRegistrationTests
{
    [Fact]
    public void ApiService_resolves_the_dbcontext_for_sql_server()
    {
        using var factory = new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(TestDatabase.Configure);
        using var scope = factory.Services.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        Assert.True(context.Database.IsSqlServer());
    }

    [Fact]
    public void MigrationService_resolves_the_dbcontext_with_one_migration_target_and_both_seeders()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration[$"ConnectionStrings:{CreatorPantryDbContext.ConnectionName}"] = TestDatabase.ConnectionString;
        builder.AddMigrationDatabase();

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        Assert.True(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.IsSqlServer());
        Assert.Equal(nameof(CreatorPantryDbContext), Assert.Single(scope.ServiceProvider.GetServices<IMigrationTarget>()).Name);

        // The PlatformAdmin role seeder and the reference-catalogue seeder, in that registration order.
        Assert.Equal(
            ["Platform roles", "Reference catalogue"],
            scope.ServiceProvider.GetServices<Domain.Managers.Persistence.IDataSeeder>().Select(seeder => seeder.Name));

        // Host.CreateApplicationBuilder defaults to Production, so this host is the production tier.
        Assert.True(host.Services.GetRequiredService<IHostEnvironment>().IsProduction());
    }

    /// <summary>
    /// The tier gate, asserted on the option the seeder actually reads rather than on the ambient environment.
    /// </summary>
    /// <remarks>
    /// This replaces an assertion that checked only <c>IsProduction()</c> and would still have passed if the
    /// gate in <c>AddMigrationDatabase</c> were changed to pass <c>true</c> unconditionally — which would seed
    /// ingredients, densities, and allergen traits citing a source whose citation reads "not suitable for
    /// production use" into a live catalogue.
    /// </remarks>
    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", true)]
    [InlineData("Staging", true)]
    public void Development_sample_data_is_seeded_outside_production_only(string environment, bool expected)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        builder.Configuration[$"ConnectionStrings:{CreatorPantryDbContext.ConnectionName}"] = TestDatabase.ConnectionString;
        builder.AddMigrationDatabase();

        using var host = builder.Build();

        Assert.Equal(
            expected,
            host.Services.GetRequiredService<ReferenceSeedOptions>().IncludeDevelopmentSampleData);
    }

    /// <summary>
    /// Every test in this suite builds its schema with <c>EnsureCreated</c>, which reads the model and never
    /// touches a migration file. This is the one assertion that notices when the two diverge — a model change
    /// committed without a migration, or a hand-edited migration body, would otherwise ship green and fail at
    /// deployment rather than in CI.
    /// </summary>
    [Fact]
    public void The_model_has_no_changes_that_no_migration_captures()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration[$"ConnectionStrings:{CreatorPantryDbContext.ConnectionName}"] = TestDatabase.ConnectionString;
        builder.AddMigrationDatabase();

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        // Compares the model against the migrations' own snapshot; it opens no connection.
        Assert.False(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.HasPendingModelChanges());
    }

    [Fact]
    public void Model_maps_application_user_with_identity_tables()
    {
        using var factory = new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(TestDatabase.Configure);
        using var scope = factory.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Model;

        var user = model.FindEntityType(typeof(ApplicationUser))!;
        Assert.Equal("AspNetUsers", user.GetTableName());

        var displayName = user.FindProperty(nameof(ApplicationUser.DisplayName))!;
        Assert.False(displayName.IsNullable);
        Assert.Equal(AccountPolicy.DisplayNameMaxLength, displayName.GetMaxLength());

        Assert.False(user.FindProperty(nameof(ApplicationUser.CreatedAt))!.IsNullable);

        var lastWorkspace = user.FindProperty(nameof(ApplicationUser.LastWorkspaceId))!;
        Assert.True(lastWorkspace.IsNullable);
        Assert.Equal(typeof(Guid?), lastWorkspace.ClrType);

        // A navigation hint only: a deleted workspace clears it (SetNull) rather than blocking the
        // delete or cascading to the user.
        var lastWorkspaceForeignKey = Assert.Single(user.GetForeignKeys(), fk => fk.Properties.Contains(lastWorkspace));
        Assert.Equal(nameof(Workspace), lastWorkspaceForeignKey.PrincipalEntityType.ClrType.Name);
        Assert.Equal(DeleteBehavior.SetNull, lastWorkspaceForeignKey.DeleteBehavior);

        string[] identityTables = ["AspNetRoles", "AspNetUserRoles", "AspNetUserClaims", "AspNetUserLogins", "AspNetUserTokens", "AspNetRoleClaims"];
        Assert.All(identityTables, table => Assert.Contains(model.GetEntityTypes(), e => e.GetTableName() == table));
    }

    [Fact]
    public void Model_maps_workspace_and_membership_with_required_indexes()
    {
        using var factory = new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(TestDatabase.Configure);
        using var scope = factory.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Model;

        var workspace = model.FindEntityType(typeof(Workspace))!;
        Assert.Equal("Workspaces", workspace.GetTableName());
        Assert.Contains(workspace.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(p => p.Name).SequenceEqual([nameof(Workspace.Slug)]));

        var membership = model.FindEntityType(typeof(WorkspaceMembership))!;
        Assert.Equal("WorkspaceMemberships", membership.GetTableName());

        // One membership per user per workspace.
        Assert.Contains(membership.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(p => p.Name).SequenceEqual([nameof(WorkspaceMembership.WorkspaceId), nameof(WorkspaceMembership.UserId)]));

        // Reverse lookup: every workspace a user belongs to.
        Assert.Contains(membership.GetIndexes(), index => !index.IsUnique
            && index.Properties.Select(p => p.Name).SequenceEqual([nameof(WorkspaceMembership.UserId)]));

        var workspaceForeignKey = Assert.Single(membership.GetForeignKeys(),
            fk => fk.PrincipalEntityType.ClrType == typeof(Workspace));
        Assert.Equal(DeleteBehavior.Cascade, workspaceForeignKey.DeleteBehavior);

        var userForeignKey = Assert.Single(membership.GetForeignKeys(),
            fk => fk.PrincipalEntityType.ClrType == typeof(ApplicationUser));
        Assert.Equal(DeleteBehavior.Cascade, userForeignKey.DeleteBehavior);
    }
}
