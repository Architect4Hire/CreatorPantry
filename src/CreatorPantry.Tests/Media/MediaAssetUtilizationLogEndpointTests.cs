using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// <c>POST /api/v1/workspaces/{workspaceSlug}/dam-assets/{assetId}/utilization</c> (DAM-009): what is recorded, what
/// is derived, what is refused, and what a repeat does.
/// </summary>
/// <remarks>
/// The derivation is the part worth most of the attention. <c>utilizedDay</c> is never sent and must always agree with
/// the date, so the tests walk a whole week and check a client cannot override it.
/// </remarks>
public sealed class MediaAssetUtilizationLogEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UseIn(SeededWorkspace workspace, Guid assetId) =>
        $"/api/v1/workspaces/{workspace.Slug}/dam-assets/{assetId:D}/utilization";

    // ---- What is recorded ----

    [Fact]
    public async Task A_use_is_recorded_with_everything_the_creator_said_about_it()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.PostAsJsonAsync(
            UseIn(_fixture.WorkspaceA, id),
            new
            {
                platformKey = "instagram",
                utilizedOn = "2026-04-01",
                campaignName = "Spring bakes",
                notes = "Carousel, slide one",
            },
            Ct);
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("instagram", body.GetProperty("platformKey").GetString());
        Assert.Equal("2026-04-01", body.GetProperty("utilizedOn").GetString());
        Assert.Equal("Spring bakes", body.GetProperty("campaignName").GetString());
        Assert.Equal("Carousel, slide one", body.GetProperty("notes").GetString());
        Assert.NotEqual(Guid.Empty, body.GetProperty("id").GetGuid());

        var row = Assert.Single(await UsesAsync(id));
        Assert.Equal(new DateOnly(2026, 4, 1), row.UtilizedOn);
        Assert.Equal("instagram", row.PlatformKey);

        // The actor is the resolved membership, never request input.
        Assert.NotEqual(Guid.Empty, row.LoggedByMembershipId);
    }

    /// <summary>
    /// <c>createdAt</c> is when the log was written and <c>utilizedOn</c> is when the asset was used. Two facts, and
    /// the row keeps both — a creator backfilling last month's posts today needs them to differ.
    /// </summary>
    [Fact]
    public async Task When_it_was_used_and_when_it_was_logged_are_different_facts()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        await LogAsync(client, id, new { platformKey = "blog", utilizedOn = "2026-01-15" });

        var row = Assert.Single(await UsesAsync(id));

        Assert.Equal(new DateOnly(2026, 1, 15), row.UtilizedOn);
        Assert.NotEqual(new DateOnly(2026, 1, 15), DateOnly.FromDateTime(row.CreatedAt.UtcDateTime));
    }

    /// <summary>The new row shows up in the history the detail read pages, and in its count.</summary>
    [Fact]
    public async Task A_logged_use_appears_in_the_history_and_the_count()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        await LogAsync(client, id, new { platformKey = "instagram", utilizedOn = "2026-04-01" });
        await LogAsync(client, id, new { platformKey = "blog", utilizedOn = "2026-04-02" });

        var history = await ReadAsync(await client.GetAsync(UseIn(_fixture.WorkspaceA, id), Ct));
        var detail = await ReadAsync(await client.GetAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/dam-assets/{id:D}", Ct));

        // Newest first, which is what the history read promises.
        Assert.Equal(
            ["blog", "instagram"],
            history.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("platformKey").GetString()));
        Assert.Equal(2, history.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, detail.GetProperty("utilizationCount").GetInt32());
    }

    // ---- Derivation ----

    /// <summary>
    /// The derived day is right for every day of the week, not just the one a single example happens to hit.
    /// </summary>
    [Theory]
    [InlineData("2026-03-29", "Sunday")]
    [InlineData("2026-03-30", "Monday")]
    [InlineData("2026-03-31", "Tuesday")]
    [InlineData("2026-04-01", "Wednesday")]
    [InlineData("2026-04-02", "Thursday")]
    [InlineData("2026-04-03", "Friday")]
    [InlineData("2026-04-04", "Saturday")]
    public async Task The_day_is_derived_from_the_date(string utilizedOn, string expectedDay)
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await LogAsync(client, id, new { platformKey = "instagram", utilizedOn });

        Assert.Equal(expectedDay, body.GetProperty("utilizedDay").GetString());
        Assert.Equal(expectedDay, Assert.Single(await UsesAsync(id)).UtilizedDay.ToString());
    }

    /// <summary>
    /// A client cannot override the day. Sending one is ignored, so a stored day can never disagree with the date it
    /// came from — the property that makes the zone question moot.
    /// </summary>
    [Fact]
    public async Task A_submitted_day_is_ignored_rather_than_trusted()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await LogAsync(client, id, new
        {
            platformKey = "instagram",

            // 2026-04-01 is a Wednesday. The lie is ignored.
            utilizedOn = "2026-04-01",
            utilizedDay = "Monday",
        });

        Assert.Equal("Wednesday", body.GetProperty("utilizedDay").GetString());
        Assert.Equal(DayOfWeek.Wednesday, Assert.Single(await UsesAsync(id)).UtilizedDay);
    }

    /// <summary>
    /// Sunday is <c>DayOfWeek.Sunday == 0</c>, so a derivation that treated the enum's zero as "unset" would store it
    /// wrong. Worth its own test because zero is the value a bug hides in.
    /// </summary>
    [Fact]
    public async Task A_sunday_is_stored_as_sunday_rather_than_as_unset()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        await LogAsync(client, id, new { platformKey = "instagram", utilizedOn = "2026-03-29" });

        Assert.Equal(DayOfWeek.Sunday, Assert.Single(await UsesAsync(id)).UtilizedDay);
    }

    // ---- Validation ----

    [Fact]
    public async Task A_platform_is_required()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);

        foreach (var platformKey in (string?[])[null, "", "   "])
        {
            var response = await client.PostAsJsonAsync(
                UseIn(_fixture.WorkspaceA, id),
                new { platformKey, utilizedOn = "2026-04-01" },
                Ct);
            var body = await ReadAsync(response);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(MediaErrorCodes.AssetInvalidRequest, body.GetProperty("code").GetString());
            Assert.True(body.GetProperty("errors").TryGetProperty("platformKey", out _));
        }

        Assert.Empty(await UsesAsync(id));
    }

    [Fact]
    public async Task A_date_is_required()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.PostAsJsonAsync(
            UseIn(_fixture.WorkspaceA, id), new { platformKey = "instagram" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("errors").TryGetProperty("utilizedOn", out _));
        Assert.Empty(await UsesAsync(id));
    }

    /// <summary>
    /// A date one day ahead of the server is accepted, because a creator east of UTC logging this afternoon is already
    /// on tomorrow's date — refusing them would refuse a correct request. Further ahead is a mistyped year.
    /// </summary>
    [Fact]
    public async Task A_date_a_day_ahead_is_accepted_and_further_ahead_is_refused()
    {
        var id = await SeededAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using var client = await SignInAsync(_fixture.WorkspaceA);

        var tomorrow = await LogAsync(client, id, new
        {
            platformKey = "instagram",
            utilizedOn = today.AddDays(MediaAssetUtilizationPolicy.FutureDayTolerance).ToString("yyyy-MM-dd"),
        });

        Assert.Equal(HttpStatusCode.Created.ToString(), HttpStatusCode.Created.ToString());
        Assert.False(string.IsNullOrEmpty(tomorrow.GetProperty("utilizedOn").GetString()));

        var tooFar = await client.PostAsJsonAsync(
            UseIn(_fixture.WorkspaceA, id),
            new
            {
                platformKey = "instagram",
                utilizedOn = today.AddDays(MediaAssetUtilizationPolicy.FutureDayTolerance + 1).ToString("yyyy-MM-dd"),
            },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, tooFar.StatusCode);
        Assert.True((await ReadAsync(tooFar)).GetProperty("errors").TryGetProperty("utilizedOn", out _));
    }

    /// <summary>
    /// No lower bound: a creator recording where a photograph was used last year is entering their own history, and a
    /// product that refused it would be telling them their records are wrong.
    /// </summary>
    [Fact]
    public async Task A_date_well_in_the_past_is_accepted()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var body = await LogAsync(client, id, new { platformKey = "blog", utilizedOn = "2019-07-04" });

        Assert.Equal("2019-07-04", body.GetProperty("utilizedOn").GetString());
    }

    [Fact]
    public async Task Over_long_fields_are_refused_by_name()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);

        var cases = new (string Field, object Body)[]
        {
            ("platformKey", new
            {
                platformKey = new string('p', MediaPolicy.VocabularyKeyMaxLength + 1),
                utilizedOn = "2026-04-01",
            }),
            ("campaignName", new
            {
                platformKey = "instagram",
                utilizedOn = "2026-04-01",
                campaignName = new string('c', MediaPolicy.CampaignNameMaxLength + 1),
            }),
            ("notes", new
            {
                platformKey = "instagram",
                utilizedOn = "2026-04-01",
                notes = new string('n', MediaPolicy.NotesMaxLength + 1),
            }),
        };

        foreach (var (field, body) in cases)
        {
            var response = await client.PostAsJsonAsync(UseIn(_fixture.WorkspaceA, id), body, Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await ReadAsync(response)).GetProperty("errors").TryGetProperty(field, out _));
        }

        Assert.Empty(await UsesAsync(id));
    }

    /// <summary>
    /// Whitespace in an optional field is absent, not a value — so a history never shows a campaign whose name is
    /// three blanks, and "has a campaign" stays one question.
    /// </summary>
    [Fact]
    public async Task Whitespace_in_an_optional_field_is_stored_as_absent()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        await LogAsync(client, id, new
        {
            platformKey = "  instagram  ",
            utilizedOn = "2026-04-01",
            campaignName = "   ",
            notes = "",
        });

        var row = Assert.Single(await UsesAsync(id));

        Assert.Equal("instagram", row.PlatformKey);
        Assert.Null(row.CampaignName);
        Assert.Null(row.Notes);
    }

    [Fact]
    public async Task A_request_wrong_in_two_ways_hears_about_both()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var response = await client.PostAsJsonAsync(
            UseIn(_fixture.WorkspaceA, id),
            new { platformKey = "", notes = new string('n', MediaPolicy.NotesMaxLength + 1) },
            Ct);
        var errors = (await ReadAsync(response)).GetProperty("errors");

        Assert.True(errors.TryGetProperty("platformKey", out _));
        Assert.True(errors.TryGetProperty("utilizedOn", out _));
        Assert.True(errors.TryGetProperty("notes", out _));
    }

    // ---- Replay ----

    /// <summary>
    /// With a key, a retry is replayed rather than recording a second use — which is what a client whose connection
    /// dropped needs.
    /// </summary>
    [Fact]
    public async Task A_retry_under_one_key_records_one_use()
    {
        var id = await SeededAsync();
        var key = Guid.NewGuid().ToString();
        var body = new { platformKey = "instagram", utilizedOn = "2026-04-01" };

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var first = await SendAsync(client, id, body, key);
        var second = await SendAsync(client, id, body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.True(second.Headers.Contains(IdempotencyPolicy.ReplayedHeader));

        Assert.Equal(
            (await ReadAsync(first)).GetProperty("id").GetGuid(),
            (await ReadAsync(second)).GetProperty("id").GetGuid());
        Assert.Single(await UsesAsync(id));
    }

    /// <summary>
    /// One key with a different log is refused rather than replayed as the first — the mistake the patch path had to
    /// be corrected for, so the fingerprint covers everything recorded about the use.
    /// </summary>
    [Fact]
    public async Task One_key_with_a_different_use_is_not_replayed_as_the_first()
    {
        var id = await SeededAsync();
        var key = Guid.NewGuid().ToString();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        await SendAsync(client, id, new { platformKey = "instagram", utilizedOn = "2026-04-01" }, key);

        var second = await SendAsync(client, id, new { platformKey = "blog", utilizedOn = "2026-04-01" }, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("instagram", Assert.Single(await UsesAsync(id)).PlatformKey);
    }

    /// <summary>
    /// <strong>Without a key, two identical calls record two uses</strong> — and that is the intended behaviour, not a
    /// gap. An asset genuinely can go out twice on one platform on one day, which is why the table imposes no
    /// uniqueness over (asset, platform, date).
    /// </summary>
    [Fact]
    public async Task Without_a_key_two_identical_calls_record_two_uses()
    {
        var id = await SeededAsync();
        var body = new { platformKey = "instagram", utilizedOn = "2026-04-01" };

        using var client = await SignInAsync(_fixture.WorkspaceA);
        await LogAsync(client, id, body);
        await LogAsync(client, id, body);

        var rows = await UsesAsync(id);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(row => row.Id).Distinct().Count());
    }

    // ---- Nothing to record against ----

    /// <summary>
    /// Three causes, one answer, and the restriction this prompt opens with: no utilization for a missing,
    /// soft-deleted or cross-workspace asset.
    /// </summary>
    [Fact]
    public async Task Every_reason_there_is_no_asset_is_the_same_not_found()
    {
        var deleted = await SeededAsync(deletedAt: Now);
        var inB = await SeededAsync(workspace: _fixture.WorkspaceB);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var codes = new List<string?>();

        foreach (var assetId in (Guid[])[Guid.NewGuid(), deleted, inB])
        {
            var response = await client.PostAsJsonAsync(
                UseIn(_fixture.WorkspaceA, assetId),
                new { platformKey = "instagram", utilizedOn = "2026-04-01" },
                Ct);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            codes.Add((await ReadAsync(response)).GetProperty("code").GetString());
        }

        Assert.Equal([MediaErrorCodes.AssetNotFound], codes.Distinct());
        Assert.Empty(await UsesAsync(deleted));
        Assert.Empty(await UsesAsync(inB));
    }

    /// <summary>
    /// Removing an asset stops it accepting uses, even though its earlier ones stay on the row. A creator cannot
    /// record new use of something they have taken out of the library.
    /// </summary>
    [Fact]
    public async Task Removing_an_asset_stops_it_accepting_uses_and_keeps_the_earlier_ones()
    {
        var id = await SeededAsync();

        using var client = await SignInAsync(_fixture.WorkspaceA);
        await LogAsync(client, id, new { platformKey = "instagram", utilizedOn = "2026-04-01" });
        await SoftDeleteAsync(id);

        var response = await client.PostAsJsonAsync(
            UseIn(_fixture.WorkspaceA, id),
            new { platformKey = "blog", utilizedOn = "2026-04-02" },
            Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // The one logged before the deletion is still there — 12.9e removes nothing.
        Assert.Single(await UsesAsync(id));
    }

    // ---- Role and isolation ----

    [Fact]
    public async Task A_viewer_cannot_record_a_use()
    {
        var id = await SeededAsync();
        await SetMemberRoleAsync(_fixture.WorkspaceA, WorkspaceRole.Viewer);

        using var member = await SignInAsync(_fixture.WorkspaceA, asOwner: false);
        var response = await member.PostAsJsonAsync(
            UseIn(_fixture.WorkspaceA, id),
            new { platformKey = "instagram", utilizedOn = "2026-04-01" },
            Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await UsesAsync(id));
    }

    [Fact]
    public async Task A_contributor_may_record_a_use()
    {
        var id = await SeededAsync();
        await SetMemberRoleAsync(_fixture.WorkspaceA, WorkspaceRole.Contributor);

        using var member = await SignInAsync(_fixture.WorkspaceA, asOwner: false);
        var response = await member.PostAsJsonAsync(
            UseIn(_fixture.WorkspaceA, id),
            new { platformKey = "instagram", utilizedOn = "2026-04-01" },
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(await UsesAsync(id));
    }

    /// <summary>
    /// A's asset from B's route and from A's slug by B's owner: 404 both times, and nothing written. The
    /// two-workspace coverage tenancy.md requires of a write path.
    /// </summary>
    [Fact]
    public async Task A_member_of_one_workspace_cannot_record_against_the_others_asset()
    {
        var inA = await SeededAsync();

        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        foreach (var slug in (SeededWorkspace[])[_fixture.WorkspaceB, _fixture.WorkspaceA])
        {
            var response = await ownerB.PostAsJsonAsync(
                UseIn(slug, inA),
                new { platformKey = "instagram", utilizedOn = "2026-04-01" },
                Ct);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Empty(await UsesAsync(inA));
    }

    /// <summary>
    /// Both workspaces log against their own assets, and neither history shows the other's. The direction a one-sided
    /// isolation test misses.
    /// </summary>
    [Fact]
    public async Task Each_workspace_records_only_against_its_own_assets()
    {
        var inA = await SeededAsync();
        var inB = await SeededAsync(workspace: _fixture.WorkspaceB);

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        await LogAsync(ownerA, inA, new { platformKey = "a-platform", utilizedOn = "2026-04-01" });

        var inBResponse = await ownerB.PostAsJsonAsync(
            UseIn(_fixture.WorkspaceB, inB),
            new { platformKey = "b-platform", utilizedOn = "2026-04-02" },
            Ct);

        Assert.Equal(HttpStatusCode.Created, inBResponse.StatusCode);

        Assert.Equal("a-platform", Assert.Single(await UsesAsync(inA)).PlatformKey);
        Assert.Equal("b-platform", Assert.Single(await UsesAsync(inB)).PlatformKey);
    }

    // ---- Helpers ----

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace, bool asOwner = true) =>
        _fixture.SignInAsync(
            asOwner ? workspace.OwnerEmail : workspace.MemberEmail, cancellationToken: Ct);

    private AsyncServiceScope ScopeFor(SeededWorkspace workspace)
    {
        var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "seed");

        return scope;
    }

    private Task<HttpResponseMessage> SendAsync(
        GatewayClient client, Guid assetId, object body, string idempotencyKey) =>
        client.SendAsync(
            HttpMethod.Post,
            UseIn(_fixture.WorkspaceA, assetId),
            body,
            new Dictionary<string, string> { [IdempotencyPolicy.KeyHeader] = idempotencyKey },
            Ct);

    /// <summary>Logs a use and insists it succeeded, so a test about what was recorded cannot pass on a refusal.</summary>
    private async Task<JsonElement> LogAsync(GatewayClient client, Guid assetId, object body)
    {
        var workspace = _fixture.WorkspaceA;
        var response = await client.PostAsJsonAsync(UseIn(workspace, assetId), body, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await ReadAsync(response);
    }

    private async Task<Guid> SeededAsync(SeededWorkspace? workspace = null, DateTimeOffset? deletedAt = null)
    {
        var target = workspace ?? _fixture.WorkspaceA;

        await using var scope = ScopeFor(target);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var actor = Guid.NewGuid();

        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            Title = "Soda bread hero",
            Kind = MediaAssetKind.Original,
            CurrentVersionNumber = 1,
            DeletedAt = deletedAt,
            DeletedByMembershipId = deletedAt is null ? null : actor,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
        };

        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync(Ct);

        return asset.Id;
    }

    private async Task<List<MediaAssetUtilization>> UsesAsync(Guid assetId)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .MediaAssetUtilizations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(use => use.MediaAssetId == assetId)
            .ToListAsync(Ct);
    }

    private async Task SoftDeleteAsync(Guid assetId)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var asset = await db.MediaAssets.SingleAsync(candidate => candidate.Id == assetId, Ct);
        asset.DeletedAt = Now;
        asset.DeletedByMembershipId = Guid.NewGuid();

        await db.SaveChangesAsync(Ct);
    }

    private async Task SetMemberRoleAsync(SeededWorkspace workspace, WorkspaceRole role)
    {
        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var memberships = await db.WorkspaceMemberships
            .IgnoreQueryFilters()
            .Where(candidate => candidate.WorkspaceId == workspace.Id)
            .ToListAsync(Ct);

        foreach (var candidate in memberships.Where(candidate => candidate.Role != WorkspaceRole.Owner))
        {
            candidate.Role = role;
        }

        await db.SaveChangesAsync(Ct);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
}
