using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Gateways;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Nutrition.Data.Entities;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Nutrition;

/// <summary>
/// <c>GET .../nutrition-mappings/{ingredientId}</c> and its <c>confirm</c>, <c>reject</c> and <c>remap</c> commands
/// through the real Gateway (14.6b): each decision, the concurrency token every one quotes, who may make them, and
/// what another workspace can see. The nutrition source is scripted.
/// </summary>
public sealed class NutritionMappingEndpointTests : IAsyncLifetime
{
    private const string ContributorEmail = "mapping-contributor-a@example.com";
    private const string Password = "correct horse battery";
    private const string FlourRecord = "169761";
    private const string OtherFlourRecord = "168936";
    private const string FdcVersion = "sr-legacy/2019-04-01";
    private const string NewerVersion = "foundation/2024-04-18";

    private readonly ScriptedEvidence _evidence = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;
    private Reference _reference = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services => services.AddSingleton<IReferenceEvidenceGateway>(_evidence));
        _reference = await SeedReferenceAsync();

        _evidence.Has(FlourRecord, "Wheat flour, white, all-purpose", FdcVersion, [new PortionEvidence(1m, 125m, _reference.CupCode, null, "1 cup")], []);
        _evidence.Has(OtherFlourRecord, "Wheat flour, whole-grain", FdcVersion, [], []);

        var userId = await _fixture.Api.CreateUserAsync(ContributorEmail, Password);
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(), WorkspaceId = _fixture.WorkspaceA.Id, UserId = userId, Role = WorkspaceRole.Contributor,
            Status = WorkspaceMembershipStatus.Active, JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Reading ----

    [Fact]
    public async Task A_member_reads_the_mapping_with_the_token_a_decision_quotes()
    {
        await SeedMappingAsync(_fixture.WorkspaceB.Id, _reference.Flour, NutritionMappingStatus.Candidate);
        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: Ct);

        var response = await viewer.GetAsync(MappingRoute(_fixture.WorkspaceB, _reference.Flour), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, "a cached mapping would hand out a stale token");
        var body = await BodyOf(response);
        Assert.Equal(("Candidate", FlourRecord), (body.GetProperty("status").GetString(), body.GetProperty("reference").GetProperty("externalRecordId").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("concurrencyToken").GetString()));
    }

    [Fact]
    public async Task No_mapping_and_another_workspaces_mapping_are_both_not_found()
    {
        await SeedMappingAsync(_fixture.WorkspaceB.Id, _reference.Flour, NutritionMappingStatus.Confirmed);
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        foreach (var ingredient in new[] { _reference.Flour, _reference.Butter })
        {
            var response = await ownerA.GetAsync(MappingRoute(_fixture.WorkspaceA, ingredient), Ct);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("nutrition.mapping.not_found", (await BodyOf(response)).GetProperty("code").GetString());
        }
    }

    // ---- Confirm ----

    [Fact]
    public async Task Confirming_a_candidate_confirms_exactly_that_mapping_and_is_audited()
    {
        await SeedMappingAsync(_fixture.WorkspaceA.Id, _reference.Flour, NutritionMappingStatus.Candidate);
        await SeedMappingAsync(_fixture.WorkspaceA.Id, _reference.Butter, NutritionMappingStatus.Candidate, OtherFlourRecord);
        using var editor = await _fixture.SignInAsync(_fixture.WorkspaceA.MemberEmail, cancellationToken: Ct);
        var token = await TokenAsync(editor, _fixture.WorkspaceA, _reference.Flour);

        var response = await editor.PostAsJsonAsync(CommandRoute(_fixture.WorkspaceA, _reference.Flour, "confirm"), new { expectedConcurrencyToken = token }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal("Confirmed", body.GetProperty("status").GetString());

        // That the token moves is not assertable here — the SQLite host fills RowVersion on insert and never bumps
        // it — and is covered against SQL Server in NutritionMappingTests.
        Assert.False(string.IsNullOrEmpty(body.GetProperty("concurrencyToken").GetString()));

        // One decision, one mapping: the other candidate is untouched.
        Assert.Equal("Candidate", (await BodyOf(await editor.GetAsync(MappingRoute(_fixture.WorkspaceA, _reference.Butter), Ct))).GetProperty("status").GetString());
        Assert.Equal(1, await AuditCountAsync(NutritionAuditActions.Confirmed));
    }

    [Fact]
    public async Task Confirming_a_newer_version_under_review_accepts_that_version()
    {
        await SeedMappingAsync(_fixture.WorkspaceA.Id, _reference.Flour, NutritionMappingStatus.NeedsReview);
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var body = await BodyOf(await owner.PostAsJsonAsync(
            CommandRoute(_fixture.WorkspaceA, _reference.Flour, "confirm"), new { expectedConcurrencyToken = await TokenAsync(owner, _fixture.WorkspaceA, _reference.Flour) }, Ct));

        Assert.Equal(("Confirmed", NewerVersion), (body.GetProperty("status").GetString(), body.GetProperty("reference").GetProperty("sourceVersion").GetString()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("reviewSourceVersion").ValueKind);
    }

    [Theory]
    [InlineData(1, 125, "Reference")]
    [InlineData(1, 120, "Creator")]
    public async Task A_portion_is_the_sources_only_when_it_is_one_the_source_measured(int cups, int grams, string origin)
    {
        await SeedMappingAsync(_fixture.WorkspaceA.Id, _reference.Flour, NutritionMappingStatus.Candidate);
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var body = await BodyOf(await owner.PostAsJsonAsync(
            CommandRoute(_fixture.WorkspaceA, _reference.Flour, "confirm"),
            new
            {
                expectedConcurrencyToken = await TokenAsync(owner, _fixture.WorkspaceA, _reference.Flour),
                portion = new { measurementUnitId = _reference.Cup, amount = cups, grams },
            },
            Ct));

        Assert.Equal(origin, body.GetProperty("portion").GetProperty("origin").GetString());
    }

    [Fact]
    public async Task A_portion_in_a_mass_unit_is_unprocessable()
    {
        await SeedMappingAsync(_fixture.WorkspaceA.Id, _reference.Flour, NutritionMappingStatus.Candidate);
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await owner.PostAsJsonAsync(
            CommandRoute(_fixture.WorkspaceA, _reference.Flour, "confirm"),
            new
            {
                expectedConcurrencyToken = await TokenAsync(owner, _fixture.WorkspaceA, _reference.Flour),
                portion = new { measurementUnitId = _reference.Gram, amount = 100, grams = 100 },
            },
            Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("nutrition.mapping.portion.unprocessable", (await BodyOf(response)).GetProperty("code").GetString());
    }

    // ---- Reject ----

    [Fact]
    public async Task Rejecting_leaves_the_mapping_unresolved_and_remembers_the_reference()
    {
        await SeedMappingAsync(_fixture.WorkspaceA.Id, _reference.Flour, NutritionMappingStatus.Candidate);
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await owner.PostAsJsonAsync(
            CommandRoute(_fixture.WorkspaceA, _reference.Flour, "reject"), new { expectedConcurrencyToken = await TokenAsync(owner, _fixture.WorkspaceA, _reference.Flour) }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(("Unresolved", "ReferenceRejected", 1), (body.GetProperty("status").GetString(), body.GetProperty("unresolvedReason").GetString(), body.GetProperty("rejectedReferenceCount").GetInt32()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("reference").ValueKind);
        Assert.Equal(1, await AuditCountAsync(NutritionAuditActions.Rejected));
    }

    [Fact]
    public async Task Rejecting_without_a_token_is_a_bad_request_naming_it()
    {
        await SeedMappingAsync(_fixture.WorkspaceA.Id, _reference.Flour, NutritionMappingStatus.Candidate);
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await owner.PostAsJsonAsync(CommandRoute(_fixture.WorkspaceA, _reference.Flour, "reject"), new { }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal("nutrition.mapping.invalid", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("expectedConcurrencyToken", out _));
    }

    // ---- Remap ----

    [Fact]
    public async Task Remapping_an_unmapped_ingredient_makes_the_first_mapping_without_a_token()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await owner.PostAsJsonAsync(
            CommandRoute(_fixture.WorkspaceA, _reference.Flour, "remap"), new { sourceCode = "usda-fdc", externalRecordId = FlourRecord }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(("Confirmed", "CreatorSelected"), (body.GetProperty("status").GetString(), body.GetProperty("matchMethod").GetString()));
        Assert.Equal(1, await AuditCountAsync(NutritionAuditActions.Remapped));
    }

    [Fact]
    public async Task Remapping_a_confirmed_mapping_takes_its_token_and_withdraws_a_rejection_of_the_new_reference()
    {
        await SeedMappingAsync(_fixture.WorkspaceA.Id, _reference.Flour, NutritionMappingStatus.Candidate, OtherFlourRecord);
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        // Turn the whole-grain record down, then choose it after all.
        var rejected = await BodyOf(await owner.PostAsJsonAsync(
            CommandRoute(_fixture.WorkspaceA, _reference.Flour, "reject"), new { expectedConcurrencyToken = await TokenAsync(owner, _fixture.WorkspaceA, _reference.Flour) }, Ct));

        var withoutToken = await owner.PostAsJsonAsync(
            CommandRoute(_fixture.WorkspaceA, _reference.Flour, "remap"), new { sourceCode = "usda-fdc", externalRecordId = OtherFlourRecord }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, withoutToken.StatusCode);
        Assert.Equal("nutrition.mapping.conflict", (await BodyOf(withoutToken)).GetProperty("code").GetString());

        var remapped = await BodyOf(await owner.PostAsJsonAsync(
            CommandRoute(_fixture.WorkspaceA, _reference.Flour, "remap"),
            new { expectedConcurrencyToken = rejected.GetProperty("concurrencyToken").GetString(), sourceCode = "usda-fdc", externalRecordId = OtherFlourRecord },
            Ct));

        Assert.Equal(("Confirmed", OtherFlourRecord, 0), (
            remapped.GetProperty("status").GetString(),
            remapped.GetProperty("reference").GetProperty("externalRecordId").GetString(),
            remapped.GetProperty("rejectedReferenceCount").GetInt32()));
    }

    [Theory]
    [InlineData("usda-fdc", "999999")]
    [InlineData("open-food-facts", FlourRecord)]
    public async Task Remapping_to_a_reference_that_is_not_approved_evidence_is_unprocessable(string sourceCode, string recordId)
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await owner.PostAsJsonAsync(
            CommandRoute(_fixture.WorkspaceA, _reference.Flour, "remap"), new { sourceCode, externalRecordId = recordId }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("nutrition.mapping.reference.unprocessable", (await BodyOf(response)).GetProperty("code").GetString());
    }

    // ---- Conflict ----

    [Theory]
    [InlineData("confirm")]
    [InlineData("reject")]
    public async Task A_decision_on_a_token_the_mapping_no_longer_has_is_a_conflict_and_writes_nothing(string command)
    {
        await SeedMappingAsync(_fixture.WorkspaceA.Id, _reference.Flour, NutritionMappingStatus.Candidate);
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        // A well-formed token from some earlier read. A real token going stale after another decision needs a store
        // that bumps row versions on update, which the SQLite host does not; NutritionMappingTests covers that on
        // SQL Server. This proves what the route answers when the token does not match.
        var stale = Convert.ToBase64String(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 });

        var late = await owner.PostAsJsonAsync(CommandRoute(_fixture.WorkspaceA, _reference.Flour, command), new { expectedConcurrencyToken = stale }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("nutrition.mapping.conflict", (await BodyOf(late)).GetProperty("code").GetString());
        Assert.Equal("Candidate", (await BodyOf(await owner.GetAsync(MappingRoute(_fixture.WorkspaceA, _reference.Flour), Ct))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Confirming_a_mapping_with_nothing_to_confirm_is_a_state_conflict()
    {
        await SeedMappingAsync(_fixture.WorkspaceA.Id, _reference.Flour, NutritionMappingStatus.Confirmed);
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await owner.PostAsJsonAsync(
            CommandRoute(_fixture.WorkspaceA, _reference.Flour, "confirm"), new { expectedConcurrencyToken = await TokenAsync(owner, _fixture.WorkspaceA, _reference.Flour) }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("nutrition.mapping.state.conflict", (await BodyOf(response)).GetProperty("code").GetString());
    }

    // ---- Who may decide, and what another workspace sees ----

    [Fact]
    public async Task Below_editor_a_member_may_read_but_not_decide()
    {
        await SeedMappingAsync(_fixture.WorkspaceA.Id, _reference.Flour, NutritionMappingStatus.Candidate);
        await SeedMappingAsync(_fixture.WorkspaceB.Id, _reference.Flour, NutritionMappingStatus.Candidate);

        using var contributor = await _fixture.SignInAsync(ContributorEmail, Password, Ct);
        var token = await TokenAsync(contributor, _fixture.WorkspaceA, _reference.Flour);
        Assert.Equal(HttpStatusCode.Forbidden, (await contributor.PostAsJsonAsync(CommandRoute(_fixture.WorkspaceA, _reference.Flour, "confirm"), new { expectedConcurrencyToken = token }, Ct)).StatusCode);

        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: Ct);
        var viewerToken = await TokenAsync(viewer, _fixture.WorkspaceB, _reference.Flour);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(CommandRoute(_fixture.WorkspaceB, _reference.Flour, "reject"), new { expectedConcurrencyToken = viewerToken }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Another_workspaces_mapping_cannot_be_decided_and_a_body_workspace_id_is_ignored()
    {
        await SeedMappingAsync(_fixture.WorkspaceB.Id, _reference.Flour, NutritionMappingStatus.Candidate);
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        var tokenB = await TokenAsync(ownerB, _fixture.WorkspaceB, _reference.Flour);

        // A's owner, quoting B's token, with B's id in the body: A has no mapping, and B's is untouched.
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var response = await ownerA.PostAsJsonAsync(
            CommandRoute(_fixture.WorkspaceA, _reference.Flour, "confirm"), new { expectedConcurrencyToken = tokenB, workspaceId = _fixture.WorkspaceB.Id }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("nutrition.mapping.not_found", (await BodyOf(response)).GetProperty("code").GetString());

        // A's owner naming B's workspace: not a member.
        Assert.Equal(HttpStatusCode.NotFound, (await ownerA.PostAsJsonAsync(CommandRoute(_fixture.WorkspaceB, _reference.Flour, "confirm"), new { expectedConcurrencyToken = tokenB }, Ct)).StatusCode);

        Assert.Equal("Candidate", (await BodyOf(await ownerB.GetAsync(MappingRoute(_fixture.WorkspaceB, _reference.Flour), Ct))).GetProperty("status").GetString());
    }

    // ---- What a decision never touches, and what it cannot do without the source ----

    [Fact]
    public async Task A_decision_never_rewrites_a_recipe_line()
    {
        await SeedMappingAsync(_fixture.WorkspaceA.Id, _reference.Flour, NutritionMappingStatus.Candidate);
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var created = await owner.PostAsJsonAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipes",
            new { title = "Shortbread", ingredientGroups = new[] { new { ingredients = new[] { new { displayText = "2 cups plain flour, sifted", ingredientId = _reference.Flour } } } } },
            Ct);
        var location = created.Headers.Location!.ToString();
        var before = await BodyOf(await owner.GetAsync(location, Ct));

        await owner.PostAsJsonAsync(CommandRoute(_fixture.WorkspaceA, _reference.Flour, "confirm"), new { expectedConcurrencyToken = await TokenAsync(owner, _fixture.WorkspaceA, _reference.Flour) }, Ct);
        var after = await BodyOf(await owner.GetAsync(location, Ct));

        Assert.Equal(before.GetProperty("ingredientGroups").GetRawText(), after.GetProperty("ingredientGroups").GetRawText());
        Assert.Equal(before.GetProperty("concurrencyToken").GetString(), after.GetProperty("concurrencyToken").GetString());
    }

    [Fact]
    public async Task A_source_that_cannot_be_asked_is_unavailable_and_nothing_is_written()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        _evidence.Down = true;

        var response = await owner.PostAsJsonAsync(
            CommandRoute(_fixture.WorkspaceA, _reference.Flour, "remap"), new { sourceCode = "usda-fdc", externalRecordId = FlourRecord }, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("nutrition.mapping.source.unavailable", (await BodyOf(response)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(MappingRoute(_fixture.WorkspaceA, _reference.Flour), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_missing_body_is_malformed_as_on_every_route()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await owner.PostAsync(
            CommandRoute(_fixture.WorkspaceA, _reference.Flour, "confirm"), new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json"), cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("request.malformed", (await BodyOf(response)).GetProperty("code").GetString());
    }

    // ---- Helpers ----

    private sealed record Reference(Guid Cup, string CupCode, Guid Gram, Guid Flour, Guid Butter, Guid FdcSource);

    private static string MappingRoute(SeededWorkspace workspace, Guid ingredientId) =>
        $"/api/v1/workspaces/{workspace.Slug}/nutrition-mappings/{ingredientId}";

    private static string CommandRoute(SeededWorkspace workspace, Guid ingredientId, string command) =>
        $"{MappingRoute(workspace, ingredientId)}/{command}";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<string> TokenAsync(GatewayClient client, SeededWorkspace workspace, Guid ingredientId)
    {
        var response = await client.GetAsync(MappingRoute(workspace, ingredientId), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await BodyOf(response)).GetProperty("concurrencyToken").GetString()!;
    }

    private async Task<int> AuditCountAsync(string action)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.AuditLogs.IgnoreQueryFilters().CountAsync(entry => entry.Action == action, Ct);
    }

    /// <summary>A mapping as a proposal, a confirmation or a refresh would have left it.</summary>
    private async Task SeedMappingAsync(Guid workspaceId, Guid ingredientId, NutritionMappingStatus status, string recordId = FlourRecord)
    {
        var at = DateTimeOffset.UtcNow;
        var decided = status is NutritionMappingStatus.Confirmed or NutritionMappingStatus.NeedsReview;

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.NutritionMappings.Add(new NutritionMapping
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            IngredientId = ingredientId,
            Status = status,
            MatchMethod = status is NutritionMappingStatus.Candidate ? NutritionMatchMethod.AutomaticSearch : NutritionMatchMethod.CreatorSelected,
            Confidence = status is NutritionMappingStatus.Candidate ? NutritionMatchConfidence.High : null,
            ReferenceSourceId = _reference.FdcSource,
            SourceCode = EvidenceSourceCatalog.UsdaFoodDataCentral.Code,
            ExternalRecordId = recordId,
            ReferenceDescription = "A recorded FDC food",
            SourceVersion = FdcVersion,
            ReviewSourceVersion = status is NutritionMappingStatus.NeedsReview ? NewerVersion : null,
            ConfirmedAt = decided ? at : null,
            ConfirmedByMembershipId = decided ? Guid.NewGuid() : null,
            CreatedAt = at,
            CreatedByMembershipId = Guid.NewGuid(),
            UpdatedAt = at,
            UpdatedByMembershipId = Guid.NewGuid(),
        });

        await db.SaveChangesAsync(Ct);
    }

    private async Task<Reference> SeedReferenceAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var fdc = EvidenceSourceCatalog.UsdaFoodDataCentral;
        var fdcSource = await db.ReferenceSources.Where(source => source.Code == fdc.Code).Select(source => (Guid?)source.Id).SingleOrDefaultAsync(Ct);
        if (fdcSource is null)
        {
            fdcSource = Guid.NewGuid();
            db.ReferenceSources.Add(new ReferenceSource
            {
                Id = fdcSource.Value, Code = fdc.Code, Name = fdc.Name, Kind = fdc.Kind, Url = fdc.Url, Citation = fdc.Citation,
                LicenseCode = fdc.LicenseCode, LicenseUrl = fdc.LicenseUrl,
            });
        }

        var reference = new Reference(Guid.NewGuid(), $"cup-{suffix}", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), fdcSource.Value);

        db.MeasurementUnits.AddRange(
            new MeasurementUnit
            {
                Id = reference.Cup, Code = reference.CupCode, DisplayName = "cup", PluralName = "cups", Abbreviation = "c",
                Dimension = MeasurementDimension.Volume, System = MeasurementSystem.UsCustomary, BaseUnitFactor = 236.5882365m, DisplayPrecision = 2,
            },
            new MeasurementUnit
            {
                Id = reference.Gram, Code = $"g-{suffix}", DisplayName = "gram", PluralName = "grams", Abbreviation = "g",
                Dimension = MeasurementDimension.Mass, System = MeasurementSystem.Metric, BaseUnitFactor = 1m, DisplayPrecision = 0,
            });

        foreach (var (id, name) in new[] { (reference.Flour, "flour"), (reference.Butter, "butter") })
        {
            db.Ingredients.Add(new Ingredient { Id = id, CanonicalName = $"{name} {suffix}", NormalizedName = $"{name} {suffix}", SearchText = $"{name} {suffix}" });
        }

        await db.SaveChangesAsync(Ct);

        return reference;
    }
}
