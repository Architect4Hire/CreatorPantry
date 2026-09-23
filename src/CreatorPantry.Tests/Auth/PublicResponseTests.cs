extern alias ApiService;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Gateways;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Auth;

/// <summary>
/// The real ApiService pipeline over SQLite: public responses for known and unknown addresses must be
/// byte-identical apart from the per-request trace id.
/// </summary>
public sealed class PublicResponseTests : IAsyncLifetime
{
    private const string Known = "cook@example.com";
    private const string Password = "correct horse battery";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private WebApplicationFactory<ApiService::Program> _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();

        _factory = new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(web =>
        {
            TestDatabase.ConfigureWithoutHealthCheck(web);
            web.ConfigureTestServices(services =>
            {
                // Replace the Aspire SQL Server registration (pooled context, options, configuration)
                // with SQLite, without naming EF Core's internal pool types.
                var contextRegistrations = services
                    .Where(descriptor => descriptor.ServiceType == typeof(CreatorPantryDbContext)
                        || descriptor.ServiceType.GenericTypeArguments.Contains(typeof(CreatorPantryDbContext)))
                    .ToList();
                contextRegistrations.ForEach(descriptor => services.Remove(descriptor));

                services.AddDbContext<CreatorPantryDbContext>(options => options.UseSqlite(_connection));
            });
        });
        _client = _factory.CreateClient();

        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.EnsureCreatedAsync();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var result = await users.CreateAsync(
            new ApplicationUser { UserName = Known, Email = Known, DisplayName = "Sam", EmailConfirmed = true }, Password);
        Assert.True(result.Succeeded);

        await users.CreateAsync(
            new ApplicationUser { UserName = "pending@example.com", Email = "pending@example.com", DisplayName = "Pat" }, Password);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Reset_request_is_identical_for_known_unconfirmed_and_unknown_addresses()
    {
        var known = await PostAsync("/api/v1/auth/password-reset", new { email = Known });
        var unconfirmed = await PostAsync("/api/v1/auth/password-reset", new { email = "pending@example.com" });
        var unknown = await PostAsync("/api/v1/auth/password-reset", new { email = "nobody@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, known.Status);
        Assert.Equal(known, unconfirmed);
        Assert.Equal(known, unknown);

        // Only the confirmed account was actually sent a message.
        var sink = _factory.Services.GetRequiredService<InMemoryAccountMessageSink>();
        Assert.Equal([Known], sink.Messages.Select(message => message.RecipientEmail));
    }

    [Fact]
    public async Task Reset_completion_is_identical_for_unknown_address_and_bad_token()
    {
        var body = (string email) => new { email, token = "dGFtcGVyZWQ", newPassword = "a brand new passphrase" };

        var known = await PostAsync("/api/v1/auth/password-reset/complete", body(Known));
        var unknown = await PostAsync("/api/v1/auth/password-reset/complete", body("nobody@example.com"));

        Assert.Equal(HttpStatusCode.BadRequest, known.Status);
        Assert.Equal(known, unknown);
    }

    [Fact]
    public async Task Registration_is_identical_for_new_and_existing_addresses()
    {
        var body = (string email) => new { email, password = Password, displayName = "Sam" };

        var existing = await PostAsync("/api/v1/auth/register", body(Known));
        var fresh = await PostAsync("/api/v1/auth/register", body("new@example.com"));

        Assert.Equal(HttpStatusCode.Accepted, existing.Status);
        Assert.Equal(existing, fresh);
    }

    [Fact]
    public async Task Development_endpoint_exposes_sink_messages_for_manual_testing()
    {
        await PostAsync("/api/v1/auth/password-reset", new { email = Known });

        var response = await _client.GetAsync("/_dev/account-messages", TestContext.Current.CancellationToken);
        var messages = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var message = Assert.Single(messages.EnumerateArray());
        Assert.Equal("PasswordReset", message.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrEmpty(message.GetProperty("token").GetString()));
    }

    /// <summary>Status, content type, and body with the per-request traceId removed.</summary>
    private async Task<PublicResponse> PostAsync(string route, object body)
    {
        var response = await _client.PostAsJsonAsync(route, body, TestContext.Current.CancellationToken);
        var json = JsonNodeWithoutTraceId(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return new PublicResponse(response.StatusCode, response.Content.Headers.ContentType?.ToString(), json);
    }

    private static string JsonNodeWithoutTraceId(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node.Remove("traceId");
        return node.ToJsonString();
    }

    private sealed record PublicResponse(HttpStatusCode Status, string? ContentType, string Body);
}
