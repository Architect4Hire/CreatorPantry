extern alias ApiService;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ApiService::CreatorPantry.ApiService.Controllers;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/exports/json-ld</c> through the real Gateway
/// for its error contract and its workspace isolation, and the controller on its own for what a success looks
/// like on the wire — a success cannot be reached over HTTP until media supplies an image URL.
/// </summary>
public sealed class RecipeJsonLdEndpointTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string ExportOf(SeededWorkspace workspace, Guid recipeId, string query = "") =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}/exports/json-ld{query}";

    // ---- Seeding ----

    private static RecipeSnapshotDocument Snapshot(RecipeStatus status) => new()
    {
        SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
        Recipe = new RecipeSnapshotHeader { Title = "Soda Bread", Status = status },
        IngredientGroups =
        [
            new RecipeSnapshotIngredientGroup
            {
                Id = Guid.NewGuid(),
                Ingredients = [new RecipeSnapshotIngredient { Id = Guid.NewGuid(), DisplayText = "500 g flour" }],
            },
        ],
        InstructionGroups =
        [
            new RecipeSnapshotInstructionGroup
            {
                Id = Guid.NewGuid(),
                Steps = [new RecipeSnapshotInstructionStep { Id = Guid.NewGuid(), Text = "Bake." }],
            },
        ],
    };

    private sealed record SeededRecipe(Guid RecipeId, Guid VersionId);

    /// <summary>A recipe with one version holding the given snapshot, and optionally an accepted SEO revision pinned to it.</summary>
    private async Task<SeededRecipe> SeedAsync(
        SeededWorkspace workspace,
        RecipeStatus status,
        RecipeVersionReadiness readiness = RecipeVersionReadiness.Draft,
        bool acceptedSeo = false)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var author = Guid.NewGuid();
        var recipe = new Recipe
        {
            Id = Guid.NewGuid(), Title = "Soda Bread", Status = status, CreatedByMembershipId = author,
            UpdatedByMembershipId = author, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var version = new RecipeVersion
        {
            Id = Guid.NewGuid(), RecipeId = recipe.Id, VersionNumber = 1, Source = RecipeVersionSource.CreatorEdit,
            Readiness = readiness, CreatedByMembershipId = author, CreatedAt = DateTimeOffset.UtcNow,
            SnapshotSchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
            Snapshot = new RecipeVersionSnapshot
            {
                Document = RecipeSnapshotSerializer.Serialize(Snapshot(status)),
            },
        };
        version.Snapshot.RecipeVersionId = version.Id;

        db.Recipes.Add(recipe);
        db.RecipeVersions.Add(version);

        if (acceptedSeo)
        {
            var proposal = new ContentProposal
            {
                Id = Guid.NewGuid(), RecipeId = recipe.Id, Kind = ContentPackageKind.Seo,
                Status = ContentProposalStatus.Proposed, CreatedByMembershipId = author,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            var revision = new ContentRevision
            {
                Id = Guid.NewGuid(), ContentProposalId = proposal.Id, RecipeId = recipe.Id, RevisionNumber = 1,
                Source = ContentRevisionSource.CreatorEdit, RecipeVersionId = version.Id, SchemaVersion = 1,
                Content = """{"schemaVersion":"content.seo-package.v1","sections":{"metaDescription":{"text":"A loaf."}}}""",
                CreatedByMembershipId = author, CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ContentProposals.Add(proposal);
            db.ContentRevisions.Add(revision);
            await db.SaveChangesAsync(Ct);

            proposal.Status = ContentProposalStatus.Accepted;
            proposal.AcceptedRevisionId = revision.Id;
        }

        await db.SaveChangesAsync(Ct);
        return new SeededRecipe(recipe.Id, version.Id);
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    // ---- Errors over HTTP ----

    [Fact]
    public async Task An_approved_recipe_without_an_image_is_unprocessable_and_lists_what_is_missing()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, RecipeStatus.Approved);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var response = await client.GetAsync(ExportOf(_fixture.WorkspaceA, seeded.RecipeId), Ct);
        var problem = await ProblemAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("recipes.jsonLdExport.incomplete.unprocessable", problem.GetProperty("code").GetString());

        var missing = problem.GetProperty("missingRequired");
        Assert.Equal("recipe_json_ld_image_missing", Assert.Single(missing.EnumerateArray()).GetProperty("code").GetString());

        // A refusal carries no document, and nothing the model has not got.
        Assert.False(problem.TryGetProperty("@type", out _));
    }

    [Fact]
    public async Task A_version_that_is_not_approved_is_a_conflict()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, RecipeStatus.Draft);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var response = await client.GetAsync(ExportOf(_fixture.WorkspaceA, seeded.RecipeId), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "recipes.jsonLdExport.notApproved.conflict", (await ProblemAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_version_marked_ready_gets_past_the_approval_gate()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, RecipeStatus.Draft, RecipeVersionReadiness.Ready);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var response = await client.GetAsync(ExportOf(_fixture.WorkspaceA, seeded.RecipeId), Ct);

        // Past the gate and into generation, where the missing image is the only thing left to say.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_recipe_is_not_found()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var response = await client.GetAsync(ExportOf(_fixture.WorkspaceA, Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("recipes.recipe.not_found", (await ProblemAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_version_the_recipe_lacks_is_not_found_naming_the_parameter()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, RecipeStatus.Approved);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var response = await client.GetAsync(ExportOf(_fixture.WorkspaceA, seeded.RecipeId, "?versionNumber=9"), Ct);
        var problem = await ProblemAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("recipes.version.not_found", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("versionNumber", out _));
    }

    [Theory]
    [InlineData("?versionNumber=0", "versionNumber")]
    [InlineData("?versionNumber=-1", "versionNumber")]
    [InlineData("?seoRevision=0", "seoRevision")]
    public async Task A_number_below_one_is_a_validation_problem_naming_the_parameter(string query, string parameter)
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, RecipeStatus.Approved);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var response = await client.GetAsync(ExportOf(_fixture.WorkspaceA, seeded.RecipeId, query), Ct);
        var problem = await ProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("recipes.jsonLdExport.invalid_request", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty(parameter, out _));
    }

    [Fact]
    public async Task Only_the_accepted_seo_revision_can_be_named()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, RecipeStatus.Approved, acceptedSeo: true);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var accepted = await client.GetAsync(ExportOf(_fixture.WorkspaceA, seeded.RecipeId, "?seoRevision=1"), Ct);
        var other = await client.GetAsync(ExportOf(_fixture.WorkspaceA, seeded.RecipeId, "?seoRevision=2"), Ct);
        var problem = await ProblemAsync(other);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal("content.revision.not_found", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("seoRevision", out _));
    }

    [Fact]
    public async Task A_seo_revision_is_not_found_when_nothing_was_accepted()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, RecipeStatus.Approved);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var response = await client.GetAsync(ExportOf(_fixture.WorkspaceA, seeded.RecipeId, "?seoRevision=1"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_workspace_id_in_the_query_is_ignored()
    {
        var a = await SeedAsync(_fixture.WorkspaceA, RecipeStatus.Approved);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        var response = await client.GetAsync(
            ExportOf(_fixture.WorkspaceB, a.RecipeId, $"?workspaceId={_fixture.WorkspaceA.Id}"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Roles and isolation ----

    [Fact]
    public async Task A_viewer_may_export()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceB, RecipeStatus.Approved);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: Ct);
        var response = await client.GetAsync(ExportOf(_fixture.WorkspaceB, seeded.RecipeId), Ct);

        // Authorized, and on to the export's own answer rather than a 403.
        Assert.Equal(WorkspaceRole.Viewer, _fixture.WorkspaceB.MemberRole);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Another_workspaces_recipe_is_not_found_through_either_slug_and_leaks_no_difference()
    {
        var a = await SeedAsync(_fixture.WorkspaceA, RecipeStatus.Approved, acceptedSeo: true);

        using var asB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);

        // Through B's own slug: B's context cannot see A's recipe.
        var throughB = await asB.GetAsync(ExportOf(_fixture.WorkspaceB, a.RecipeId), Ct);
        var throughBUnknown = await asB.GetAsync(ExportOf(_fixture.WorkspaceB, Guid.NewGuid()), Ct);

        // Through A's slug: B is no member of A, so A's existence is not disclosed either.
        var throughA = await asB.GetAsync(ExportOf(_fixture.WorkspaceA, a.RecipeId), Ct);

        Assert.Equal(HttpStatusCode.NotFound, throughB.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, throughA.StatusCode);
        Assert.Equal(
            (await ProblemAsync(throughBUnknown)).GetProperty("code").GetString(),
            (await ProblemAsync(throughB)).GetProperty("code").GetString());

        // A's own owner still reaches it, so the 404s above are isolation and not a broken seed.
        using var asA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await asA.GetAsync(ExportOf(_fixture.WorkspaceA, a.RecipeId), Ct)).StatusCode);
    }

    [Theory]
    [InlineData("?versionNumber=1&seoRevision=1")]
    [InlineData("?versionNumber=9")]
    [InlineData("?seoRevision=1")]
    public async Task A_foreign_recipe_is_indistinguishable_from_an_unknown_one_whatever_else_is_asked(string query)
    {
        var a = await SeedAsync(_fixture.WorkspaceA, RecipeStatus.Approved, acceptedSeo: true);

        using var asB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        var foreign = await asB.GetAsync(ExportOf(_fixture.WorkspaceB, a.RecipeId, query), Ct);
        var unknown = await asB.GetAsync(ExportOf(_fixture.WorkspaceB, Guid.NewGuid(), query), Ct);

        // The recipe check runs before any version or revision lookup, so neither parameter can be used to
        // learn that a foreign recipe exists or what it has accepted.
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(unknown.StatusCode, foreign.StatusCode);
        Assert.Equal(Shape(await ProblemAsync(unknown)), Shape(await ProblemAsync(foreign)));
    }

    /// <summary>A problem body without the per-request trace id, so two refusals can be compared whole.</summary>
    private static string Shape(JsonElement problem) =>
        string.Join("|", problem.EnumerateObject()
            .Where(property => property.Name != "traceId")
            .Select(property => $"{property.Name}={property.Value.GetRawText()}"));

    [Theory]
    [InlineData("\"0123456789abcdef0123456789abcdef\"")]
    [InlineData("*")]
    public async Task A_conditional_request_cannot_confirm_a_foreign_recipe(string ifNoneMatch)
    {
        var a = await SeedAsync(_fixture.WorkspaceA, RecipeStatus.Approved, acceptedSeo: true);
        var headers = new Dictionary<string, string> { ["If-None-Match"] = ifNoneMatch };

        using var asB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        var throughB = await asB.GetAsync(ExportOf(_fixture.WorkspaceB, a.RecipeId), headers, Ct);
        var throughA = await asB.GetAsync(ExportOf(_fixture.WorkspaceA, a.RecipeId), headers, Ct);

        // 404 before an ETag is ever computed, so no validator can turn into a 304.
        Assert.Equal(HttpStatusCode.NotFound, throughB.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, throughA.StatusCode);
    }

    [Fact]
    public async Task The_real_seo_facade_reads_only_the_resolved_workspaces_acceptance()
    {
        var a = await SeedAsync(_fixture.WorkspaceA, RecipeStatus.Approved, acceptedSeo: true);

        async Task<OperationResult<Domain.Modules.Content.Managers.AcceptedSeoServiceModel?>> ReadAs(SeededWorkspace workspace)
        {
            await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
                workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

            return await scope.ServiceProvider.GetRequiredService<Domain.Modules.Content.Facade.IContentSeoFacade>()
                .GetAcceptedSeoAsync(a.RecipeId, a.VersionId, null, Ct);
        }

        // Through the same DI wiring the endpoint uses: A's own context sees A's words, B's sees nothing.
        Assert.Equal("A loaf.", (await ReadAs(_fixture.WorkspaceA)).Value!.MetaDescription);
        Assert.Null((await ReadAs(_fixture.WorkspaceB)).Value);
    }

    [Fact]
    public async Task Another_workspaces_seo_revision_cannot_be_named_against_my_recipe()
    {
        var a = await SeedAsync(_fixture.WorkspaceA, RecipeStatus.Approved, acceptedSeo: true);
        var b = await SeedAsync(_fixture.WorkspaceB, RecipeStatus.Approved);

        using var asB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        var response = await asB.GetAsync(ExportOf(_fixture.WorkspaceB, b.RecipeId, "?seoRevision=1"), Ct);

        // A has an accepted revision 1; B's recipe has none, and A's is not visible from B.
        Assert.NotEqual(a.RecipeId, b.RecipeId);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("content.revision.not_found", (await ProblemAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, RecipeStatus.Approved);

        using var http = _fixture.Gateway.CreateClient();
        var response = await http.GetAsync(ExportOf(_fixture.WorkspaceA, seeded.RecipeId), Ct);

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect or HttpStatusCode.Found);
    }

    // ---- The wire format of a success, on the controller itself ----

    private sealed class FixedFacade(OperationResult<RecipeJsonLdExportServiceModel> result) : IRecipeExportFacade
    {
        public Task<OperationResult<RecipeJsonLdExportServiceModel>> GetJsonLdAsync(
            Guid recipeId, RecipeJsonLdExportViewModel model, CancellationToken cancellationToken) =>
            Task.FromResult(result);

        public Task<OperationResult<RecipeMarkdownExportServiceModel>> GetMarkdownAsync(
            Guid recipeId, RecipeMarkdownExportViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static (RecipeExportsController Controller, DefaultHttpContext Http) ControllerFor(
        string json, params string[] warnings)
    {
        var export = new RecipeJsonLdExportServiceModel(
            json, 3, [.. warnings.Select(code => new RecipeJsonLdIssue(code, "m"))]);
        var http = new DefaultHttpContext();
        var controller = new RecipeExportsController(new FixedFacade(OperationResult<RecipeJsonLdExportServiceModel>.Success(export)))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        return (controller, http);
    }

    [Fact]
    public async Task A_success_is_the_document_alone_as_ld_json_with_revalidation_headers()
    {
        var (controller, http) = ControllerFor("""{"@type":"Recipe"}""");

        var result = await controller.GetJsonLd("w", Guid.NewGuid(), new RecipeJsonLdExportViewModel(), Ct);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("""{"@type":"Recipe"}""", content.Content);
        Assert.Equal("application/ld+json; charset=utf-8", content.ContentType);
        Assert.Equal("private, no-cache", http.Response.Headers.CacheControl.ToString());
        Assert.Matches("^\"[0-9a-f]{32}\"$", http.Response.Headers.ETag.ToString());
        Assert.Equal("Cookie", http.Response.Headers.Vary.ToString());
        Assert.False(http.Response.Headers.ContainsKey(RecipeExportsController.WarningsHeader));
    }

    [Fact]
    public async Task Warnings_are_listed_by_code_in_their_own_header_and_not_in_the_body()
    {
        var (controller, http) = ControllerFor("{}", "recipe_json_ld_times_missing", "recipe_json_ld_yield_missing");

        var result = await controller.GetJsonLd("w", Guid.NewGuid(), new RecipeJsonLdExportViewModel(), Ct);

        Assert.Equal(
            "recipe_json_ld_times_missing, recipe_json_ld_yield_missing",
            http.Response.Headers[RecipeExportsController.WarningsHeader].ToString());
        Assert.Equal("{}", Assert.IsType<ContentResult>(result).Content);
    }

    [Fact]
    public async Task The_etag_is_stable_and_changes_with_the_body_or_the_warnings()
    {
        static async Task<string> ETagOf(string json, params string[] warnings)
        {
            var (controller, http) = ControllerFor(json, warnings);
            await controller.GetJsonLd("w", Guid.NewGuid(), new RecipeJsonLdExportViewModel(), Ct);
            return http.Response.Headers.ETag.ToString();
        }

        var baseline = await ETagOf("{}");

        Assert.Equal(baseline, await ETagOf("{}"));
        Assert.NotEqual(baseline, await ETagOf("{ }"));
        Assert.NotEqual(baseline, await ETagOf("{}", "recipe_json_ld_times_missing"));
    }

    [Theory]
    [InlineData(true, "exact", HttpStatusCode.NotModified)]
    [InlineData(true, "weak", HttpStatusCode.NotModified)]
    [InlineData(true, "list", HttpStatusCode.NotModified)]
    [InlineData(true, "star", HttpStatusCode.NotModified)]
    [InlineData(false, "stale", HttpStatusCode.OK)]
    public async Task A_matching_if_none_match_answers_not_modified_with_no_body(
        bool expectNotModified, string form, HttpStatusCode expected)
    {
        var (first, firstHttp) = ControllerFor("{}");
        await first.GetJsonLd("w", Guid.NewGuid(), new RecipeJsonLdExportViewModel(), Ct);
        var etag = firstHttp.Response.Headers.ETag.ToString();

        var (controller, http) = ControllerFor("{}");
        http.Request.Headers.IfNoneMatch = form switch
        {
            "exact" => etag,
            "weak" => "W/" + etag,
            "list" => "\"other\", " + etag,
            "star" => "*",
            _ => "\"0123456789abcdef0123456789abcdef\"",
        };

        var result = await controller.GetJsonLd("w", Guid.NewGuid(), new RecipeJsonLdExportViewModel(), Ct);

        if (expectNotModified)
        {
            Assert.Equal((int)expected, Assert.IsType<StatusCodeResult>(result).StatusCode);
            Assert.Equal(etag, http.Response.Headers.ETag.ToString());
        }
        else
        {
            Assert.IsType<ContentResult>(result);
        }
    }

    [Fact]
    public void The_route_is_versioned_workspace_shaped_and_limited_to_viewers_and_reads()
    {
        var action = typeof(RecipeExportsController).GetMethod(nameof(RecipeExportsController.GetJsonLd))!;
        var route = typeof(RecipeExportsController).GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>().Single();

        Assert.Equal("api/v{version:apiVersion}/workspaces/{workspaceSlug}/recipes/{recipeId:guid}/exports", route.Template);
        Assert.Equal("json-ld", action.GetCustomAttributes(typeof(HttpGetAttribute), false).Cast<HttpGetAttribute>().Single().Template);

        // The controller exposes exactly these two reads, both GETs, and nothing that writes.
        var templates = typeof(RecipeExportsController).GetMethods()
            .SelectMany(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute), false)
                .Cast<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>())
            .ToList();
        Assert.All(templates, attribute => Assert.Equal(["GET"], attribute.HttpMethods));
        Assert.Equal(["json-ld", "markdown"], templates.Select(attribute => attribute.Template!).Order());
        Assert.Equal(
            ApiService::CreatorPantry.ApiService.Authorization.AuthorizationPolicies.WorkspaceViewer,
            action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single().Policy);
    }
}
