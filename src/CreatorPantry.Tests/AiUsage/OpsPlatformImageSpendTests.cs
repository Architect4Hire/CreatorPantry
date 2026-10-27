using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// What an operator can see of the platform's image spend breaker and what lifting it does (PRICE-005,
/// AP.2.7). Who may reach these routes at all is <see cref="OpsAccessTests"/>, which lists both.
/// </summary>
public sealed class OpsPlatformImageSpendTests
{
    private const string Route = "/api/v1/ops/ai-usage/platform/image-spend";

    private const string Lift = "/api/v1/ops/ai-usage/platform/image-spend/lift";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The ceiling this host was configured with: read, so the test is about the shape and not the figure.</summary>
    private static decimal Ceiling(SqliteApiHost host) =>
        host.Factory.Services.GetRequiredService<IOptions<AiQuotaOptions>>().Value.PlatformImageSpendPerDay
        ?? throw new InvalidOperationException("The API's appsettings.json carries a ceiling.");

    private static DateOnly Today(SqliteApiHost host) =>
        PlatformImageSpendPolicy.DayOf(host.Factory.Services.GetRequiredService<IClock>().UtcNow);

    /// <summary>
    /// A day nothing was admitted on reads as armed at nought, against the configured ceiling, in dollars — and
    /// carries nothing but figures: no account, no workspace, no model.
    /// </summary>
    [Fact]
    public async Task Today_with_nothing_admitted_reads_armed_at_nought()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var response = await ops.GetAsync(Route, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await JsonAsync(response);
        Assert.Equal(Today(host).ToString("yyyy-MM-dd"), body.GetProperty("day").GetString());
        Assert.Equal("USD", body.GetProperty("currency").GetString());
        Assert.Equal(Ceiling(host), body.GetProperty("ceiling").GetDecimal());
        Assert.Equal(0m, body.GetProperty("admitted").GetDecimal());
        Assert.Equal(Ceiling(host), body.GetProperty("remaining").GetDecimal());
        Assert.Equal("Armed", body.GetProperty("state").GetString());

        Assert.Equal(
            ["day", "currency", "ceiling", "admitted", "remaining", "state", "trippedAt", "liftedAt", "liftedBy"],
            body.EnumerateObject().Select(property => property.Name));

        // A GET writes nothing, not even the day's row.
        Assert.Null(await DayAsync(host, Today(host)));
    }

    /// <summary>A tripped day says so, with what was admitted and what is left of the ceiling.</summary>
    [Fact]
    public async Task A_tripped_day_reads_tripped_with_its_figures()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());
        var tripped = DateTimeOffset.UtcNow;

        await SeedAsync(host, new PlatformImageSpendDay
        {
            Day = Today(host),
            AdmittedMicros = PlatformImageSpendPolicy.ToMicros(Ceiling(host)) - 20_000,
            TrippedAt = tripped,
        });

        var body = await JsonAsync(await ops.GetAsync(Route, Ct));

        Assert.Equal("Tripped", body.GetProperty("state").GetString());
        Assert.Equal(Ceiling(host) - 0.02m, body.GetProperty("admitted").GetDecimal());
        Assert.Equal(0.02m, body.GetProperty("remaining").GetDecimal());
        Assert.Equal(JsonValueKind.String, body.GetProperty("trippedAt").ValueKind);
    }

    /// <summary>An operator can read a past day by naming it.</summary>
    [Fact]
    public async Task A_named_day_is_read()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        await SeedAsync(host, new PlatformImageSpendDay { Day = new DateOnly(2026, 1, 2), AdmittedMicros = 1_250_000 });

        var body = await JsonAsync(await ops.GetAsync($"{Route}?day=2026-01-02", Ct));

        Assert.Equal("2026-01-02", body.GetProperty("day").GetString());
        Assert.Equal(1.25m, body.GetProperty("admitted").GetDecimal());
    }

    /// <summary>
    /// Lifting is audited in the ops client's name with the reason it gave, reports the day as lifted, and says
    /// who lifted it on the read.
    /// </summary>
    [Fact]
    public async Task Lifting_reports_the_day_lifted_and_audits_who_and_why()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync("launch-desk"));

        var response = await ops.PostAsJsonAsync(Lift, new { reason = "Launch day; spend reviewed with the owner." }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await JsonAsync(response);
        Assert.Equal("Lifted", body.GetProperty("outcome").GetString());
        Assert.Equal("Lifted", body.GetProperty("day").GetProperty("state").GetString());
        Assert.Equal("launch-desk", body.GetProperty("day").GetProperty("liftedBy").GetString());

        var reread = await JsonAsync(await ops.GetAsync(Route, Ct));
        Assert.Equal("Lifted", reread.GetProperty("state").GetString());

        var audit = Assert.Single(await AuditAsync(host));
        Assert.Equal(PlatformAuditActorType.OpsClient, audit.ActorType);
        Assert.Equal("launch-desk", audit.ActorName);
        Assert.Equal(PlatformImageSpendAuditActions.Lifted, audit.Action);
        Assert.Equal(PlatformImageSpendAuditActions.SubjectType, audit.SubjectType);
        Assert.Equal(Today(host).ToString("yyyy-MM-dd"), audit.SubjectId);
        Assert.Equal("Launch day; spend reviewed with the owner.", audit.Reason);
        Assert.StartsWith("state=Armed;", audit.BeforeReference);
        Assert.StartsWith("state=Lifted;", audit.AfterReference);
    }

    /// <summary>
    /// A second lift of the same day changes nothing and writes nothing — which is what makes a retried lift
    /// safe without an idempotency key, and keeps every audit row a change somebody actually made.
    /// </summary>
    [Fact]
    public async Task Lifting_a_lifted_day_is_unchanged_and_writes_no_second_audit_row()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        await ops.PostAsJsonAsync(Lift, new { reason = "First lift." }, Ct);
        var again = await ops.PostAsJsonAsync(Lift, new { reason = "Second lift." }, Ct);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("Unchanged", (await JsonAsync(again)).GetProperty("outcome").GetString());
        Assert.Single(await AuditAsync(host));
    }

    /// <summary>A lift must say why; without a reason it is refused and nothing is lifted.</summary>
    [Fact]
    public async Task A_lift_without_a_reason_is_refused_and_lifts_nothing()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var response = await ops.PostAsJsonAsync(Lift, new { reason = "  " }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            AiUsageAdministrationErrors.ImageSpendLiftInvalid,
            (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Null((await DayAsync(host, Today(host)))?.LiftedAt);
        Assert.Empty(await AuditAsync(host));
    }

    /// <summary>A host with no ceiling reads as off, and has no breaker to lift.</summary>
    [Fact]
    public async Task A_host_with_no_ceiling_reads_off_and_cannot_be_lifted()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: services =>
            services.PostConfigure<AiQuotaOptions>(options => options.PlatformImageSpendPerDay = null));
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var read = await JsonAsync(await ops.GetAsync(Route, Ct));
        Assert.Equal("Off", read.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("ceiling").ValueKind);
        Assert.Equal(JsonValueKind.Null, read.GetProperty("remaining").ValueKind);

        var lift = await ops.PostAsJsonAsync(Lift, new { reason = "Nothing to lift." }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, lift.StatusCode);
        Assert.Equal(
            AiUsageAdministrationErrors.ImageSpendCeilingOff,
            (await JsonAsync(lift)).GetProperty("code").GetString());
        Assert.Empty(await AuditAsync(host));
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static async Task SeedAsync(SqliteApiHost host, PlatformImageSpendDay day)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.PlatformImageSpendDays.Add(day);
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<PlatformImageSpendDay?> DayAsync(SqliteApiHost host, DateOnly day)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .PlatformImageSpendDays.AsNoTracking().SingleOrDefaultAsync(row => row.Day == day, Ct);
    }

    private static async Task<List<PlatformAuditLog>> AuditAsync(SqliteApiHost host)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .PlatformAuditLogs.AsNoTracking()
            .Where(log => log.SubjectType == PlatformImageSpendAuditActions.SubjectType)
            .ToListAsync(Ct);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
}
