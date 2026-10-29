extern alias ApiService;

using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Gateways;
using CreatorPantry.Domain.Modules.Auth.Managers;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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

    /// <summary>The development sink account/registration messages land in; never reachable through the gateway.</summary>
    public InMemoryAccountMessageSink Messages => Factory.Services.GetRequiredService<InMemoryAccountMessageSink>();

    /// <param name="time">Optional clock shared with a gateway under test, so token lifetimes agree.</param>
    /// <param name="configureServices">Optional test doubles, applied after the host's own replacements.</param>
    public static async Task<SqliteApiHost> StartAsync(TimeProvider? time = null, Action<IServiceCollection>? configureServices = null)
    {
        var host = new SqliteApiHost();
        await host._connection.OpenAsync();

        host.Factory = new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(web =>
        {
            TestDatabase.ConfigureWithoutHealthCheck(web);
            web.ConfigureLogging(logging => logging.AddProvider(host.Logs));
            web.ConfigureTestServices(services =>
            {
                TestLogging.RemoveProcessWideLoggers(services);

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
                services.AddDbContext<CreatorPantryDbContext>(options => options
                    .UseSqlite(host._connection)
                    // Recipe.RowVersion is a SQL Server rowversion the server generates; SQLite has no
                    // equivalent, so without this every recipe insert fails on a NOT NULL column EF never
                    // sends — and DateTimeOffset, which SQLite cannot order by at all. See
                    // SqliteModelCustomizer for both, and for why the production mappings stay native.
                    .ReplaceService<IModelCustomizer, SqliteModelCustomizer>());

                // Identity's default is 100,000 rounds of PBKDF2, by design about a twentieth of a second.
                // A test creates four accounts and signs in several times, so that was most of what every
                // test in this assembly spent its time on. Nothing here is a test of how hard a hash is to
                // break; what is stored and checked is still a real Identity V3 hash.
                services.Configure<PasswordHasherOptions>(options => options.IterationCount = 1_000);

                configureServices?.Invoke(services);
            });
        });

        await host.CreateSchemaAsync();
        return host;
    }

    private static readonly SemaphoreSlim SchemaGate = new(1, 1);

    private static SqliteConnection? _schema;

    /// <summary>
    /// Gives this host's database the schema and reference rows. The first host in the process has EF Core
    /// create them; every later one is handed a copy of that database, page for page.
    /// </summary>
    /// <remarks>
    /// Creating the schema is several hundred statements, and every test that starts a host used to pay for
    /// all of them. The model is the same for every host here — one context type, one
    /// <see cref="SqliteModelCustomizer"/> — so the result is too, and SQLite's own backup is exact. The copy
    /// is taken straight after creation, before any test has written a row.
    /// </remarks>
    private async Task CreateSchemaAsync()
    {
        await SchemaGate.WaitAsync();

        try
        {
            if (_schema is not null)
            {
                _schema.BackupDatabase(_connection);
                return;
            }

            await using var scope = Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.EnsureCreatedAsync();

            var schema = new SqliteConnection("DataSource=:memory:");
            await schema.OpenAsync();
            _connection.BackupDatabase(schema);
            _schema = schema;
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    public async Task<string> CreateUserAsync(string email, string password, bool emailConfirmed = true, string displayName = "Sam")
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Stamped as registration stamps it, from the host's clock: an account's creation dates its trial (AP.7.1),
        // and an unset instant would make every test account one whose trial ended in year one.
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            EmailConfirmed = emailConfirmed,
            CreatedAt = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow,
        };

        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Code)));
        return user.Id;
    }

    /// <summary>Provisions an ops API client the way the migration service would, and returns its key.</summary>
    /// <param name="scopes">Granted <see cref="OpsScopes"/> values. Defaults to AI-usage administration.</param>
    /// <remarks>
    /// Goes through <c>IOpsApiClientDataLayer.UpsertAsync</c> and <c>OpsApiKeyHasher</c> rather than writing a
    /// row by hand, so a test authenticates against the same hash the seeder would have stored. The key is
    /// generated here and returned once; nothing reads it back out of the database afterwards, because nothing
    /// can.
    /// </remarks>
    public async Task<string> CreateOpsClientAsync(string name = "tests", params string[] scopes)
    {
        var (key, prefix, salt, hash) = OpsApiKeyHasher.Issue();

        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IOpsApiClientDataLayer>().UpsertAsync(
            name,
            prefix,
            salt,
            hash,
            string.Join(',', scopes.Length == 0 ? [OpsScopes.AiUsageAdmin] : scopes),
            TestContext.Current.CancellationToken);

        return key;
    }

    /// <summary>Switches an ops client off, the way an operator revoking a credential would.</summary>
    public async Task RevokeOpsClientAsync(string name)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var client = await context.OpsApiClients.SingleAsync(
            row => row.Name == name, TestContext.Current.CancellationToken);

        client.RevokedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A client carrying an ops key on every request.</summary>
    public HttpClient CreateOpsClient(string key)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(OpsApiKeyPolicy.Scheme, key);

        return client;
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
