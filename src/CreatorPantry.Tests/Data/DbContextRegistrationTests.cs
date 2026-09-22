extern alias ApiService;

using CreatorPantry.Domain.Auth;
using CreatorPantry.Domain.Data;
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
        Assert.DoesNotContain(user.GetForeignKeys(), fk => fk.Properties.Contains(lastWorkspace));

        string[] identityTables = ["AspNetRoles", "AspNetUserRoles", "AspNetUserClaims", "AspNetUserLogins", "AspNetUserTokens", "AspNetRoleClaims"];
        Assert.All(identityTables, table => Assert.Contains(model.GetEntityTypes(), e => e.GetTableName() == table));
    }
}
