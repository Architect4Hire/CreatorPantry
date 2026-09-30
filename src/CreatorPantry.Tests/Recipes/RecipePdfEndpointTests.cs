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
/// <c>GET /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/exports/pdf</c> through the real Gateway —
/// real cookie session, real gateway-signed internal token, real API — for what a reader receives, the download
/// headers, the error contract, and what another workspace can never receive.
/// </summary>
public sealed partial class RecipePdfEndpointTests : IAsyncLifetime
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

    private Task<(HttpResponseMessage Response, byte[] Bytes)> OwnA(Guid recipeId, string query = "") =>
        GetBytesAsync(_fixture.WorkspaceA, _fixture.WorkspaceA, recipeId, query);

    private static string ExportOf(SeededWorkspace workspace, Guid recipeId, string query = "") =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}/exports/pdf{query}";

    [GeneratedRegex(@"^attachment; filename=[a-z0-9]+(-[a-z0-9]+)*-v\d+\.pdf$")]
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
        RecipeVersionReadiness readiness = RecipeVersionReadiness.Draft,
        string? corruptDocument = null)
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
                Document = corruptDocument ?? RecipeSnapshotSerializer.Serialize(snapshot),
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

    private async Task<(HttpResponseMessage Response, byte[] Bytes)> GetBytesAsync(
        SeededWorkspace inWorkspace, SeededWorkspace viaSlug, Guid recipeId, string query = "", string? email = null)
    {
        using var client = await _fixture.SignInAsync(email ?? inWorkspace.OwnerEmail, cancellationToken: Ct);
        var response = await client.GetAsync(ExportOf(viaSlug, recipeId, query), Ct);
        return (response, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    private static string TextOf(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join("\n", document.GetPages().Select(page => page.Text));
    }

    private static string Normalised(string text) => Regex.Replace(text, @"\s+", "");

    private static string Problem(string body, string name) =>
        JsonDocument.Parse(body).RootElement.GetProperty(name).GetString()!;

    // ---- What a reader receives ----

    [Fact]
    public async Task An_approved_recipe_downloads_as_a_pdf_with_real_text_and_a_safe_name()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);

        var (response, bytes) = await OwnA(seeded.RecipeId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment; filename=soda-bread-v1.pdf", RawDisposition(response));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("private", response.Headers.CacheControl!.ToString());
        Assert.Contains("no-cache", response.Headers.CacheControl.ToString());
        Assert.Equal("Cookie", string.Join(",", response.Headers.Vary));
        Assert.Matches("^\"[0-9a-f]{32}\"$", response.Headers.ETag!.Tag);

        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        var text = Normalised(TextOf(bytes));
        Assert.Contains("SodaBread", text);
        Assert.Contains("4ozbutter", text);
        Assert.Contains("Bake.", text);
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

        // Whether the font can draw the title is the renderer's business; the name is safe either way.
        if (response.StatusCode == HttpStatusCode.OK)
        {
            Assert.Matches(SafeDisposition(), RawDisposition(response));
        }
        else
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.False(response.Content.Headers.Contains("Content-Disposition"));
        }
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

    // ---- Template, units, editorial, page size ----

    [Fact]
    public async Task The_compact_template_drops_the_description_and_the_editorial()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "An intro.");

        var (_, standard) = await OwnA(seeded.RecipeId);
        var (_, compact) = await OwnA(seeded.RecipeId, "?template=compact");

        Assert.Contains("Adenseloaf.", Normalised(TextOf(standard)));
        Assert.Contains("Anintro.", Normalised(TextOf(standard)));
        Assert.DoesNotContain("Adenseloaf.", Normalised(TextOf(compact)));
        Assert.DoesNotContain("Anintro.", Normalised(TextOf(compact)));
    }

    [Fact]
    public async Task Units_append_a_conversion_and_never_replace_the_creators_line()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);

        var (_, metric) = await OwnA(seeded.RecipeId, "?units=metric");

        var text = Normalised(TextOf(metric));
        Assert.Contains("4ozbutter", text);
        Assert.Contains("113g", text);
        Assert.Contains("salttotaste", text);
    }

    [Fact]
    public async Task Page_size_selects_the_paper()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);

        var (_, a4) = await OwnA(seeded.RecipeId);
        var (_, letter) = await OwnA(seeded.RecipeId, "?pageSize=letter");

        using var a4Doc = PdfDocument.Open(a4);
        using var letterDoc = PdfDocument.Open(letter);
        Assert.Equal(595, Math.Round(a4Doc.GetPage(1).Width));
        Assert.Equal(612, Math.Round(letterDoc.GetPage(1).Width));
    }

    [Fact]
    public async Task An_editorial_revision_pinned_to_another_version_is_left_out_and_reported()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "An intro.", secondVersion: true);

        var (response, bytes) = await OwnA(seeded.RecipeId, "?versionNumber=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Anintro.", Normalised(TextOf(bytes)));
        Assert.Contains("recipe_pdf_editorial_not_current", response.Headers.GetValues("Cp-Export-Warnings").Single());
    }

    [Fact]
    public async Task No_image_is_embedded_because_none_is_authorized()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);

        var (_, bytes) = await OwnA(seeded.RecipeId);

        using var document = PdfDocument.Open(bytes);
        Assert.Empty(document.GetPages().SelectMany(page => page.GetImages()));
    }

    // ---- Conditional requests ----

    [Fact]
    public async Task The_etag_is_stable_across_renders_and_answers_not_modified()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var first = await client.GetAsync(ExportOf(_fixture.WorkspaceA, seeded.RecipeId), Ct);
        var second = await client.GetAsync(ExportOf(_fixture.WorkspaceA, seeded.RecipeId), Ct);
        var etag = first.Headers.ETag!.ToString();

        // The bytes carry a creation stamp, so only a validator over the inputs can be stable.
        Assert.Equal(etag, second.Headers.ETag!.ToString());

        var again = await client.GetAsync(
            ExportOf(_fixture.WorkspaceA, seeded.RecipeId),
            new Dictionary<string, string> { ["If-None-Match"] = etag }, Ct);
        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
        Assert.Empty(await again.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task The_etag_differs_between_templates_units_and_page_sizes()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);

        var etags = new List<string>();
        foreach (var query in new[] { "", "?template=compact", "?units=metric", "?pageSize=letter" })
        {
            var (response, _) = await OwnA(seeded.RecipeId, query);
            etags.Add(response.Headers.ETag!.Tag);
        }

        Assert.Equal(4, etags.Distinct().Count());
    }

    // ---- Errors ----

    [Fact]
    public async Task A_version_that_is_not_approved_is_a_conflict()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, status: RecipeStatus.Draft);

        var (response, bytes) = await OwnA(seeded.RecipeId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("recipes.pdfExport.notApproved.conflict", Problem(System.Text.Encoding.UTF8.GetString(bytes), "code"));
        Assert.False(response.Content.Headers.Contains("Content-Disposition"));
    }

    [Fact]
    public async Task A_version_whose_stored_document_cannot_be_read_is_a_controlled_server_error_and_leaks_nothing()
    {
        // Snapshots are immutable once written, so this cannot arise through the app; it is seeded at insert
        // to pin down what an export does if storage is ever corrupted.
        var seeded = await SeedAsync(_fixture.WorkspaceA, corruptDocument: "not json {");

        var (response, bytes) = await OwnA(seeded.RecipeId);
        var body = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.False(response.Content.Headers.Contains("Content-Disposition"));
        Assert.DoesNotContain("not json", body);
        Assert.DoesNotContain("JsonException", body);
        Assert.DoesNotContain("   at ", body);
    }

    [Fact]
    public async Task A_refusal_is_never_cacheable()
    {
        var notApproved = await SeedAsync(_fixture.WorkspaceA, status: RecipeStatus.Draft);
        var incomplete = await SeedAsync(_fixture.WorkspaceA, ingredients: false);

        foreach (var (response, _) in new[]
        {
            await OwnA(notApproved.RecipeId),           // 409
            await OwnA(incomplete.RecipeId),            // 422, which lists the recipe's missing facts
            await OwnA(Guid.NewGuid()),                 // 404
            await OwnA(incomplete.RecipeId, "?pageSize=tabloid"), // 400
        })
        {
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
            Assert.Contains("private", response.Headers.CacheControl.ToString());
            Assert.Equal("Cookie", string.Join(",", response.Headers.Vary));
        }
    }

    [Fact]
    public async Task A_version_marked_ready_is_exported_although_the_recipe_moved_on()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, status: RecipeStatus.Draft, readiness: RecipeVersionReadiness.Ready);

        var (response, _) = await OwnA(seeded.RecipeId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_recipe_that_cannot_be_drawn_is_unprocessable_and_says_why()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, ingredients: false);

        var (response, bytes) = await OwnA(seeded.RecipeId);
        var body = System.Text.Encoding.UTF8.GetString(bytes);
        var problem = JsonDocument.Parse(body).RootElement;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("recipes.pdfExport.incomplete.unprocessable", problem.GetProperty("code").GetString());
        Assert.Equal(
            "recipe_pdf_ingredients_missing",
            Assert.Single(problem.GetProperty("missingRequired").EnumerateArray()).GetProperty("code").GetString());
        Assert.False(response.Content.Headers.Contains("Content-Disposition"));
    }

    private static Domain.Modules.Measurement.Managers.MeasurementUnitServiceModel Gram(decimal? factor) =>
        new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), "g", "gram", "grams", "g",
            MeasurementDimension.Mass, Domain.Modules.Measurement.Managers.MeasurementSystem.Metric, factor, 0);

    // Built once: a snapshot carries ids, and fresh ones per call would make identical inputs differ.
    private static readonly RecipeExportSource TagSource = new(
            Guid.Parse("00000000-0000-0000-0000-0000000000b1"), 1, RecipeVersionReadiness.Ready,
            new RecipeSnapshotDocument
            {
                SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
                Recipe = new RecipeSnapshotHeader { Title = "Soda Bread", Status = RecipeStatus.Approved },
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
            });

    private static string TagFor(Domain.Modules.Measurement.Managers.MeasurementUnitServiceModel target)
    {
        var business = new RecipeExportBusiness(dataLayer: null!);

        var result = business.BuildPdf(
            TagSource, RecipeExportTemplate.Standard, RecipeUnitPresentation.Metric, RecipePdfPageSize.A4,
            new Dictionary<Guid, Domain.Modules.Measurement.Managers.MeasurementUnitServiceModel>(),
            new Dictionary<MeasurementDimension, Domain.Modules.Measurement.Managers.MeasurementUnitServiceModel>
            {
                [MeasurementDimension.Mass] = target,
            },
            editorial: null, image: null, DateTimeOffset.UtcNow);

        Assert.True(result.Succeeded);
        return result.Value!.ContentTag;
    }

    [Fact]
    public void The_validator_is_stable_for_one_catalogue_and_changes_when_the_catalogue_is_corrected()
    {
        Assert.Equal(TagFor(Gram(1m)), TagFor(Gram(1m)));

        // A corrected conversion factor changes every converted amount on the page, so it must change the tag:
        // otherwise a client holding the old PDF would be told it is still current.
        Assert.NotEqual(TagFor(Gram(1m)), TagFor(Gram(2m)));
    }

    [Fact]
    public void A_renderer_fault_is_a_500_with_its_own_code_and_no_detail()
    {
        var business = new RecipeExportBusiness(dataLayer: null!);
        var broken = new RecipeExportSource(
            Guid.NewGuid(), 1, RecipeVersionReadiness.Ready, new RecipeSnapshotDocument { Recipe = null! });

        var result = business.BuildPdf(
            broken, RecipeExportTemplate.Standard, RecipeUnitPresentation.AsWritten, RecipePdfPageSize.A4,
            new Dictionary<Guid, Domain.Modules.Measurement.Managers.MeasurementUnitServiceModel>(),
            new Dictionary<MeasurementDimension, Domain.Modules.Measurement.Managers.MeasurementUnitServiceModel>(),
            editorial: null, image: null, DateTimeOffset.UtcNow);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.PdfExportRenderFailed, result.Error!.Code);
        Assert.Equal(500, ProblemResults.StatusFor(result.Error.Code));
        Assert.Empty(result.Error.FieldErrors);
        Assert.Null(result.Error.Extensions);
    }

    [Fact]
    public async Task An_unknown_recipe_and_a_missing_version_and_an_unaccepted_revision_are_not_found()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "An intro.");

        var unknown = await OwnA(Guid.NewGuid());
        var version = await OwnA(seeded.RecipeId, "?versionNumber=9");
        var revision = await OwnA(seeded.RecipeId, "?editorialRevision=2");

        Assert.Equal(HttpStatusCode.NotFound, unknown.Response.StatusCode);
        Assert.Equal("recipes.recipe.not_found", Problem(System.Text.Encoding.UTF8.GetString(unknown.Bytes), "code"));
        Assert.Equal(HttpStatusCode.NotFound, version.Response.StatusCode);
        Assert.Equal("recipes.version.not_found", Problem(System.Text.Encoding.UTF8.GetString(version.Bytes), "code"));
        Assert.Equal(HttpStatusCode.NotFound, revision.Response.StatusCode);
        var problem = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(revision.Bytes)).RootElement;
        Assert.Equal("content.revision.not_found", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("editorialRevision", out _));
    }

    [Theory]
    [InlineData("?versionNumber=0", "versionNumber")]
    [InlineData("?editorialRevision=0", "editorialRevision")]
    [InlineData("?template=fancy", "template")]
    [InlineData("?units=imperial", "units")]
    [InlineData("?pageSize=tabloid", "pageSize")]
    [InlineData("?pageSize=1", "pageSize")]
    public async Task An_invalid_query_is_a_validation_problem_naming_the_parameter(string query, string parameter)
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);

        var (response, bytes) = await OwnA(seeded.RecipeId, query);
        var problem = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes)).RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("recipes.pdfExport.invalid_request", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty(parameter, out _));
    }

    [Theory]
    [InlineData("?imageUrl=http://169.254.169.254/latest")]
    [InlineData("?image=https://example.com/a.png")]
    [InlineData("?assetId=00000000-0000-0000-0000-000000000001")]
    public async Task No_query_parameter_can_name_an_image(string query)
    {
        var seeded = await SeedAsync(_fixture.WorkspaceA);

        var (response, bytes) = await OwnA(seeded.RecipeId, query);

        // Unknown parameters are ignored, so nothing is fetched and the document has no figure.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = PdfDocument.Open(bytes);
        Assert.Empty(document.GetPages().SelectMany(page => page.GetImages()));
    }

    // ---- Roles ----

    [Fact]
    public async Task A_viewer_may_export()
    {
        var seeded = await SeedAsync(_fixture.WorkspaceB);

        var (response, _) = await GetBytesAsync(
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

        var (response, _) = await GetBytesAsync(
            _fixture.WorkspaceB, _fixture.WorkspaceB, a.RecipeId, $"?workspaceId={_fixture.WorkspaceA.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?versionNumber=1&editorialRevision=1")]
    [InlineData("?versionNumber=9")]
    [InlineData("?units=metric&template=compact&pageSize=letter")]
    public async Task A_foreign_recipe_is_indistinguishable_from_an_unknown_one_whatever_else_is_asked(string query)
    {
        var a = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "A's words.");

        var foreign = await GetBytesAsync(_fixture.WorkspaceB, _fixture.WorkspaceB, a.RecipeId, query);
        var unknown = await GetBytesAsync(_fixture.WorkspaceB, _fixture.WorkspaceB, Guid.NewGuid(), query);
        var throughA = await GetBytesAsync(_fixture.WorkspaceB, _fixture.WorkspaceA, a.RecipeId, query);

        Assert.Equal(HttpStatusCode.NotFound, foreign.Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, throughA.Response.StatusCode);
        Assert.Equal(Shape(unknown.Bytes), Shape(foreign.Bytes));
        Assert.DoesNotContain("A's words.", System.Text.Encoding.UTF8.GetString(foreign.Bytes));
        Assert.False(foreign.Response.Content.Headers.Contains("Content-Disposition"));
        Assert.Null(foreign.Response.Headers.ETag);
    }

    private static string Shape(byte[] problem) =>
        string.Join("|", JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(problem)).RootElement.EnumerateObject()
            .Where(property => property.Name != "traceId")
            .Select(property => $"{property.Name}={property.Value.GetRawText()}"));

    [Fact]
    public async Task Workspace_bs_validator_cannot_confirm_or_reach_workspace_as_export()
    {
        var a = await SeedAsync(_fixture.WorkspaceA);

        var (mine, _) = await OwnA(a.RecipeId);
        var etag = mine.Headers.ETag!.ToString();

        using var asB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        foreach (var slugOf in new[] { _fixture.WorkspaceA, _fixture.WorkspaceB })
        {
            foreach (var validator in new[] { etag, "*" })
            {
                var response = await asB.GetAsync(
                    ExportOf(slugOf, a.RecipeId), new Dictionary<string, string> { ["If-None-Match"] = validator }, Ct);

                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task Each_workspace_receives_only_its_own_accepted_editorial()
    {
        var a = await SeedAsync(_fixture.WorkspaceA, editorialIntro: "ALPHA intro.");
        var b = await SeedAsync(_fixture.WorkspaceB, editorialIntro: "BRAVO intro.");

        var (_, bytesA) = await OwnA(a.RecipeId);
        var (_, bytesB) = await GetBytesAsync(_fixture.WorkspaceB, _fixture.WorkspaceB, b.RecipeId);

        var textA = Normalised(TextOf(bytesA));
        var textB = Normalised(TextOf(bytesB));
        Assert.Contains("ALPHAintro.", textA);
        Assert.DoesNotContain("BRAVO", textA);
        Assert.Contains("BRAVOintro.", textB);
        Assert.DoesNotContain("ALPHA", textB);
    }

    [Fact]
    public async Task Naming_an_editorial_revision_another_workspace_accepted_is_not_found()
    {
        await SeedAsync(_fixture.WorkspaceA, editorialIntro: "ALPHA intro.");
        var b = await SeedAsync(_fixture.WorkspaceB);

        var (response, bytes) = await GetBytesAsync(_fixture.WorkspaceB, _fixture.WorkspaceB, b.RecipeId, "?editorialRevision=1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("content.revision.not_found", Problem(System.Text.Encoding.UTF8.GetString(bytes), "code"));
    }

    [Fact]
    public async Task Identical_recipes_in_two_workspaces_do_not_share_a_validator()
    {
        var a = await SeedAsync(_fixture.WorkspaceA);
        var b = await SeedAsync(_fixture.WorkspaceB);

        var (inA, _) = await OwnA(a.RecipeId);
        var (inB, _) = await GetBytesAsync(_fixture.WorkspaceB, _fixture.WorkspaceB, b.RecipeId);

        // Same title, lines and steps, and the same query: only the version identity differs, and it must
        // keep differing, or one workspace's validator would confirm the other's document.
        Assert.NotEqual(inA.Headers.ETag!.Tag, inB.Headers.ETag!.Tag);
    }
}
