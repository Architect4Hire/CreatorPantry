extern alias ApiService;

using CreatorPantry.Domain.Auth;
using CreatorPantry.Domain.Data;
using CreatorPantry.Domain.Tenancy;
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
    public void MigrationService_resolves_the_dbcontext_with_one_migration_target_and_the_role_seeder()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration[$"ConnectionStrings:{CreatorPantryDbContext.ConnectionName}"] = TestDatabase.ConnectionString;
        builder.AddMigrationDatabase();

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        Assert.True(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.IsSqlServer());
        Assert.Equal(nameof(CreatorPantryDbContext), Assert.Single(scope.ServiceProvider.GetServices<IMigrationTarget>()).Name);
        Assert.Single(scope.ServiceProvider.GetServices<Domain.Data.Seeding.IDataSeeder>());
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
