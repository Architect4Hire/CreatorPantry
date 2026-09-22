extern alias ApiService;

using System.Security.Cryptography;
using ApiService::CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Data;
using CreatorPantry.Domain.Facade.Idempotency;
using CreatorPantry.Domain.Idempotency;
using CreatorPantry.Domain.Models.Idempotency;
using CreatorPantry.Domain.Models.Results;
using CreatorPantry.Domain.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CreatorPantry.Tests.Idempotency;

/// <summary>The idempotency seam over a SQLite file database (one connection per scope, like requests).</summary>
public sealed class IdempotencyTests : IDisposable
{
    private const string Operation = "test.create-role";
    private const string User = "user-1";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"cp-idem-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly ServiceProvider _provider;
    private int _runs;

    public IdempotencyTests()
    {
        _provider = BuildProvider(TestDatabase.FingerprintKey);
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task First_request_runs_once_and_commits_its_write_with_the_record()
    {
        var outcome = await ExecuteAsync(Command("key-1", new { name = "Bakers" }), CreateRole("Bakers"));

        Assert.True(outcome.Result.Succeeded);
        Assert.False(outcome.Replayed);
        Assert.Equal(1, _runs);
        Assert.True(await RoleExistsAsync("Bakers"));
        Assert.Equal(1, await RecordCountAsync());
    }

    [Fact]
    public async Task Same_key_and_payload_replays_the_committed_result_without_running_again()
    {
        var first = await ExecuteAsync(Command("key-1", new { name = "Bakers" }), CreateRole("Bakers"));
        var replay = await ExecuteAsync(Command("key-1", new { name = "Bakers" }), CreateRole("Bakers"));

        Assert.True(replay.Replayed);
        Assert.Equal(first.Result.Value, replay.Result.Value);
        Assert.Equal(1, _runs);
    }

    [Fact]
    public async Task Fingerprints_are_canonical_so_property_order_does_not_matter()
    {
        await ExecuteAsync(Command("key-1", new { name = "Bakers", servings = 4 }), CreateRole("Bakers"));
        var replay = await ExecuteAsync(Command("key-1", new { servings = 4, name = "Bakers" }), CreateRole("Bakers"));

        Assert.True(replay.Replayed);
        Assert.Equal(1, _runs);
    }

    [Fact]
    public async Task Same_key_with_a_different_payload_is_rejected_and_not_run()
    {
        await ExecuteAsync(Command("key-1", new { name = "Bakers" }), CreateRole("Bakers"));

        var conflict = await ExecuteAsync(Command("key-1", new { name = "Grillers" }), CreateRole("Grillers"));

        Assert.Equal(IdempotencyPolicy.KeyReusedCode, conflict.Result.Error!.Code);
        Assert.Equal(1, _runs);
        Assert.False(await RoleExistsAsync("Grillers"));
    }

    [Fact]
    public async Task Key_runs_fresh_after_the_retention_window()
    {
        await ExecuteAsync(Command("key-1", new { name = "Bakers" }), CreateRole("Bakers"));

        _time.Advance(IdempotencyPolicy.Retention - TimeSpan.FromSeconds(1));
        var withinWindow = await ExecuteAsync(Command("key-1", new { name = "Bakers" }), CreateRole("Bakers"));
        _time.Advance(TimeSpan.FromSeconds(2));
        var afterWindow = await ExecuteAsync(Command("key-1", new { name = "Grillers" }), CreateRole("Grillers"));

        Assert.True(withinWindow.Replayed);
        Assert.False(afterWindow.Replayed);
        Assert.True(afterWindow.Result.Succeeded);
        Assert.Equal(2, _runs);
        Assert.Equal(1, await RecordCountAsync()); // the expired record was replaced, not duplicated
    }

    [Fact]
    public async Task Keys_are_scoped_to_user_workspace_and_operation()
    {
        await ExecuteAsync(Command("key-1", new { name = "A" }), CreateRole("A"));

        var otherUser = await ExecuteAsync(Command("key-1", new { name = "B" }) with { UserId = "user-2" }, CreateRole("B"));
        var otherWorkspace = await ExecuteAsync(Command("key-1", new { name = "C" }) with { WorkspaceId = Guid.NewGuid() }, CreateRole("C"));
        var otherOperation = await ExecuteAsync(Command("key-1", new { name = "D" }) with { Operation = "test.other" }, CreateRole("D"));

        Assert.All([otherUser, otherWorkspace, otherOperation], outcome => Assert.False(outcome.Replayed));
        Assert.Equal(4, _runs);
    }

    [Fact]
    public async Task Expected_failure_rolls_back_the_write_and_is_not_remembered()
    {
        var failed = await ExecuteAsync(Command("key-1", new { name = "Bakers" }), CreateRole("Bakers", fail: true));
        var retry = await ExecuteAsync(Command("key-1", new { name = "Bakers" }), CreateRole("Bakers"));

        Assert.False(failed.Result.Succeeded);
        Assert.False(retry.Replayed);
        Assert.True(retry.Result.Succeeded);
        Assert.Equal(2, _runs);
        Assert.Equal(1, await RecordCountAsync());
    }

    [Fact]
    public async Task Exception_rolls_back_the_write_and_a_retry_runs_again()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteAsync(Command("key-1", new { name = "Bakers" }), CreateRole("Bakers", throwAfterWrite: true)));

        Assert.False(await RoleExistsAsync("Bakers"));
        Assert.Equal(0, await RecordCountAsync());

        var retry = await ExecuteAsync(Command("key-1", new { name = "Bakers" }), CreateRole("Bakers"));
        Assert.True(retry.Result.Succeeded);
        Assert.False(retry.Replayed);
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_key_run_the_operation_exactly_once()
    {
        var command = Command("key-1", new { name = "Bakers" });

        var outcomes = await Task.WhenAll(
            Task.Run(() => ExecuteAsync(command, CreateRole("Bakers", delay: TimeSpan.FromMilliseconds(300)))),
            Task.Run(() => ExecuteAsync(command, CreateRole("Bakers", delay: TimeSpan.FromMilliseconds(300)))));

        Assert.Equal(1, _runs);
        Assert.All(outcomes, outcome => Assert.True(outcome.Result.Succeeded));
        Assert.Single(outcomes, outcome => outcome.Replayed);
        Assert.Equal(outcomes[0].Result.Value, outcomes[1].Result.Value);
    }

    [Fact]
    public async Task Stored_row_holds_a_keyed_hash_and_no_payload_plaintext()
    {
        const string marker = "sensitive-marker-value";
        await ExecuteAsync(Command("key-1", new { name = "Bakers", note = marker }), CreateRole("Bakers"));

        await using var scope = _provider.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().IdempotencyRecords.SingleAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(32, row.FingerprintHash.Length);
        Assert.DoesNotContain(marker, row.ResultJson + row.Key + row.Operation);
        var unkeyed = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Operation + "\n" + """{"name":"Bakers","note":"sensitive-marker-value"}"""));
        Assert.NotEqual(unkeyed, row.FingerprintHash); // an attacker without the server key cannot test guesses
    }

    [Fact]
    public async Task A_different_server_key_produces_a_different_fingerprint()
    {
        await ExecuteAsync(Command("key-1", new { name = "Bakers" }), CreateRole("Bakers"));
        var otherKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        using var otherProvider = BuildProvider(otherKey);
        await using var scope = otherProvider.CreateAsyncScope();
        var conflict = await scope.ServiceProvider.GetRequiredService<IIdempotentCommandExecutor>().ExecuteAsync(
            Command("key-1", new { name = "Bakers" }), CreateRole("Bakers"), TestContext.Current.CancellationToken);

        Assert.Equal(IdempotencyPolicy.KeyReusedCode, conflict.Result.Error!.Code);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("tab\tkey")]
    [InlineData("naïve")]
    public async Task Malformed_keys_are_rejected_without_running(string key)
    {
        var outcome = await ExecuteAsync(Command(key, new { name = "Bakers" }), CreateRole("Bakers"));

        Assert.Equal(IdempotencyPolicy.KeyInvalidCode, outcome.Result.Error!.Code);
        Assert.Equal(0, _runs);
    }

    [Fact]
    public async Task Overlong_keys_are_rejected()
    {
        var outcome = await ExecuteAsync(Command(new string('k', 256), new { }), CreateRole("Bakers"));

        Assert.Equal(IdempotencyPolicy.KeyInvalidCode, outcome.Result.Error!.Code);
    }

    [Fact]
    public async Task Missing_key_is_rejected_when_required_and_runs_unprotected_when_optional()
    {
        var required = await ExecuteAsync(Command(null, new { name = "A" }), CreateRole("A"));
        var optional = await ExecuteAsync(Command(null, new { name = "B" }) with { KeyRequired = false }, CreateRole("B"));

        Assert.Equal(IdempotencyPolicy.KeyRequiredCode, required.Result.Error!.Code);
        Assert.True(optional.Result.Succeeded);
        Assert.Equal(1, _runs);
        Assert.Equal(0, await RecordCountAsync());
    }

    [Fact]
    public async Task Binary_results_are_refused()
    {
        await using var scope = _provider.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<IIdempotentCommandExecutor>();

        await Assert.ThrowsAsync<NotSupportedException>(() => executor.ExecuteAsync(
            Command("key-1", new { }), _ => Task.FromResult(OperationResult<byte[]>.Success([1, 2, 3])),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Http_mapping_uses_422_for_a_reused_key_and_flags_replays()
    {
        var controller = new ProbeController { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        var result = controller.IdempotentResult(
            new IdempotentOutcome<CreatedRole>(OperationResult<CreatedRole>.Success(new CreatedRole("r1", 1)), Replayed: true),
            value => controller.Ok(value));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("true", controller.Response.Headers[IdempotencyPolicy.ReplayedHeader]);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, ProblemResults.StatusFor(IdempotencyPolicy.KeyReusedCode));
        Assert.Equal(StatusCodes.Status400BadRequest, ProblemResults.StatusFor(IdempotencyPolicy.KeyInvalidCode));
    }

    [Theory]
    [InlineData("")]
    [InlineData("dG9vLXNob3J0")] // base64, but under 32 bytes
    public void Api_refuses_to_start_without_a_valid_fingerprint_key(string key)
    {
        using var api = new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(web =>
        {
            TestDatabase.ConfigureWithoutHealthCheck(web);
            web.UseSetting("Idempotency:FingerprintKey", key);
        });

        var error = Assert.Throws<OptionsValidationException>(() => api.CreateClient());

        Assert.Contains("FingerprintKey", error.Message);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------

    public sealed record CreatedRole(string RoleId, int Run);

    private sealed class ProbeController : ControllerBase;

    private ServiceProvider BuildProvider(string fingerprintKey) => new ServiceCollection()
        .AddLogging()
        .AddSingleton<TimeProvider>(_time)
        .AddApplicationTime()
        .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlite($"Data Source={_databasePath};Default Timeout=30"))
        .AddIdempotency(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Idempotency:FingerprintKey"] = fingerprintKey })
            .Build())
        .BuildServiceProvider(validateScopes: true);

    private static IdempotentCommand Command(string? key, object fingerprint) => new(User, null, Operation, key, fingerprint);

    /// <summary>A real business write through the request's DbContext, counting how often it runs.</summary>
    private Func<CancellationToken, Task<OperationResult<CreatedRole>>> CreateRole(
        string name, bool fail = false, bool throwAfterWrite = false, TimeSpan? delay = null) => async cancellationToken =>
    {
        var run = Interlocked.Increment(ref _runs);
        var context = CurrentContext!;
        var role = new IdentityRole(name) { NormalizedName = name.ToUpperInvariant() };
        context.Roles.Add(role);
        await context.SaveChangesAsync(cancellationToken);

        if (delay is not null)
        {
            await Task.Delay(delay.Value, cancellationToken);
        }

        if (throwAfterWrite)
        {
            throw new InvalidOperationException("Simulated failure after the business write.");
        }

        return fail
            ? OperationResult<CreatedRole>.Failure(new OperationError("test.failed", "Failed.", new Dictionary<string, string[]>()))
            : OperationResult<CreatedRole>.Success(new CreatedRole(role.Id, run));
    };

    private static readonly AsyncLocal<CreatorPantryDbContext?> CurrentContextSlot = new();

    private static CreatorPantryDbContext? CurrentContext => CurrentContextSlot.Value;

    private async Task<IdempotentOutcome<CreatedRole>> ExecuteAsync(
        IdempotentCommand command, Func<CancellationToken, Task<OperationResult<CreatedRole>>> operation)
    {
        await using var scope = _provider.CreateAsyncScope();
        CurrentContextSlot.Value = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        return await scope.ServiceProvider.GetRequiredService<IIdempotentCommandExecutor>()
            .ExecuteAsync(command, operation, TestContext.Current.CancellationToken);
    }

    private async Task<bool> RoleExistsAsync(string name)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Roles
            .AnyAsync(role => role.Name == name, TestContext.Current.CancellationToken);
    }

    private async Task<int> RecordCountAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().IdempotencyRecords
            .CountAsync(TestContext.Current.CancellationToken);
    }
}
