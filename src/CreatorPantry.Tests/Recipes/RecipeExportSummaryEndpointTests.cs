extern alias ApiService;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApiService::CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Modules.Recipes.Business;
using UglyToad.PdfPig;
using ApiService::CreatorPantry.ApiService.Controllers;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/exports/summary</c> through the real Gateway —
/// real cookie session, real gateway-signed internal token, real API — for what a reader receives, the download
/// headers, the error contract, and what another workspace can never receive.
/// </summary>
public sealed class RecipeExportSummaryEndpointTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TwoWorkspaceGatewayFixture _fixture = null!;
    private Guid _ounceId;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

        // The measurement catalogue is global reference data the API host does not seed by itself.
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        await new ReferenceDataSeeder(
                db, new ReferenceSeedOptions(IncludeDevelopmentSampleData: false), NullLogger<ReferenceDataSeeder>.Instance)
            .SeedAsync(Ct);
        _ounceId = (await db.MeasurementUnits.AsNoTracking().SingleAsync(unit => unit.Code == "oz", Ct)).Id;
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string ExportOf(SeededWorkspace workspace, Guid recipeId, string query = "") =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}/exports/summary{query}";

    // ---- Seeding ----

    private sealed record SeededRecipe(Guid RecipeId, Guid VersionId);

    private const string Editorial =
        """{"schemaVersion":"content.editorial-package.v1","sections":{"introduction":{"text":"INTRO"},"cta":{"text":"Tag us!"}}}""";

    private async Task<SeededRecipe> SeedAsync(
        SeededWorkspace workspace,
        string title = "Soda Bread",
        RecipeStatus status = RecipeStatus.Approved,
        bool ingredients = true,
        string? editorialIntro = null,
        bool secondVersion = false,
        RecipeVersionReadiness readiness = RecipeVersionReadiness.Draft,
        bool acceptedSeo = false,
        bool editorialNeedsReview = false)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var author = Guid.NewGuid();
        var recipe = new Recipe
        {
            Id = Guid.NewGuid(), Title = title, Status = status, CreatedByMembershipId = author,
            UpdatedByMembershipId = author, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Recipes.Add(recipe);

        var snapshot = new RecipeSnapshotDocument
        {
            SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
            Recipe = new RecipeSnapshotHeader { Title = title, Status = status, Description = "A dense loaf." },
            IngredientGroups = ingredients
                ? [new RecipeSnapshotIngredientGroup
                    {
                        Id = Guid.NewGuid(),
                        Ingredients =
                        [
                            new RecipeSnapshotIngredient
                            {
                                Id = Guid.NewGuid(), SortOrder = 0, DisplayText = "4 oz butter", Quantity = 4m,
                                MeasurementUnitId = _ounceId, MeasurementUnitDimension = MeasurementDimension.Mass,
                            },
                            new RecipeSnapshotIngredient { Id = Guid.NewGuid(), SortOrder = 1, DisplayText = "salt to taste" },
                        ],
                    }]
                : [],
            InstructionGroups =
            [
                new RecipeSnapshotInstructionGroup
                {
                    Id = Guid.NewGuid(),
                    Steps = [new RecipeSnapshotInstructionStep { Id = Guid.NewGuid(), Text = "Bake." }],
                },
            ],
        };

        RecipeVersion NewVersion(int number) => new()
        {
            Id = Guid.NewGuid(), RecipeId = recipe.Id, VersionNumber = number, Source = RecipeVersionSource.CreatorEdit,
            Readiness = readiness, CreatedByMembershipId = author, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(number),
            SnapshotSchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
        };

        var versions = new List<RecipeVersion> { NewVersion(1) };
        if (secondVersion) versions.Add(NewVersion(2));
        foreach (var version in versions)
        {
            version.Snapshot = new RecipeVersionSnapshot
            {
                RecipeVersionId = version.Id,
                Document = RecipeSnapshotSerializer.Serialize(snapshot),
            };
            db.RecipeVersions.Add(version);
        }

        if (editorialIntro is not null)
        {
            var proposal = new ContentProposal
            {
                Id = Guid.NewGuid(), RecipeId = recipe.Id, Kind = ContentPackageKind.Editorial,
                Status = ContentProposalStatus.Proposed, CreatedByMembershipId = author,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            var revision = new ContentRevision
            {
                Id = Guid.NewGuid(), ContentProposalId = proposal.Id, RecipeId = recipe.Id, RevisionNumber = 1,
                Source = ContentRevisionSource.CreatorEdit, RecipeVersionId = versions[0].Id, SchemaVersion = 1,
                Content = Editorial.Replace("INTRO", editorialIntro), CreatedByMembershipId = author,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ContentProposals.Add(proposal);
            db.ContentRevisions.Add(revision);
            await db.SaveChangesAsync(Ct);

            proposal.Status = ContentProposalStatus.Accepted;
            proposal.AcceptedRevisionId = revision.Id;
        }

        if (acceptedSeo)
        {
            var seoProposal = new ContentProposal
            {
                Id = Guid.NewGuid(), RecipeId = recipe.Id, Kind = ContentPackageKind.Seo,
                Status = ContentProposalStatus.Proposed, CreatedByMembershipId = author,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            var seoRevision = new ContentRevision
            {
                Id = Guid.NewGuid(), ContentProposalId = seoProposal.Id, RecipeId = recipe.Id, RevisionNumber = 1,
                Source = ContentRevisionSource.CreatorEdit, RecipeVersionId = versions[0].Id, SchemaVersion = 1,
                Content = """{"schemaVersion":"content.seo-package.v1","sections":{"metaDescription":{"text":"A loaf."}}}""",
                CreatedByMembershipId = author, CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ContentProposals.Add(seoProposal);
            db.ContentRevisions.Add(seoRevision);
            await db.SaveChangesAsync(Ct);

            seoProposal.Status = ContentProposalStatus.Accepted;
            seoProposal.AcceptedRevisionId = seoRevision.Id;
        }

        if (editorialNeedsReview)
        {
            var editorial = await db.ContentProposals.SingleAsync(p => p.RecipeId == recipe.Id && p.Kind == ContentPackageKind.Editorial, Ct);
            editorial.Status = ContentProposalStatus.NeedsReview;
            editorial.StaleSince = DateTimeOffset.UtcNow;
            editorial.StaleReasons = ContentStaleReasons.BrandChanged;
        }

        await db.SaveChangesAsync(Ct);
        return new SeededRecipe(recipe.Id, versions[0].Id);
    }

    /// <summary>The header exactly as sent, before the client's parser normalises it. Exactly one is sent.</summary>
    private static string RawDisposition(HttpResponseMessage response) =>
        response.Content.Headers.NonValidated["Content-Disposition"].Single();

    private async Task<(HttpResponseMessage Response, JsonElement Body)> GetAsync(
        SeededWorkspace inWorkspace, SeededWorkspace viaSlug, Guid recipeId, string? email = null)
    {
        using var client = await _fixture.SignInAsync(email ?? inWorkspace.OwnerEmail, cancellationToken: Ct);
        var response = await client.GetAsync(ExportOf(viaSlug, recipeId), Ct);
        return (response, await response.Content.ReadFromJsonAsync<JsonElement>(Ct));
    }

    private Task<(HttpResponseMessage Response, JsonElement Body)> OwnA(Guid recipeId) =>
        GetAsync(_fixture.WorkspaceA, _fixture.WorkspaceA, recipeId);

    // ---- What a reader receives ----

    [Fact]
    public async Task An_approved_recipe_with_accepted_copy_reports_both_revisions_as_current()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "An intro.", acceptedSeo: true);

        var (response, body) = await OwnA(seeded.RecipeId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-cache", response.Headers.CacheControl!.ToString());
        Assert.Contains("private", response.Headers.CacheControl.ToString());
        Assert.Equal("Cookie", string.Join(",", response.Headers.Vary));
        Assert.Equal(1, body.GetProperty("versionNumber").GetInt32());
        Assert.True(body.GetProperty("exportable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("notExportableReason").ValueKind);
        Assert.Equal(1, body.GetProperty("editorial").GetProperty("revisionNumber").GetInt32());
        Assert.True(body.GetProperty("editorial").GetProperty("isCurrent").GetBoolean());
        Assert.Equal(1, body.GetProperty("seo").GetProperty("revisionNumber").GetInt32());
        Assert.True(body.GetProperty("seo").GetProperty("isCurrent").GetBoolean());
    }

    [Fact]
    public async Task Nothing_accepted_is_null_not_absent()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);

        var (_, body) = await OwnA(seeded.RecipeId);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("editorial").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("seo").ValueKind);
    }

    [Fact]
    public async Task Copy_accepted_for_an_older_version_is_not_current()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "An intro.", acceptedSeo: true, secondVersion: true);

        var (_, body) = await OwnA(seeded.RecipeId);

        Assert.Equal(2, body.GetProperty("versionNumber").GetInt32());
        Assert.False(body.GetProperty("editorial").GetProperty("isCurrent").GetBoolean());
        Assert.False(body.GetProperty("seo").GetProperty("isCurrent").GetBoolean());
    }

    [Fact]
    public async Task Copy_marked_for_review_is_not_current_even_on_the_version_it_was_accepted_for()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "An intro.", acceptedSeo: true, editorialNeedsReview: true);

        var (_, body) = await OwnA(seeded.RecipeId);

        Assert.False(body.GetProperty("editorial").GetProperty("isCurrent").GetBoolean());
        Assert.True(body.GetProperty("seo").GetProperty("isCurrent").GetBoolean());
    }

    [Fact]
    public async Task A_version_that_is_not_approved_is_reported_not_refused()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, status: RecipeStatus.Draft);

        var (response, body) = await OwnA(seeded.RecipeId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body.GetProperty("exportable").GetBoolean());
        Assert.Equal("recipe_export_not_approved", body.GetProperty("notExportableReason").GetString());
    }

    [Fact]
    public async Task A_version_marked_ready_is_exportable()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, status: RecipeStatus.Draft, readiness: RecipeVersionReadiness.Ready);

        var (_, body) = await OwnA(seeded.RecipeId);

        Assert.True(body.GetProperty("exportable").GetBoolean());
    }

    [Fact]
    public async Task The_summary_carries_no_copy_text_path_or_address()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "SECRET intro.", acceptedSeo: true);

        var (_, body) = await OwnA(seeded.RecipeId);
        var text = body.GetRawText();

        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("A loaf.", text);
        Assert.DoesNotContain("http", text);
        Assert.DoesNotContain("/", text);
        Assert.DoesNotContain(_fixture.WorkspaceA.Slug, text);
        Assert.DoesNotContain(seeded.RecipeId.ToString(), text);
    }

    // ---- Roles ----

    [Fact]
    public async Task A_viewer_may_read_the_summary()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceB);

        var (response, _) = await GetAsync(
            _fixture.WorkspaceB, _fixture.WorkspaceB, seeded.RecipeId, email: _fixture.WorkspaceB.MemberEmail);

        Assert.Equal(WorkspaceRole.Viewer, _fixture.WorkspaceB.MemberRole);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);

        using var http = _fixture.Gateway.CreateClient();
        var response = await http.GetAsync(ExportOf(_fixture.WorkspaceA, seeded.RecipeId), Ct);

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect or HttpStatusCode.Found);
    }

    // ---- Isolation ----

    [Fact]
    public async Task A_foreign_recipe_is_indistinguishable_from_an_unknown_one()
    {
        var a = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "A's words.", acceptedSeo: true);

        var foreign = await GetAsync(_fixture.WorkspaceB, _fixture.WorkspaceB, a.RecipeId);
        var unknown = await GetAsync(_fixture.WorkspaceB, _fixture.WorkspaceB, Guid.NewGuid());
        var throughA = await GetAsync(_fixture.WorkspaceB, _fixture.WorkspaceA, a.RecipeId);

        Assert.Equal(HttpStatusCode.NotFound, foreign.Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, throughA.Response.StatusCode);
        Assert.Equal("recipes.recipe.not_found", foreign.Body.GetProperty("code").GetString());
        Assert.Equal(Shape(unknown.Body), Shape(foreign.Body));
        Assert.DoesNotContain("A's words.", foreign.Body.GetRawText());
    }

    private static string Shape(JsonElement problem) =>
        string.Join("|", problem.EnumerateObject()
            .Where(property => property.Name != "traceId")
            .Select(property => $"{property.Name}={property.Value.GetRawText()}"));

    [Fact]
    public async Task Each_workspace_sees_only_its_own_accepted_copy()
    {
        var a = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "ALPHA.", acceptedSeo: true);
        var b = await SeedAsync(_fixture.WorkspaceB);

        var (_, bodyA) = await OwnA(a.RecipeId);
        var (_, bodyB) = await GetAsync(_fixture.WorkspaceB, _fixture.WorkspaceB, b.RecipeId);

        Assert.Equal(JsonValueKind.Object, bodyA.GetProperty("editorial").ValueKind);
        Assert.Equal(JsonValueKind.Object, bodyA.GetProperty("seo").ValueKind);
        Assert.Equal(JsonValueKind.Null, bodyB.GetProperty("editorial").ValueKind);
        Assert.Equal(JsonValueKind.Null, bodyB.GetProperty("seo").ValueKind);
    }

    [Fact]
    public async Task The_real_editorial_facade_reads_only_the_resolved_workspaces_acceptance()
    {
        var a = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "ALPHA intro.");

        async Task<Domain.Managers.Results.OperationResult<Domain.Modules.Content.Managers.AcceptedEditorialServiceModel?>> ReadAs(
            SeededWorkspace workspace, int? revision)
        {
            await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
                workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

            return await scope.ServiceProvider.GetRequiredService<Domain.Modules.Content.Facade.IContentEditorialFacade>()
                .GetAcceptedEditorialAsync(a.RecipeId, a.VersionId, revision, Ct);
        }

        // Through the same DI wiring the endpoint uses: A's own context sees A's revision; B's sees nothing, and
        // cannot name A's revision number either.
        Assert.Equal(1, (await ReadAs(_fixture.WorkspaceA, null)).Value!.RevisionNumber);
        Assert.Null((await ReadAs(_fixture.WorkspaceB, null)).Value);
        Assert.Equal("content.revision.not_found", (await ReadAs(_fixture.WorkspaceB, 1)).Error!.Code);
    }
}
