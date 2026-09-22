extern alias ApiService;

using CreatorPantry.Domain.Data;
using CreatorPantry.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Tests;

/// <summary>The real ApiService over an in-memory SQLite database, with captured logs.</summary>
internal sealed class SqliteApiHost : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    private SqliteApiHost()
    {
    }

    public WebApplicationFactory<ApiService::Program> Factory { get; private set; } = null!;

    public CapturingLoggerProvider Logs { get; } = new();

    /// <param name="time">Optional clock shared with a gateway under test, so token lifetimes agree.</param>
    public static async Task<SqliteApiHost> StartAsync(TimeProvider? time = null)
    {
        var host = new SqliteApiHost();
        await host._connection.OpenAsync();

        host.Factory = new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(web =>
        {
            TestDatabase.ConfigureWithoutHealthCheck(web);
            web.ConfigureLogging(logging => logging.AddProvider(host.Logs));
            web.ConfigureTestServices(services =>
            {
                if (time is not null)
                {
                    services.AddSingleton(time);
                }

                // Replace the Aspire SQL Server registration without naming EF Core's internal pool types.
                var contextRegistrations = services
                    .Where(descriptor => descriptor.ServiceType == typeof(CreatorPantryDbContext)
                        || descriptor.ServiceType.GenericTypeArguments.Contains(typeof(CreatorPantryDbContext)))
                    .ToList();
                contextRegistrations.ForEach(descriptor => services.Remove(descriptor));
                services.AddDbContext<CreatorPantryDbContext>(options => options.UseSqlite(host._connection));
            });
        });

        await using var scope = host.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.EnsureCreatedAsync();
        return host;
    }

    public async Task<string> CreateUserAsync(string email, string password, bool emailConfirmed = true, string displayName = "Sam")
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = displayName, EmailConfirmed = emailConfirmed };

        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Code)));
        return user.Id;
    }

    public async Task ChangePasswordAsync(string userId, string current, string next)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var result = await users.ChangePasswordAsync((await users.FindByIdAsync(userId))!, current, next);
        Assert.True(result.Succeeded);
    }

    public async Task<string> SecurityStampAsync(string userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await users.GetSecurityStampAsync((await users.FindByIdAsync(userId))!))!;
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
