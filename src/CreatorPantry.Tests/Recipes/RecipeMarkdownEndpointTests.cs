extern alias ApiService;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// <c>GET /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/exports/markdown</c> through the real Gateway —
/// real cookie session, real gateway-signed internal token, real API — for what a reader receives, the download
/// headers, the error contract, and what another workspace can never receive.
/// </summary>
public sealed partial class RecipeMarkdownEndpointTests : IAsyncLifetime
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
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}/exports/markdown{query}";

    [GeneratedRegex(@"^attachment; filename=[a-z0-9]+(-[a-z0-9]+)*-v\d+\.md$")]
    private static partial Regex SafeDisposition();

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
        RecipeVersionReadiness readiness = RecipeVersionReadiness.Draft)
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

        await db.SaveChangesAsync(Ct);
        return new SeededRecipe(recipe.Id, versions[0].Id);
    }

    /// <summary>The header exactly as sent, before the client's parser normalises it. Exactly one is sent.</summary>
    private static string RawDisposition(HttpResponseMessage response) =>
        response.Content.Headers.NonValidated["Content-Disposition"].Single();

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private async Task<(HttpResponseMessage Response, string Body)> GetAsync(
        SeededWorkspace inWorkspace, SeededWorkspace viaSlug, Guid recipeId, string query = "", string? email = null)
    {
        using var client = await _fixture.SignInAsync(email ?? inWorkspace.OwnerEmail, cancellationToken: Ct);
        var response = await client.GetAsync(ExportOf(viaSlug, recipeId, query), Ct);
        return (response, await response.Content.ReadAsStringAsync(Ct));
    }

    private Task<(HttpResponseMessage Response, string Body)> OwnA(Guid recipeId, string query = "") =>
        GetAsync(_fixture.WorkspaceA, _fixture.WorkspaceA, recipeId, query);

    // ---- What a reader receives ----

    [Fact]
    public async Task An_approved_recipe_downloads_as_markdown_with_a_safe_name()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);

        var (response, body) = await OwnA(seeded.RecipeId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        Assert.Equal("attachment; filename=soda-bread-v1.md", RawDisposition(response));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("private", response.Headers.CacheControl!.ToString());
        Assert.Contains("no-cache", response.Headers.CacheControl.ToString());
        Assert.Equal("Cookie", string.Join(",", response.Headers.Vary));
        Assert.Matches("^\"[0-9a-f]{32}\"$", response.Headers.ETag!.Tag);

        Assert.StartsWith("# Soda Bread\n", body);
        Assert.Contains("- 4 oz butter\n", body);
        Assert.Contains("Version 1 · Units: as written", body);
    }

    [Fact]
    public async Task The_body_is_exactly_what_the_exporter_renders_for_the_same_input()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);

        var (_, body) = await OwnA(seeded.RecipeId);

        Assert.Equal(
            "# Soda Bread\n\n> A dense loaf.\n\n## Ingredients\n\n- 4 oz butter\n- salt to taste\n\n"
            + "## Instructions\n\n1. Bake.\n\n---\n\nExported from CreatorPantry · Version 1 · Units: as written\n",
            body);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("a\"; filename=\"evil.exe")]
    [InlineData("line\r\nbreak: x")]
    [InlineData("😀 麻婆豆腐")]
    public async Task A_hostile_title_cannot_shape_the_download_name(string title)
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, title);

        var (response, _) = await OwnA(seeded.RecipeId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches(SafeDisposition(), RawDisposition(response));
    }

    [Fact]
    public async Task The_name_carries_no_id_workspace_or_path()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);

        var (response, _) = await OwnA(seeded.RecipeId);

        var disposition = RawDisposition(response);
        Assert.DoesNotContain(seeded.RecipeId.ToString(), disposition);
        Assert.DoesNotContain(_fixture.WorkspaceA.Slug, disposition);
        Assert.DoesNotContain("/", disposition);
        Assert.DoesNotContain("\\", disposition);
    }

    // ---- Template, units, editorial ----

    [Fact]
    public async Task The_compact_template_drops_the_description_and_the_editorial()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "An intro.");

        var (_, standard) = await OwnA(seeded.RecipeId);
        var (_, compact) = await OwnA(seeded.RecipeId, "?template=compact");

        Assert.Contains("> A dense loaf.", standard);
        Assert.Contains("## Introduction (editorial)", standard);
        Assert.Contains("An intro.", standard);
        Assert.DoesNotContain("A dense loaf.", compact);
        Assert.DoesNotContain("An intro.", compact);
    }

    [Fact]
    public async Task Units_append_a_conversion_and_never_replace_the_creators_line()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);

        var (_, metric) = await OwnA(seeded.RecipeId, "?units=metric");
        var (_, us) = await OwnA(seeded.RecipeId, "?units=usCustomary");

        Assert.Contains("- 4 oz butter (≈ 113 g)\n", metric);
        Assert.Contains("- salt to taste\n", metric);
        Assert.Contains("Units: metric", metric);

        // Already US customary, so the line is exactly what the creator wrote.
        Assert.Contains("- 4 oz butter\n", us);
        Assert.DoesNotContain("≈", us);
    }

    [Fact]
    public async Task An_editorial_revision_pinned_to_another_version_is_left_out_and_reported()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "An intro.", secondVersion: true);

        var (response, body) = await OwnA(seeded.RecipeId, "?versionNumber=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("An intro.", body);
        Assert.Contains("recipe_markdown_editorial_not_current", response.Headers.GetValues("Cp-Export-Warnings").Single());
    }

    [Fact]
    public async Task Missing_times_and_yield_are_reported_by_code_in_the_header_and_not_the_body()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);

        var (response, body) = await OwnA(seeded.RecipeId);

        Assert.Equal(
            "recipe_markdown_times_missing, recipe_markdown_yield_missing",
            response.Headers.GetValues("Cp-Export-Warnings").Single());
        Assert.DoesNotContain("recipe_markdown", body);
    }

    [Fact]
    public async Task A_named_version_and_editorial_revision_are_honoured()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "An intro.", secondVersion: true);

        var (response, body) = await OwnA(seeded.RecipeId, "?versionNumber=1&editorialRevision=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Version 1", body);
        Assert.Contains("An intro.", body);
        Assert.Contains("soda-bread-v1.md", RawDisposition(response));
    }

    // ---- Conditional requests ----

    [Fact]
    public async Task A_matching_if_none_match_answers_not_modified_with_no_body()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var first = await client.GetAsync(ExportOf(_fixture.WorkspaceA, seeded.RecipeId), Ct);
        var etag = first.Headers.ETag!.ToString();

        foreach (var validator in new[] { etag, "W/" + etag, "\"other\", " + etag, "*" })
        {
            var again = await client.GetAsync(
                ExportOf(_fixture.WorkspaceA, seeded.RecipeId),
                new Dictionary<string, string> { ["If-None-Match"] = validator }, Ct);

            Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
            Assert.Equal(etag, again.Headers.ETag!.ToString());
            Assert.Empty(await again.Content.ReadAsStringAsync(Ct));
        }

        var stale = await client.GetAsync(
            ExportOf(_fixture.WorkspaceA, seeded.RecipeId),
            new Dictionary<string, string> { ["If-None-Match"] = "\"0123456789abcdef0123456789abcdef\"" }, Ct);
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
    }

    [Fact]
    public async Task The_etag_differs_between_templates_and_unit_presentations()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);

        var etags = new List<string>();
        foreach (var query in new[] { "", "?template=compact", "?units=metric" })
        {
            var (response, _) = await OwnA(seeded.RecipeId, query);
            etags.Add(response.Headers.ETag!.Tag);
        }

        Assert.Equal(3, etags.Distinct().Count());
    }

    // ---- Errors ----

    [Fact]
    public async Task A_version_that_is_not_approved_is_a_conflict()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, status: RecipeStatus.Draft);

        var (response, body) = await OwnA(seeded.RecipeId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("recipes.markdownExport.notApproved.conflict", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.False(response.Content.Headers.Contains("Content-Disposition"));
    }

    [Fact]
    public async Task A_version_marked_ready_is_exported_although_the_recipe_moved_on()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, status: RecipeStatus.Draft, readiness: RecipeVersionReadiness.Ready);

        var (response, _) = await OwnA(seeded.RecipeId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_recipe_missing_facts_is_unprocessable_and_lists_them()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, ingredients: false);

        var (response, body) = await OwnA(seeded.RecipeId);
        var problem = JsonDocument.Parse(body).RootElement;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("recipes.markdownExport.incomplete.unprocessable", problem.GetProperty("code").GetString());
        Assert.Equal(
            "recipe_markdown_ingredients_missing",
            Assert.Single(problem.GetProperty("missingRequired").EnumerateArray()).GetProperty("code").GetString());
        Assert.False(response.Content.Headers.Contains("Content-Disposition"));
    }

    [Fact]
    public async Task An_unknown_recipe_and_a_missing_version_and_an_unaccepted_revision_are_not_found()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "An intro.");

        var unknown = await OwnA(Guid.NewGuid());
        var version = await OwnA(seeded.RecipeId, "?versionNumber=9");
        var revision = await OwnA(seeded.RecipeId, "?editorialRevision=2");

        Assert.Equal(HttpStatusCode.NotFound, unknown.Response.StatusCode);
        Assert.Equal("recipes.recipe.not_found", JsonDocument.Parse(unknown.Body).RootElement.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, version.Response.StatusCode);
        Assert.Equal("recipes.version.not_found", JsonDocument.Parse(version.Body).RootElement.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, revision.Response.StatusCode);
        var problem = JsonDocument.Parse(revision.Body).RootElement;
        Assert.Equal("content.revision.not_found", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("editorialRevision", out _));
    }

    [Theory]
    [InlineData("?versionNumber=0", "versionNumber")]
    [InlineData("?editorialRevision=0", "editorialRevision")]
    [InlineData("?template=fancy", "template")]
    [InlineData("?template=1", "template")]
    [InlineData("?units=imperial", "units")]
    public async Task An_invalid_query_is_a_validation_problem_naming_the_parameter(string query, string parameter)
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);

        var (response, body) = await OwnA(seeded.RecipeId, query);
        var problem = JsonDocument.Parse(body).RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("recipes.markdownExport.invalid_request", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty(parameter, out _));
    }

    [Fact]
    public async Task The_compact_template_does_not_refuse_an_editorial_revision_it_would_not_use()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);

        var (response, _) = await OwnA(seeded.RecipeId, "?template=compact&editorialRevision=9");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Roles ----

    [Fact]
    public async Task A_viewer_may_export()
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
    public async Task A_workspace_id_in_the_query_is_ignored()
    {
        var a = await SeedAsync(_fixture.WorkspaceA);

        var (response, _) = await GetAsync(
            _fixture.WorkspaceB, _fixture.WorkspaceB, a.RecipeId, $"?workspaceId={_fixture.WorkspaceA.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?versionNumber=1&editorialRevision=1")]
    [InlineData("?versionNumber=9")]
    [InlineData("?units=metric&template=compact")]
    public async Task A_foreign_recipe_is_indistinguishable_from_an_unknown_one_whatever_else_is_asked(string query)
    {
        var a = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "A's words.");

        var foreign = await GetAsync(_fixture.WorkspaceB, _fixture.WorkspaceB, a.RecipeId, query);
        var unknown = await GetAsync(_fixture.WorkspaceB, _fixture.WorkspaceB, Guid.NewGuid(), query);
        var throughA = await GetAsync(_fixture.WorkspaceB, _fixture.WorkspaceA, a.RecipeId, query);

        Assert.Equal(HttpStatusCode.NotFound, foreign.Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, throughA.Response.StatusCode);
        Assert.Equal(Shape(unknown.Body), Shape(foreign.Body));
        Assert.DoesNotContain("A's words.", foreign.Body);
        Assert.False(foreign.Response.Content.Headers.Contains("Content-Disposition"));
    }

    private static string Shape(string problem) =>
        string.Join("|", JsonDocument.Parse(problem).RootElement.EnumerateObject()
            .Where(property => property.Name != "traceId")
            .Select(property => $"{property.Name}={property.Value.GetRawText()}"));

    [Fact]
    public async Task Workspace_bs_validator_cannot_confirm_or_reach_workspace_as_export()
    {
        var a = await SeedAsync(_fixture.WorkspaceA);

        // A's real ETag, obtained legitimately by A's owner.
        var (mine, _) = await OwnA(a.RecipeId);
        var etag = mine.Headers.ETag!.ToString();

        using var asB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        foreach (var slugOf in new[] { _fixture.WorkspaceA, _fixture.WorkspaceB })
        {
            foreach (var validator in new[] { etag, "*" })
            {
                var response = await asB.GetAsync(
                    ExportOf(slugOf, a.RecipeId), new Dictionary<string, string> { ["If-None-Match"] = validator }, Ct);

                // 404 before any ETag exists to match, so a validator can never turn into a 304.
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task Each_workspace_receives_only_its_own_accepted_editorial()
    {
        var a = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "ALPHA intro.");
        var b = await SeedAsync(_fixture.WorkspaceB, editorialIntro: "BRAVO intro.");

        var (_, bodyA) = await GetAsync(_fixture.WorkspaceA, _fixture.WorkspaceA, a.RecipeId);
        var (_, bodyB) = await GetAsync(_fixture.WorkspaceB, _fixture.WorkspaceB, b.RecipeId);

        Assert.Contains("ALPHA intro.", bodyA);
        Assert.DoesNotContain("BRAVO", bodyA);
        Assert.Contains("BRAVO intro.", bodyB);
        Assert.DoesNotContain("ALPHA", bodyB);
    }

    [Fact]
    public async Task Naming_an_editorial_revision_another_workspace_accepted_is_not_found()
    {
        var a = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "ALPHA intro.");
        var b = await SeedAsync(_fixture.WorkspaceB);

        // A has accepted revision 1; B's recipe has none, and A's is not visible from B.
        var (response, body) = await GetAsync(_fixture.WorkspaceB, _fixture.WorkspaceB, b.RecipeId, "?editorialRevision=1");

        Assert.NotEqual(a.RecipeId, b.RecipeId);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("content.revision.not_found", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    // ---- The route ----

    [Fact]
    public void The_route_is_versioned_workspace_shaped_viewer_only_and_a_single_get()
    {
        var action = typeof(RecipeExportsController).GetMethod(nameof(RecipeExportsController.GetMarkdown))!;

        Assert.Equal(
            "markdown",
            action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpGetAttribute), false)
                .Cast<Microsoft.AspNetCore.Mvc.HttpGetAttribute>().Single().Template);
        Assert.Equal(
            ApiService::CreatorPantry.ApiService.Authorization.AuthorizationPolicies.WorkspaceViewer,
            action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single().Policy);
        Assert.Equal("text/markdown", RecipeExportsController.MarkdownContentType);
    }
}
