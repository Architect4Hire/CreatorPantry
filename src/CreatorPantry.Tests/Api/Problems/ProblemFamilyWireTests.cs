extern alias ApiService;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.ServiceDefaults;
using CreatorPantry.Tests.Gateway;
using CreatorPantry.Tests.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ApiServiceProblems = ApiService::CreatorPantry.ApiService.Http.ProblemResults;

namespace CreatorPantry.Tests.Api.Problems;

/// <summary>
/// One contract test per error family, as a browser receives it through the real Gateway (API-002/003): RFC 9457
/// shape, stable <c>code</c>, <c>traceId</c>, <c>retry</c> advice, the headers recovery depends on, and nothing a
/// caller must not see — no stack trace, exception text, connection string, storage path, or SQL.
/// </summary>
/// <remarks>
/// <para>
/// Families the product reaches on its own (validation, malformed bodies, 401/403/404, edge rejections) are
/// provoked for real. The rest are answered by a scripted <see cref="IAiFeatureFacade"/>, so one route can
/// return each refusal the domain can produce: what is under test is the mapping from an
/// <see cref="OperationError"/> or an exception to the wire, which every controller shares. Whether each module
/// raises the right code is that module's own endpoint tests.
/// </para>
/// </remarks>
public sealed class ProblemFamilyWireTests : IAsyncLifetime
{
    private readonly FacadeScript _script = new();

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            var original = services.Last(descriptor => descriptor.ServiceType == typeof(IAiFeatureFacade));
            services.Remove(original);
            services.AddScoped<IAiFeatureFacade>(provider => new ScriptedAiFeatureFacade(
                _script,
                (IAiFeatureFacade)(original.ImplementationFactory?.Invoke(provider)
                    ?? ActivatorUtilities.CreateInstance(provider, original.ImplementationType!))));
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string FeaturesIn(string slug) => $"/api/v1/workspaces/{slug}/ai-features";

    private string FeaturesInA => FeaturesIn(_fixture.WorkspaceA.Slug);

    // ---- Families the product reaches on its own -------------------------------------------------------------

    [Fact]
    public async Task Validation_names_each_field_and_is_not_retried()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await owner.SendAsync(HttpMethod.Put, FeaturesInA, new { enabled = new[] { "diagnostic" } }, null, Ct);

        var body = await AssertProblemAsync(response, HttpStatusCode.BadRequest, AiFeatureErrors.Invalid, ProblemRetry.None);
        Assert.True(body.GetProperty("errors").TryGetProperty("enabled[0]", out _));
    }

    /// <summary>
    /// An unreadable value is named by its path, but in the framework's generic words: System.Text.Json's own
    /// sentence names the CLR type it was bound to and the byte it failed at.
    /// </summary>
    [Fact]
    public async Task A_malformed_body_names_the_field_without_describing_the_server()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await owner.SendAsync(HttpMethod.Put, FeaturesInA, new { enabled = 5 }, null, Ct);

        var body = await AssertProblemAsync(
            response, HttpStatusCode.BadRequest, ApiServiceProblems.MalformedRequestCode, ProblemRetry.None);
        var errors = body.GetProperty("errors").EnumerateObject().ToList();
        Assert.NotEmpty(errors);
        Assert.All(
            errors.SelectMany(error => error.Value.EnumerateArray()),
            message => Assert.DoesNotContain("convert", message.GetString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task No_session_is_401_and_is_not_retried()
    {
        using var anonymous = _fixture.Gateway.CreateClient(GatewayTestHost.HttpsClient);

        var response = await anonymous.GetAsync(FeaturesInA, Ct);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized", ProblemRetry.None);
    }

    [Fact]
    public async Task A_role_below_the_policy_is_403_and_is_not_retried()
    {
        using var editor = await _fixture.SignInAsync(_fixture.WorkspaceA.MemberEmail, cancellationToken: Ct);

        var response = await editor.SendAsync(HttpMethod.Put, FeaturesInA, new { enabled = Array.Empty<string>() }, null, Ct);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "forbidden", ProblemRetry.None);
    }

    /// <summary>
    /// A workspace the caller cannot see and one that does not exist are one answer, byte for byte apart from the
    /// trace — so no part of the problem, <c>retry</c> included, can be used to probe for a neighbour.
    /// </summary>
    [Fact]
    public async Task Another_workspace_and_no_workspace_are_the_same_404()
    {
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var neighbour = await AssertProblemAsync(
            await ownerA.GetAsync(FeaturesIn(_fixture.WorkspaceB.Slug), Ct), HttpStatusCode.NotFound, "not_found", ProblemRetry.None);
        var nobody = await AssertProblemAsync(
            await ownerA.GetAsync(FeaturesIn("no-such-kitchen"), Ct), HttpStatusCode.NotFound, "not_found", ProblemRetry.None);

        Assert.Equal(WithoutTrace(nobody), WithoutTrace(neighbour));
    }

    [Fact]
    public async Task A_body_over_the_edge_limit_is_413_and_is_not_retried()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await owner.PostAsync(
            FeaturesInA, new ByteArrayContent(new byte[(int)EdgeHardening.DefaultMaxRequestBodyBytes + 1]), cancellationToken: Ct);

        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, EdgeHardening.RequestTooLargeCode, ProblemRetry.None);
    }

    [Fact]
    public async Task A_missing_antiforgery_token_is_400_and_is_not_retried()
    {
        using var anonymous = _fixture.Gateway.CreateClient(GatewayTestHost.HttpsClient);

        var response = await anonymous.PutAsync(FeaturesInA, new StringContent("{}"), Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "csrf.invalid", ProblemRetry.None);
    }

    [Fact]
    public async Task The_edge_rate_limit_says_to_wait_and_until_when()
    {
        using var anonymous = _fixture.Gateway.CreateClient(GatewayTestHost.HttpsClient);
        var token = (await anonymous.GetFromJsonAsync<JsonElement>("/bff/antiforgery", Ct))
            .GetProperty("requestToken").GetString()!;

        HttpResponseMessage last = null!;
        for (var attempt = 0; attempt < 12 && last?.StatusCode != HttpStatusCode.TooManyRequests; attempt++)
        {
            using var login = new HttpRequestMessage(HttpMethod.Post, "/bff/login")
            {
                Content = JsonContent.Create(new { email = "nobody@example.com", password = "not the password" }),
            };
            login.Headers.Add("X-XSRF-TOKEN", token);
            last = await anonymous.SendAsync(login, Ct);
        }

        await AssertProblemAsync(last, HttpStatusCode.TooManyRequests, "rate_limited", ProblemRetry.Later);
        Assert.NotNull(last.Headers.RetryAfter);
    }

    // ---- Families answered through the shared mapping -------------------------------------------------------

    [Theory]
    [InlineData(RecipeErrorCodes.RecipeConflict, HttpStatusCode.Conflict, ProblemRetry.Reread)]
    [InlineData(MediaErrorCodes.AssetVersionTaken, HttpStatusCode.Conflict, ProblemRetry.Same)]
    [InlineData(RecipeErrorCodes.RecipeArchivedConflict, HttpStatusCode.Conflict, ProblemRetry.None)]
    [InlineData(AiProposalErrors.QuotaSuspended, HttpStatusCode.Forbidden, ProblemRetry.None)]

    // An entitlement refusal, which is the same family as a suspension: 403, and `none`, because the same
    // request cannot succeed until the account's plan changes (AP.4.3).
    [InlineData(MediaErrorCodes.GenerationQualityClassForbidden, HttpStatusCode.Forbidden, ProblemRetry.None)]

    // The ops plan routes (AP.4.4). A contended chain is the one of the four worth retrying, and only after
    // reading it again, which is what `reread` says; the other three cannot be helped by sending them again.
    [InlineData(AccountPlanAdministrationErrors.Unprocessable, HttpStatusCode.UnprocessableEntity, ProblemRetry.None)]
    [InlineData(AccountPlanAdministrationErrors.Conflict, HttpStatusCode.Conflict, ProblemRetry.Reread)]
    [InlineData(AccountPlanAdministrationErrors.ChangeScheduled, HttpStatusCode.Conflict, ProblemRetry.None)]
    [InlineData(AccountPlanAdministrationErrors.NoCatalogue, HttpStatusCode.NotFound, ProblemRetry.None)]
    [InlineData("probe.record.gone", HttpStatusCode.Gone, ProblemRetry.None)]
    [InlineData(BrandErrorCodes.SourceFileTooLarge, HttpStatusCode.RequestEntityTooLarge, ProblemRetry.None)]
    [InlineData(IdempotencyPolicy.KeyReusedCode, HttpStatusCode.UnprocessableEntity, ProblemRetry.None)]
    [InlineData(PublishingErrorCodes.ProviderNotConfigured, HttpStatusCode.UnprocessableEntity, ProblemRetry.None)]
    [InlineData(MediaErrorCodes.AssetStorageUnavailable, HttpStatusCode.ServiceUnavailable, ProblemRetry.Later)]
    public async Task Each_family_carries_its_status_code_and_recovery(string code, HttpStatusCode status, string retry)
    {
        _script.Answer = () => Refusal(code);
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await owner.GetAsync(FeaturesInA, Ct);

        await AssertProblemAsync(response, status, code, retry);
        Assert.Null(response.Headers.RetryAfter);
    }

    [Theory]
    [InlineData(AiProposalErrors.QuotaExhausted, HttpStatusCode.TooManyRequests)]
    [InlineData(MediaErrorCodes.AssetStorageUnavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task A_temporary_refusal_that_knows_when_it_ends_says_so(string code, HttpStatusCode status)
    {
        var resetsAt = new DateTimeOffset(2037, 3, 1, 0, 0, 0, TimeSpan.Zero);
        _script.Answer = () => Refusal(code, new Dictionary<string, object?> { ["resetsAt"] = resetsAt });
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await owner.GetAsync(FeaturesInA, Ct);

        await AssertProblemAsync(response, status, code, ProblemRetry.Later);
        Assert.Equal(resetsAt, response.Headers.RetryAfter?.Date);
    }

    /// <summary>
    /// <c>code</c>, <c>retry</c> and <c>traceId</c> are the mapping's to set. An application error that could
    /// overwrite one could lie about which refusal it is, how to recover, or which request it belongs to.
    /// </summary>
    [Fact]
    public async Task An_application_error_cannot_rewrite_the_fields_the_mapping_owns()
    {
        _script.Answer = () => Refusal(RecipeErrorCodes.RecipeArchivedConflict, new Dictionary<string, object?>
        {
            ["code"] = "something.else",
            ["retry"] = ProblemRetry.Same,
            ["traceId"] = "forged",
            ["archivedAt"] = "2037-01-01",
        });
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var body = await AssertProblemAsync(
            await owner.GetAsync(FeaturesInA, Ct), HttpStatusCode.Conflict, RecipeErrorCodes.RecipeArchivedConflict, ProblemRetry.None);

        Assert.NotEqual("forged", body.GetProperty("traceId").GetString());
        Assert.Equal("2037-01-01", body.GetProperty("archivedAt").GetString());
    }

    [Fact]
    public async Task An_unhandled_fault_is_500_and_says_nothing_about_it()
    {
        _script.Answer = () => throw new InvalidOperationException(
            "Server=sql;Database=cp;Password=hunter2 while writing C:\\storage\\blobs\\workspace\\a.jpg: SELECT * FROM Recipes");
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var body = await AssertProblemAsync(
            await owner.GetAsync(FeaturesInA, Ct), HttpStatusCode.InternalServerError, "internal_error", ProblemRetry.None);

        Assert.False(body.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String);
    }

    /// <summary>A bad request the framework throws mid-read is the caller's, not a 500 of ours.</summary>
    [Fact]
    public async Task A_bad_request_the_framework_throws_keeps_its_status()
    {
        _script.Answer = () => throw new BadHttpRequestException(
            "Request body too large while buffering to C:\\storage\\tmp\\upload.bin", StatusCodes.Status413PayloadTooLarge);
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        await AssertProblemAsync(
            await owner.GetAsync(FeaturesInA, Ct), HttpStatusCode.RequestEntityTooLarge, EdgeHardening.RequestTooLargeCode, ProblemRetry.None);
    }

    /// <summary>
    /// The safety net for a write whose business layer did not catch the row moving under it: a 409 the caller
    /// recovers from by reading again, rather than a 500 that tells them nothing.
    /// </summary>
    [Fact]
    public async Task An_uncaught_concurrency_failure_is_a_stale_conflict_to_reread()
    {
        _script.Answer = () => throw new DbUpdateConcurrencyException(
            "The database operation was expected to affect 1 row(s), but actually affected 0 row(s). UPDATE [AiFeatures]");
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        await AssertProblemAsync(
            await owner.GetAsync(FeaturesInA, Ct), HttpStatusCode.Conflict, ApiServiceProblems.ConcurrencyConflictCode, ProblemRetry.Reread);
    }

    // ---- Shared assertions ----------------------------------------------------------------------------------

    /// <summary>The RFC 9457 shape every family shares, plus the absence of anything internal.</summary>
    private static async Task<JsonElement> AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode status, string code, string retry)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(status == response.StatusCode, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = JsonDocument.Parse(text).RootElement.Clone();
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal(retry, body.GetProperty("retry").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));

        foreach (var marker in InternalMarkers)
        {
            Assert.DoesNotContain(marker, text, StringComparison.OrdinalIgnoreCase);
        }

        return body;
    }

    /// <summary>What a stack trace, an exception message, a connection string, a path or SQL would bring with it.</summary>
    private static readonly string[] InternalMarkers =
    [
        "   at ", "System.", "Exception", "hunter2", "Password=", "Server=", "LineNumber", "BytePosition",
        "C:\\\\", "storage", "SELECT ", "UPDATE [", "row(s)",
    ];

    private static string WithoutTrace(JsonElement body) =>
        JsonSerializer.Serialize(body.EnumerateObject()
            .Where(property => property.Name != "traceId")
            .ToDictionary(property => property.Name, property => property.Value));

    private static OperationResult<AiFeaturesServiceModel> Refusal(
        string code, IReadOnlyDictionary<string, object?>? extensions = null) =>
        OperationResult<AiFeaturesServiceModel>.Failure(
            new OperationError(code, "Refused for this test.", new Dictionary<string, string[]>(), extensions));

    private sealed class FacadeScript
    {
        /// <summary>The answer every call gets; null lets the real facade answer.</summary>
        public Func<OperationResult<AiFeaturesServiceModel>>? Answer { get; set; }
    }

    private sealed class ScriptedAiFeatureFacade(FacadeScript script, IAiFeatureFacade real) : IAiFeatureFacade
    {
        public Task<OperationResult<AiFeaturesServiceModel>> GetAsync(CancellationToken cancellationToken) =>
            script.Answer is { } answer ? Task.FromResult(answer()) : real.GetAsync(cancellationToken);

        public Task<OperationResult<AiFeaturesServiceModel>> ReplaceAsync(
            ReplaceAiFeaturesViewModel model, CancellationToken cancellationToken) =>
            script.Answer is { } answer ? Task.FromResult(answer()) : real.ReplaceAsync(model, cancellationToken);
    }
}
