using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The creative-context seam over HTTP, through the gateway, for two workspaces that share a name and nothing
/// else (AF.1.3).
/// </summary>
/// <remarks>
/// <para>
/// Sources are seeded straight into the database rather than through their own routes. What is under test is
/// whether this seam will name them, and a recipe created over HTTP would say nothing a seeded one does not
/// while making every test depend on four other controllers.
/// </para>
/// <para>
/// <strong>What SQLite cannot show here.</strong> It never moves a row version, so "two editors, the second is
/// refused" is not provable in this file: an edit followed by another with the same token still matches. What
/// is provable is the contract around a token that was never this row's, and
/// <see cref="CreativeContextSqlServerTests"/> proves the real thing against a real <c>rowversion</c>.
/// </para>
/// </remarks>
public sealed class CreativeContextsEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SeededWorkspace A => _fixture.WorkspaceA;

    private SeededWorkspace B => _fixture.WorkspaceB;

    private static string ContextsIn(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/creative-contexts";

    private static string ContextIn(SeededWorkspace workspace, Guid id) => $"{ContextsIn(workspace)}/{id}";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static string Token(JsonElement context) => context.GetProperty("concurrencyToken").GetString()!;

    private static Guid Id(JsonElement context) => context.GetProperty("id").GetGuid();

    private static JsonElement[] References(JsonElement context) =>
        [.. context.GetProperty("references").EnumerateArray()];

    private static string[] Channels(JsonElement context) =>
        [.. context.GetProperty("channelKeys").EnumerateArray().Select(key => key.GetString()!)];

    /// <summary>A token of the right shape that no row was ever issued.</summary>
    private static string NeverIssued => Convert.ToBase64String(new byte[CreativeContextConcurrencyToken.ByteLength]);

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static object Piece(string title = "Soda bread, autumn") => new
    {
        workingTitle = title,
        pictureBrief = "Overhead, the loaf torn open on linen, soft window light.",
        channelKeys = new[] { "blog", "instagram" },
        day = "Monday",
    };

    private async Task<JsonElement> CreatedAsync(GatewayClient client, SeededWorkspace workspace, object? body = null)
    {
        var response = await client.PostAsJsonAsync(ContextsIn(workspace), body ?? Piece(), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await BodyOf(response);
    }

    private static Task<HttpResponseMessage> AddAsync(
        GatewayClient client, SeededWorkspace workspace, JsonElement context, object reference, string? token = null) =>
        client.PostAsJsonAsync(
            $"{ContextIn(workspace, Id(context))}/references",
            new { expectedConcurrencyToken = token ?? Token(context), reference },
            Ct);

    private static Task<HttpResponseMessage> RemoveAsync(
        GatewayClient client, SeededWorkspace workspace, Guid contextId, Guid referenceId, string token) =>
        client.SendAsync(
            HttpMethod.Delete,
            $"{ContextIn(workspace, contextId)}/references/{referenceId}?expectedConcurrencyToken={Uri.EscapeDataString(token)}",
            body: null,
            headers: null,
            Ct);

    /// <summary>Runs against the API's own database as a member of that workspace.</summary>
    private async Task<T> InAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    private Task<int> CountAsync(SeededWorkspace workspace) =>
        InAsync(workspace, db => db.CreativeContexts.CountAsync(Ct));

    /// <summary>
    /// A recipe and its first version, created through the recipe route.
    /// </summary>
    /// <remarks>
    /// The one source that is not seeded directly: a pinned version is resolved through its snapshot, and only
    /// the recipe seam writes a version together with one.
    /// </remarks>
    private async Task<(Guid RecipeId, Guid VersionId)> CreateRecipeAsync(SeededWorkspace workspace)
    {
        using var client = await SignInAsync(workspace.OwnerEmail);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes", new { title = "Buttermilk Soda Bread" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyOf(response);

        return (body.GetProperty("recipeId").GetGuid(), body.GetProperty("versionId").GetGuid());
    }

    /// <summary>One usable source of every kind this seam accepts, in that workspace, as request bodies.</summary>
    private async Task<IReadOnlyList<(string Kind, object Reference)>> SourcesAsync(SeededWorkspace workspace)
    {
        var (recipeId, versionId) = await CreateRecipeAsync(workspace);
        var (requestId, conceptId) = await InAsync(workspace, db => CreativeContextSeeds.ConceptAsync(db, Now, Ct));
        var assetId = await InAsync(workspace, db => CreativeContextSeeds.MediaAssetAsync(db, workspace.Id, Now, Ct));
        var imageId = await InAsync(workspace, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));
        var promptId = await InAsync(workspace, db => CreativeContextSeeds.PromptRecordAsync(db, Now, Ct));

        return
        [
            ("Recipe", new { kind = "Recipe", recipeId, recipeVersionId = versionId }),
            ("RecipeConcept", new { kind = "RecipeConcept", conceptRequestId = requestId, conceptId }),
            ("DamAsset", new { kind = "DamAsset", mediaAssetId = assetId, mediaAssetVersionNumber = 1 }),
            ("GeneratedImage", new { kind = "GeneratedImage", generatedImageId = imageId }),
            ("PromptRecord", new { kind = "PromptRecord", promptRecordId = promptId }),
        ];
    }

    // ---- create and read ------------------------------------------------------------------------------

    [Fact]
    public async Task A_created_context_is_readable_at_its_location_header()
    {
        using var client = await SignInAsync(A.OwnerEmail);

        var response = await client.PostAsJsonAsync(ContextsIn(A), Piece(), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await BodyOf(response);

        var read = await client.GetAsync(response.Headers.Location!.ToString(), Ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var body = await BodyOf(read);
        Assert.Equal(Id(created), Id(body));
        Assert.Equal("Soda bread, autumn", body.GetProperty("workingTitle").GetString());
        Assert.Equal(["blog", "instagram"], Channels(body));
        Assert.Equal("Monday", body.GetProperty("day").GetString());
        Assert.Empty(References(body));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("archivedAt").ValueKind);
        Assert.Equal(Token(created), Token(body));
    }

    [Fact]
    public async Task A_context_may_start_as_nothing_at_all()
    {
        // A bare hand-off, before the creator has said anything about the piece.
        using var client = await SignInAsync(A.OwnerEmail);

        var body = await CreatedAsync(client, A, new { });

        Assert.Equal(JsonValueKind.Null, body.GetProperty("workingTitle").ValueKind);
        Assert.Empty(Channels(body));
    }

    [Fact]
    public async Task Blank_words_are_stored_as_absent_and_the_rest_is_kept_as_typed()
    {
        using var client = await SignInAsync(A.OwnerEmail);

        var body = await CreatedAsync(client, A, new
        {
            workingTitle = "   ",
            pictureBrief = "  The LOAF, torn open — don't tidy the crumbs.  ",
        });

        Assert.Equal(JsonValueKind.Null, body.GetProperty("workingTitle").ValueKind);

        // Trimmed at the ends and otherwise untouched: these are the creator's words.
        Assert.Equal("The LOAF, torn open — don't tidy the crumbs.", body.GetProperty("pictureBrief").GetString());
    }

    [Fact]
    public async Task The_response_names_no_workspace_and_no_author()
    {
        using var client = await SignInAsync(A.OwnerEmail);

        var body = await CreatedAsync(client, A);
        var names = body.EnumerateObject().Select(property => property.Name).ToList();

        Assert.DoesNotContain("workspaceId", names);
        Assert.DoesNotContain("createdByMembershipId", names);
        Assert.DoesNotContain("rowVersion", names);
    }

    [Fact]
    public async Task The_author_is_the_callers_membership_whatever_the_body_says()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var smuggled = Guid.NewGuid();

        var body = await CreatedAsync(client, A, new
        {
            workingTitle = "Whose is this",
            workspaceId = B.Id,
            createdByMembershipId = smuggled,
        });

        var stored = await InAsync(A, db => db.CreativeContexts.SingleAsync(context => context.Id == Id(body), Ct));
        Assert.Equal(A.Id, stored.WorkspaceId);
        Assert.NotEqual(smuggled, stored.CreatedByMembershipId);
        Assert.Equal(0, await CountAsync(B));
    }

    [Fact]
    public async Task Responses_are_not_cacheable()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var created = await CreatedAsync(client, A);

        foreach (var path in new[] { ContextsIn(A), ContextIn(A, Id(created)), ContextIn(A, Guid.NewGuid()) })
        {
            var response = await client.GetAsync(path, Ct);
            Assert.True(response.Headers.CacheControl is { NoStore: true }, path);
        }
    }

    [Fact]
    public async Task An_unknown_context_answers_404()
    {
        using var client = await SignInAsync(A.OwnerEmail);

        var response = await client.GetAsync(ContextIn(A, Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ContentErrorCodes.CreativeContextNotFound, Code(await BodyOf(response)));
    }

    // ---- shape ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_malformed_context_answers_400_with_the_fields_that_are_wrong()
    {
        using var client = await SignInAsync(A.OwnerEmail);

        var response = await client.PostAsJsonAsync(ContextsIn(A), new
        {
            workingTitle = new string('a', CreativeContextPolicy.WorkingTitleMaxLength + 1),
            channelKeys = new[] { "blog", "blog" },
            weeklyThemeKey = "Not A Key",
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(ContentErrorCodes.CreativeContextInvalid, Code(body));

        var errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("workingTitle", out _));
        Assert.True(errors.TryGetProperty("channelKeys[1]", out _));
        Assert.True(errors.TryGetProperty("weeklyThemeKey", out _));
        Assert.Equal(0, await CountAsync(A));
    }

    public static TheoryData<string, string> MisshapenReferences => new()
    {
        { """{ "kind": "Recipe" }""", "from.recipeId" },
        { """{ "kind": "RecipeConcept", "conceptId": "5b0f6a3e-2f0c-4d0f-9a35-3d5b6c1f0a11" }""", "from.conceptRequestId" },
        { """{ "kind": "GeneratedImage", "generatedImageId": "5b0f6a3e-2f0c-4d0f-9a35-3d5b6c1f0a11", "recipeId": "5b0f6a3e-2f0c-4d0f-9a35-3d5b6c1f0a12" }""", "from.recipeId" },
        { """{ "kind": "DamAsset", "mediaAssetId": "5b0f6a3e-2f0c-4d0f-9a35-3d5b6c1f0a11", "mediaAssetVersionNumber": 0 }""", "from.mediaAssetVersionNumber" },
        { """{ "recipeId": "5b0f6a3e-2f0c-4d0f-9a35-3d5b6c1f0a11" }""", "from.kind" },

        // No table to check one against until post packages exist, so the kind itself is refused.
        { """{ "kind": "SocialPackage" }""", "from.kind" },
    };

    [Theory]
    [MemberData(nameof(MisshapenReferences))]
    public async Task A_reference_carries_its_kinds_fields_and_no_others(string reference, string field)
    {
        using var client = await SignInAsync(A.OwnerEmail);

        var response = await client.PostAsJsonAsync(
            ContextsIn(A), new { from = JsonDocument.Parse(reference).RootElement }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(ContentErrorCodes.CreativeContextInvalid, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), body.GetRawText());
        Assert.Equal(0, await CountAsync(A));
    }

    // ---- references: each kind --------------------------------------------------------------------------

    [Fact]
    public async Task A_context_can_be_started_from_each_kind_of_source()
    {
        using var client = await SignInAsync(A.OwnerEmail);

        foreach (var (kind, reference) in await SourcesAsync(A))
        {
            var response = await client.PostAsJsonAsync(ContextsIn(A), new { from = reference }, Ct);

            Assert.True(response.StatusCode == HttpStatusCode.Created, $"{kind}: {await response.Content.ReadAsStringAsync(Ct)}");
            var only = Assert.Single(References(await BodyOf(response)));
            Assert.Equal(kind, only.GetProperty("kind").GetString());
            Assert.Equal(0, only.GetProperty("sortOrder").GetInt32());
        }
    }

    [Fact]
    public async Task Each_kind_of_source_can_be_added_in_order_and_is_published_as_ids_only()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var context = await CreatedAsync(client, A);

        foreach (var (kind, reference) in await SourcesAsync(A))
        {
            var response = await AddAsync(client, A, context, reference);
            Assert.True(response.StatusCode == HttpStatusCode.Created, $"{kind}: {await response.Content.ReadAsStringAsync(Ct)}");
            context = await BodyOf(response);
        }

        var references = References(context);
        Assert.Equal(
            ["Recipe", "RecipeConcept", "DamAsset", "GeneratedImage", "PromptRecord"],
            references.Select(reference => reference.GetProperty("kind").GetString()));
        Assert.Equal([0, 1, 2, 3, 4], references.Select(reference => reference.GetProperty("sortOrder").GetInt32()));

        // A reference points and never copies: nothing on it could hold a title, a description or a body.
        // `kind` and `purpose` are this row's own two facts about itself — what it points at, and what it is
        // for — and are enum names rather than anything copied from the source.
        Assert.All(references, reference => Assert.All(
            reference.EnumerateObject().Where(property => property.Name is not "kind" and not "purpose" and not "addedAt"),
            property => Assert.True(
                property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Number
                || Guid.TryParse(property.Value.GetString(), out _),
                property.Name)));
    }

    [Fact]
    public async Task A_recipe_and_an_asset_may_be_named_without_pinning_a_version()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var (recipeId, _) = await InAsync(A, db => CreativeContextSeeds.RecipeAsync(db, Now, Ct));
        var assetId = await InAsync(A, db => CreativeContextSeeds.MediaAssetAsync(db, A.Id, Now, Ct));

        var context = await CreatedAsync(client, A, new { from = new { kind = "Recipe", recipeId } });
        var added = await AddAsync(client, A, context, new { kind = "DamAsset", mediaAssetId = assetId });

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var references = References(await BodyOf(added));
        Assert.Equal(JsonValueKind.Null, references[0].GetProperty("recipeVersionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, references[1].GetProperty("mediaAssetVersionNumber").ValueKind);
    }

    // ---- references: refusals disclose nothing ----------------------------------------------------------

    [Fact]
    public async Task Another_workspaces_source_is_refused_exactly_as_an_unknown_one_is()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var context = await CreatedAsync(client, A);

        var theirs = await SourcesAsync(B);
        var unknown = new (string Kind, object Reference)[]
        {
            ("Recipe", new { kind = "Recipe", recipeId = Guid.NewGuid(), recipeVersionId = Guid.NewGuid() }),
            ("RecipeConcept", new { kind = "RecipeConcept", conceptRequestId = Guid.NewGuid(), conceptId = Guid.NewGuid() }),
            ("DamAsset", new { kind = "DamAsset", mediaAssetId = Guid.NewGuid(), mediaAssetVersionNumber = 1 }),
            ("GeneratedImage", new { kind = "GeneratedImage", generatedImageId = Guid.NewGuid() }),
            ("PromptRecord", new { kind = "PromptRecord", promptRecordId = Guid.NewGuid() }),
        };

        for (var index = 0; index < theirs.Count; index++)
        {
            var borrowed = await AddAsync(client, A, context, theirs[index].Reference);
            var missing = await AddAsync(client, A, context, unknown[index].Reference);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, borrowed.StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);

            // The same code, sentence and field errors: an id cannot be used to ask what a neighbour holds.
            Assert.Equal(
                WithoutTraceId(await BodyOf(missing)), WithoutTraceId(await BodyOf(borrowed)));
        }

        Assert.Empty(References(await BodyOf(await client.GetAsync(ContextIn(A, Id(context)), Ct))));
    }

    [Fact]
    public async Task Starting_from_another_workspaces_source_creates_nothing()
    {
        using var client = await SignInAsync(A.OwnerEmail);

        foreach (var (kind, reference) in await SourcesAsync(B))
        {
            var response = await client.PostAsJsonAsync(ContextsIn(A), new { from = reference }, Ct);

            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, kind);
            Assert.Equal(ContentErrorCodes.CreativeContextReferenceUnprocessable, Code(await BodyOf(response)));
        }

        Assert.Equal(0, await CountAsync(A));
    }

    [Fact]
    public async Task A_source_that_is_no_longer_usable_gets_the_same_refusal()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var context = await CreatedAsync(client, A);

        var (recipeId, _) = await InAsync(A, db => CreativeContextSeeds.RecipeAsync(db, Now, Ct));
        var assetId = await InAsync(A, db => CreativeContextSeeds.MediaAssetAsync(db, A.Id, Now, Ct));
        var imageId = await InAsync(A, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));

        // Each taken out of use the way its own module does it: none of them is a row delete.
        await InAsync(A, async db =>
        {
            (await db.Recipes.SingleAsync(recipe => recipe.Id == recipeId, Ct)).Status = RecipeStatus.Archived;

            var asset = await db.MediaAssets.SingleAsync(item => item.Id == assetId, Ct);
            asset.DeletedAt = Now;
            asset.DeletedByMembershipId = Guid.NewGuid();

            (await db.GeneratedImages.SingleAsync(image => image.Id == imageId, Ct)).Status = GeneratedImageStatus.Rejected;

            return await db.SaveChangesAsync(Ct);
        });

        var unknown = WithoutTraceId(await BodyOf(
            await AddAsync(client, A, context, new { kind = "GeneratedImage", generatedImageId = Guid.NewGuid() })));

        foreach (var reference in new object[]
        {
            new { kind = "Recipe", recipeId },
            new { kind = "DamAsset", mediaAssetId = assetId },
            new { kind = "GeneratedImage", generatedImageId = imageId },
        })
        {
            var response = await AddAsync(client, A, context, reference);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(unknown, WithoutTraceId(await BodyOf(response)));
        }
    }

    [Fact]
    public async Task A_version_of_some_other_record_is_not_a_version_of_this_one()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var context = await CreatedAsync(client, A);

        var (recipeId, _) = await CreateRecipeAsync(A);
        var (_, otherVersionId) = await CreateRecipeAsync(A);
        var assetId = await InAsync(A, db => CreativeContextSeeds.MediaAssetAsync(db, A.Id, Now, Ct));

        var wrongRecipeVersion = await AddAsync(
            client, A, context, new { kind = "Recipe", recipeId, recipeVersionId = otherVersionId });
        var missingAssetVersion = await AddAsync(
            client, A, context, new { kind = "DamAsset", mediaAssetId = assetId, mediaAssetVersionNumber = 2 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongRecipeVersion.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingAssetVersion.StatusCode);
    }

    [Fact]
    public async Task A_concept_must_be_one_its_request_actually_offered()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var context = await CreatedAsync(client, A);

        var (requestId, _) = await InAsync(A, db => CreativeContextSeeds.ConceptAsync(db, Now, Ct));

        // A real request of this workspace, and a concept id it never produced. The foreign key alone would
        // accept this: a concept is not a row, so only the Ai module can say it does not exist.
        var invented = await AddAsync(
            client, A, context, new { kind = "RecipeConcept", conceptRequestId = requestId, conceptId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, invented.StatusCode);
        Assert.Equal(ContentErrorCodes.CreativeContextReferenceUnprocessable, Code(await BodyOf(invented)));
    }

    [Fact]
    public async Task A_source_is_named_once_whichever_version_is_pinned()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var (recipeId, versionId) = await CreateRecipeAsync(A);

        var context = await CreatedAsync(client, A, new { from = new { kind = "Recipe", recipeId } });
        var again = await AddAsync(client, A, context, new { kind = "Recipe", recipeId, recipeVersionId = versionId });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(ContentErrorCodes.CreativeContextReferenceDuplicate, Code(await BodyOf(again)));

        // The same recipe on a second piece of work is not a duplicate of anything.
        var other = await client.PostAsJsonAsync(ContextsIn(A), new { from = new { kind = "Recipe", recipeId } }, Ct);
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    // ---- references: what each one is for (AF.4.3) ------------------------------------------------------

    [Fact]
    public async Task A_source_named_without_saying_what_it_is_for_is_something_the_work_draws_on()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var (recipeId, _) = await CreateRecipeAsync(A);

        var context = await CreatedAsync(client, A, new { from = new { kind = "Recipe", recipeId } });

        Assert.Equal("Source", Assert.Single(References(context)).GetProperty("purpose").GetString());
    }

    [Fact]
    public async Task A_picture_can_be_both_this_works_keeper_and_the_picture_it_takes_cues_from()
    {
        // The two facts are independent: a run makes a picture, and the next prompt may be planned around
        // that same picture. Naming it once would force one of them to overwrite the other, and the surface
        // that replaces a cue would then remove a keeper (AF.4.3).
        using var client = await SignInAsync(A.OwnerEmail);
        var imageId = await InAsync(A, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));
        var context = await CreatedAsync(client, A);

        var keeper = await AddAsync(
            client, A, context, new { kind = "GeneratedImage", generatedImageId = imageId, purpose = "Keeper" });
        Assert.Equal(HttpStatusCode.Created, keeper.StatusCode);
        context = await BodyOf(keeper);

        var cue = await AddAsync(client, A, context, new { kind = "GeneratedImage", generatedImageId = imageId });
        Assert.Equal(HttpStatusCode.Created, cue.StatusCode);

        Assert.Equal(["Keeper", "Source"], References(await BodyOf(cue)).Select(each => each.GetProperty("purpose").GetString()));
    }

    [Fact]
    public async Task One_picture_is_named_once_for_one_purpose()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var assetId = await InAsync(A, db => CreativeContextSeeds.MediaAssetAsync(db, A.Id, Now, Ct));
        var context = await CreatedAsync(client, A);

        var first = await AddAsync(
            client, A, context, new { kind = "DamAsset", mediaAssetId = assetId, purpose = "Keeper" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var again = await AddAsync(
            client, A, await BodyOf(first), new { kind = "DamAsset", mediaAssetId = assetId, purpose = "Keeper" });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(ContentErrorCodes.CreativeContextReferenceDuplicate, Code(await BodyOf(again)));
    }

    [Fact]
    public async Task A_recipe_is_named_once_however_it_is_used()
    {
        // Only a picture can honestly be both an output and a cue. A recipe this work is about does not
        // become a second source by being called something else.
        using var client = await SignInAsync(A.OwnerEmail);
        var (recipeId, _) = await CreateRecipeAsync(A);
        var context = await CreatedAsync(client, A, new { from = new { kind = "Recipe", recipeId } });

        var again = await AddAsync(client, A, context, new { kind = "Recipe", recipeId, purpose = "Keeper" });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task A_purpose_the_server_does_not_know_is_refused_rather_than_read_as_a_source()
    {
        // Defaulting it would store a keeper as the picture the next prompt is planned around.
        using var client = await SignInAsync(A.OwnerEmail);
        var imageId = await InAsync(A, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));
        var context = await CreatedAsync(client, A);

        var refused = await AddAsync(
            client, A, context, new { kind = "GeneratedImage", generatedImageId = imageId, purpose = 9 });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task A_context_names_a_bounded_number_of_sources()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var context = await CreatedAsync(client, A);

        for (var count = 0; count < CreativeContextPolicy.MaxReferences; count++)
        {
            var promptId = await InAsync(A, db => CreativeContextSeeds.PromptRecordAsync(db, Now, Ct));
            var added = await AddAsync(client, A, context, new { kind = "PromptRecord", promptRecordId = promptId });
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
            context = await BodyOf(added);
        }

        var onePast = await InAsync(A, db => CreativeContextSeeds.PromptRecordAsync(db, Now, Ct));
        var refused = await AddAsync(client, A, context, new { kind = "PromptRecord", promptRecordId = onePast });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(ContentErrorCodes.CreativeContextReferenceLimit, Code(await BodyOf(refused)));
    }

    [Fact]
    public async Task Removing_a_reference_leaves_the_source_and_the_order_of_the_rest()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var context = await CreatedAsync(client, A);
        var sources = await SourcesAsync(A);

        foreach (var (_, reference) in sources.Take(3))
        {
            context = await BodyOf(await AddAsync(client, A, context, reference));
        }

        var middle = References(context)[1];
        var response = await RemoveAsync(
            client, A, Id(context), middle.GetProperty("id").GetGuid(), Token(context));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var remaining = References(await BodyOf(response));
        Assert.Equal(["Recipe", "DamAsset"], remaining.Select(reference => reference.GetProperty("kind").GetString()));
        Assert.Equal([0, 2], remaining.Select(reference => reference.GetProperty("sortOrder").GetInt32()));

        // The usage went; the concept request it named is still there.
        var requestId = middle.GetProperty("conceptRequestId").GetGuid();
        Assert.True(await InAsync(A, db => db.AiOperations.AnyAsync(operation => operation.Id == requestId, Ct)));
    }

    [Fact]
    public async Task Removing_a_reference_that_is_not_there_answers_404()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var context = await CreatedAsync(client, A);

        var response = await RemoveAsync(client, A, Id(context), Guid.NewGuid(), Token(context));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ContentErrorCodes.CreativeContextNotFound, Code(await BodyOf(response)));
    }

    // ---- channels and themes ----------------------------------------------------------------------------

    [Fact]
    public async Task A_key_that_names_no_channel_answers_422_with_its_position()
    {
        using var client = await SignInAsync(A.OwnerEmail);

        var response = await client.PostAsJsonAsync(
            ContextsIn(A), new { channelKeys = new[] { "blog", "mastodon" } }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(ContentErrorCodes.CreativeContextChannelUnprocessable, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty("channelKeys[1]", out _));
        Assert.Equal(0, await CountAsync(A));
    }

    [Fact]
    public async Task A_theme_must_be_one_of_this_workspaces_live_themes()
    {
        using var ownerA = await SignInAsync(A.OwnerEmail);
        using var ownerB = await SignInAsync(B.OwnerEmail);

        // The theme exists — in the other workspace, under the same key a creator here might guess.
        var put = await ownerB.SendAsync(
            HttpMethod.Put,
            $"/api/v1/workspaces/{B.Slug}/weekly-themes",
            new { themes = new[] { new { day = "Monday", key = "meat-free-monday", displayName = "Meat-free Monday" } } },
            headers: null,
            Ct);
        Assert.True(put.IsSuccessStatusCode);

        var theirs = await ownerA.PostAsJsonAsync(ContextsIn(A), new { weeklyThemeKey = "meat-free-monday" }, Ct);
        var unknown = await ownerA.PostAsJsonAsync(ContextsIn(A), new { weeklyThemeKey = "never-a-theme" }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, theirs.StatusCode);
        var refusal = await BodyOf(theirs);
        Assert.Equal(ContentErrorCodes.CreativeContextThemeUnprocessable, Code(refusal));
        Assert.Equal(WithoutTraceId(await BodyOf(unknown)), WithoutTraceId(refusal));

        // And in the workspace that owns it, it is simply a theme.
        var mine = await ownerB.PostAsJsonAsync(ContextsIn(B), new { weeklyThemeKey = "meat-free-monday" }, Ct);
        Assert.Equal(HttpStatusCode.Created, mine.StatusCode);
    }

    [Fact]
    public async Task A_theme_retired_after_it_was_chosen_does_not_make_the_context_uneditable()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var themes = $"/api/v1/workspaces/{A.Slug}/weekly-themes";

        await client.SendAsync(
            HttpMethod.Put,
            themes,
            new { themes = new[] { new { day = "Monday", key = "meat-free-monday", displayName = "Meat-free Monday" } } },
            headers: null,
            Ct);

        var context = await CreatedAsync(client, A, new { weeklyThemeKey = "meat-free-monday" });

        // Replacing the week with nothing retires the theme; its key stays stored on the context.
        await client.SendAsync(HttpMethod.Put, themes, new { themes = Array.Empty<object>() }, headers: null, Ct);

        var edited = await client.PatchAsJsonAsync(
            ContextIn(A, Id(context)),
            new { expectedConcurrencyToken = Token(context), workingTitle = "Still mine to edit" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("meat-free-monday", (await BodyOf(edited)).GetProperty("weeklyThemeKey").GetString());

        // But it cannot be newly chosen.
        var fresh = await client.PostAsJsonAsync(ContextsIn(A), new { weeklyThemeKey = "meat-free-monday" }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fresh.StatusCode);
    }

    // ---- edit -------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_edit_changes_what_was_sent_clears_what_was_nulled_and_leaves_the_rest()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var context = await CreatedAsync(client, A);

        var response = await client.PatchAsJsonAsync(
            ContextIn(A, Id(context)),
            new
            {
                expectedConcurrencyToken = Token(context),
                workingTitle = "Soda bread, the reel",
                day = (string?)null,
                channelKeys = new[] { "instagram", "pinterest", "blog" },
            },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal("Soda bread, the reel", body.GetProperty("workingTitle").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("day").ValueKind);
        Assert.Equal(["instagram", "pinterest", "blog"], Channels(body));

        // Not sent, so not touched.
        Assert.Equal(
            "Overhead, the loaf torn open on linen, soft window light.",
            body.GetProperty("pictureBrief").GetString());

        // And it is what a fresh read returns, not only what the edit echoed.
        var read = await BodyOf(await client.GetAsync(ContextIn(A, Id(context)), Ct));
        Assert.Equal(["instagram", "pinterest", "blog"], Channels(read));
        Assert.Equal("Soda bread, the reel", read.GetProperty("workingTitle").GetString());
    }

    [Fact]
    public async Task A_new_context_has_no_brief_chosen()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var context = await CreatedAsync(client, A);

        Assert.Equal(JsonValueKind.Null, context.GetProperty("briefSource").ValueKind);
        Assert.Equal(JsonValueKind.Null, context.GetProperty("workingBrief").ValueKind);
    }

    [Theory]
    [InlineData("Description")]
    [InlineData("Idea")]
    [InlineData("Combined")]
    public async Task Choosing_a_brief_stores_the_choice_and_the_brief_and_never_touches_the_description(string source)
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var context = await CreatedAsync(client, A);

        var response = await client.PatchAsJsonAsync(
            ContextIn(A, Id(context)),
            new
            {
                expectedConcurrencyToken = Token(context),
                briefSource = source,
                workingBrief = "Overhead, the loaf torn open on linen.\n\nDevelop an Irish bake for Sunday.",
            },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // A fresh read, which is what a resumed run on another device gets.
        var read = await BodyOf(await client.GetAsync(ContextIn(A, Id(context)), Ct));
        Assert.Equal(source, read.GetProperty("briefSource").GetString());
        Assert.Equal(
            "Overhead, the loaf torn open on linen.\n\nDevelop an Irish bake for Sunday.",
            read.GetProperty("workingBrief").GetString());
        Assert.Equal(
            "Overhead, the loaf torn open on linen, soft window light.",
            read.GetProperty("pictureBrief").GetString());
    }

    [Fact]
    public async Task Editing_the_description_leaves_a_chosen_brief_as_it_was_and_a_choice_can_be_cleared()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var context = await CreatedAsync(client, A);
        var chosen = await BodyOf(await client.PatchAsJsonAsync(
            ContextIn(A, Id(context)),
            new { expectedConcurrencyToken = Token(context), briefSource = "Idea", workingBrief = "Develop an Irish bake." },
            Ct));

        var edited = await BodyOf(await client.PatchAsJsonAsync(
            ContextIn(A, Id(context)),
            new { expectedConcurrencyToken = Token(chosen), pictureBrief = "A wide shot of the table." },
            Ct));

        Assert.Equal("Idea", edited.GetProperty("briefSource").GetString());
        Assert.Equal("Develop an Irish bake.", edited.GetProperty("workingBrief").GetString());

        // Blank is stored as absent, as the other words are.
        var cleared = await BodyOf(await client.PatchAsJsonAsync(
            ContextIn(A, Id(context)),
            new { expectedConcurrencyToken = Token(edited), briefSource = (string?)null, workingBrief = "   " },
            Ct));

        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("briefSource").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("workingBrief").ValueKind);
        Assert.Equal("A wide shot of the table.", cleared.GetProperty("pictureBrief").GetString());
    }

    [Fact]
    public async Task A_brief_that_is_too_long_or_from_nowhere_answers_400_with_its_field()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var context = await CreatedAsync(client, A);

        var tooLong = await client.PatchAsJsonAsync(
            ContextIn(A, Id(context)),
            new
            {
                expectedConcurrencyToken = Token(context),
                workingBrief = new string('a', CreativeContextPolicy.WorkingBriefMaxLength + 1),
            },
            Ct);
        var noSuchSource = await client.PatchAsJsonAsync(
            ContextIn(A, Id(context)),
            new { expectedConcurrencyToken = Token(context), briefSource = 7 },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Contains("workingBrief", await tooLong.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, noSuchSource.StatusCode);

        var read = await BodyOf(await client.GetAsync(ContextIn(A, Id(context)), Ct));
        Assert.Equal(JsonValueKind.Null, read.GetProperty("workingBrief").ValueKind);
        Assert.Equal(JsonValueKind.Null, read.GetProperty("briefSource").ValueKind);
    }

    [Fact]
    public async Task Another_workspace_cannot_choose_this_contexts_brief()
    {
        using var owner = await SignInAsync(A.OwnerEmail);
        var context = await CreatedAsync(owner, A);
        using var other = await SignInAsync(B.OwnerEmail);

        var response = await other.PatchAsJsonAsync(
            ContextIn(B, Id(context)),
            new { expectedConcurrencyToken = Token(context), briefSource = "Idea", workingBrief = "Not theirs to set." },
            Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var read = await BodyOf(await owner.GetAsync(ContextIn(A, Id(context)), Ct));
        Assert.Equal(JsonValueKind.Null, read.GetProperty("workingBrief").ValueKind);
    }

    [Fact]
    public async Task An_edit_that_changes_nothing_writes_nothing()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var context = await CreatedAsync(client, A);

        var response = await client.PatchAsJsonAsync(
            ContextIn(A, Id(context)),
            new { expectedConcurrencyToken = Token(context), workingTitle = "Soda bread, autumn" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(context.GetProperty("updatedAt").GetString(), body.GetProperty("updatedAt").GetString());
    }

    [Fact]
    public async Task Archiving_takes_a_context_out_of_the_list_and_leaves_it_readable()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var context = await CreatedAsync(client, A);

        var archived = await client.PatchAsJsonAsync(
            ContextIn(A, Id(context)), new { expectedConcurrencyToken = Token(context), archived = true }, Ct);

        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var body = await BodyOf(archived);
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("archivedAt").ValueKind);

        Assert.Empty((await BodyOf(await client.GetAsync(ContextsIn(A), Ct))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ContextIn(A, Id(context)), Ct)).StatusCode);

        var restored = await client.PatchAsJsonAsync(
            ContextIn(A, Id(context)), new { expectedConcurrencyToken = Token(body), archived = false }, Ct);

        Assert.Equal(JsonValueKind.Null, (await BodyOf(restored)).GetProperty("archivedAt").ValueKind);
        Assert.Single((await BodyOf(await client.GetAsync(ContextsIn(A), Ct))).GetProperty("items").EnumerateArray());

        // Both moves are audited, with ids and counts and never the creator's words.
        var audit = await InAsync(A, db => db.AuditLogs
            .Where(entry => entry.ResourceId == Id(context).ToString("D"))
            .Select(entry => new { entry.Action, entry.Summary })
            .ToListAsync(Ct));

        Assert.Equal(
            [
                ContentAuditActions.CreativeContextArchived,
                ContentAuditActions.CreativeContextCreated,
                ContentAuditActions.CreativeContextRestored,
            ],
            audit.Select(entry => entry.Action).Order());
        Assert.All(audit, entry => Assert.DoesNotContain("Soda bread", entry.Summary));
    }

    // ---- stale tokens -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_token_that_is_not_this_contexts_is_refused_on_every_write_and_nothing_is_written()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var imageId = await InAsync(A, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));
        var context = await CreatedAsync(
            client, A, new { workingTitle = "Untouched", from = new { kind = "GeneratedImage", generatedImageId = imageId } });
        var promptId = await InAsync(A, db => CreativeContextSeeds.PromptRecordAsync(db, Now, Ct));

        var responses = new[]
        {
            await client.PatchAsJsonAsync(
                ContextIn(A, Id(context)),
                new { expectedConcurrencyToken = NeverIssued, workingTitle = "Overwritten" },
                Ct),
            await AddAsync(client, A, context, new { kind = "PromptRecord", promptRecordId = promptId }, NeverIssued),
            await RemoveAsync(
                client, A, Id(context), References(context)[0].GetProperty("id").GetGuid(), NeverIssued),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(ContentErrorCodes.CreativeContextStale, Code(await BodyOf(response)));
        }

        // Nothing was written, which is what separates a refusal from a failed write.
        var read = await BodyOf(await client.GetAsync(ContextIn(A, Id(context)), Ct));
        Assert.Equal("Untouched", read.GetProperty("workingTitle").GetString());
        Assert.Single(References(read));
        Assert.Equal(Token(context), Token(read));
    }

    [Fact]
    public async Task Another_contexts_token_cannot_be_spent_on_this_one()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var mine = await CreatedAsync(client, A, Piece("Mine"));
        var other = await CreatedAsync(client, A, Piece("Other"));

        var response = await client.PatchAsJsonAsync(
            ContextIn(A, Id(mine)), new { expectedConcurrencyToken = Token(other), workingTitle = "Overwritten" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!")]
    [InlineData("AAAA")]
    public async Task A_token_that_could_never_have_been_issued_is_a_400_not_a_conflict(string? token)
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var context = await CreatedAsync(client, A);

        var response = await client.PatchAsJsonAsync(
            ContextIn(A, Id(context)), new { expectedConcurrencyToken = token, workingTitle = "Overwritten" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(ContentErrorCodes.CreativeContextInvalid, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty("expectedConcurrencyToken", out _));
    }

    // ---- idempotent create ------------------------------------------------------------------------------

    [Fact]
    public async Task Replaying_a_create_returns_the_first_context_and_makes_no_second_one()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var key = Guid.NewGuid().ToString();

        var first = await client.PostAsJsonAsync(ContextsIn(A), Piece(), key, Ct);
        var replay = await client.PostAsJsonAsync(ContextsIn(A), Piece(), key, Ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(Id(await BodyOf(first)), Id(await BodyOf(replay)));
        Assert.True(replay.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.False(first.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal(1, await CountAsync(A));
    }

    [Fact]
    public async Task A_key_names_one_request()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var key = Guid.NewGuid().ToString();

        await client.PostAsJsonAsync(ContextsIn(A), Piece("First"), key, Ct);
        var different = await client.PostAsJsonAsync(ContextsIn(A), Piece("Second"), key, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, different.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, Code(await BodyOf(different)));
        Assert.Equal(1, await CountAsync(A));
    }

    [Fact]
    public async Task The_same_key_in_another_workspace_is_another_request()
    {
        using var ownerA = await SignInAsync(A.OwnerEmail);
        using var ownerB = await SignInAsync(B.OwnerEmail);
        var key = Guid.NewGuid().ToString();

        var mine = await BodyOf(await ownerA.PostAsJsonAsync(ContextsIn(A), Piece(), key, Ct));
        var theirs = await ownerB.PostAsJsonAsync(ContextsIn(B), Piece(), key, Ct);

        Assert.Equal(HttpStatusCode.Created, theirs.StatusCode);
        Assert.False(theirs.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.NotEqual(Id(mine), Id(await BodyOf(theirs)));
    }

    [Fact]
    public async Task A_refused_create_is_not_remembered()
    {
        // The source is not there the first time and is the second: a retry under the same key must run again
        // rather than replay the refusal.
        using var client = await SignInAsync(A.OwnerEmail);
        var key = Guid.NewGuid().ToString();
        var promptId = Guid.NewGuid();
        var body = new { from = new { kind = "PromptRecord", promptRecordId = promptId } };

        var refused = await client.PostAsJsonAsync(ContextsIn(A), body, key, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        await InAsync(A, async db =>
        {
            db.PromptRecords.Add(new PromptRecord
            {
                Id = promptId,
                ChannelKey = "instagram",
                ImageKind = PromptImageKind.Hero,
                Text = "Overhead shot of soda bread.",
                Source = PromptRecordSource.Manual,
                CreatedByMembershipId = Guid.NewGuid(),
                CreatedAt = Now,
            });

            return await db.SaveChangesAsync(Ct);
        });

        var retried = await client.PostAsJsonAsync(ContextsIn(A), body, key, Ct);
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
    }

    // ---- roles ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_contributing_member_may_write()
    {
        // Workspace A's second member is an Editor, which is above Contributor.
        using var client = await SignInAsync(A.MemberEmail);

        var context = await CreatedAsync(client, A);
        var edited = await client.PatchAsJsonAsync(
            ContextIn(A, Id(context)), new { expectedConcurrencyToken = Token(context), workingTitle = "Edited" }, Ct);

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
    }

    [Fact]
    public async Task A_viewer_may_read_and_may_not_write()
    {
        using var owner = await SignInAsync(B.OwnerEmail);
        var imageId = await InAsync(B, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));
        var context = await CreatedAsync(
            owner, B, new { workingTitle = "Owner's", from = new { kind = "GeneratedImage", generatedImageId = imageId } });

        // Workspace B's second member is a Viewer, which is below Contributor.
        using var viewer = await SignInAsync(B.MemberEmail);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(ContextsIn(B), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(ContextIn(B, Id(context)), Ct)).StatusCode);

        var writes = new[]
        {
            await viewer.PostAsJsonAsync(ContextsIn(B), Piece(), Ct),
            await viewer.PatchAsJsonAsync(
                ContextIn(B, Id(context)), new { expectedConcurrencyToken = Token(context), workingTitle = "Viewer's" }, Ct),
            await AddAsync(viewer, B, context, new { kind = "GeneratedImage", generatedImageId = imageId }),
            await RemoveAsync(viewer, B, Id(context), References(context)[0].GetProperty("id").GetGuid(), Token(context)),
        };

        Assert.All(writes, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));

        var read = await BodyOf(await owner.GetAsync(ContextIn(B, Id(context)), Ct));
        Assert.Equal("Owner's", read.GetProperty("workingTitle").GetString());
        Assert.Single(References(read));
        Assert.Equal(1, await CountAsync(B));
    }

    // ---- list -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_is_most_recently_updated_first_and_carries_summaries()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var imageId = await InAsync(A, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));

        var first = await CreatedAsync(client, A, Piece("First"));
        await CreatedAsync(client, A, Piece("Second"));

        // Touching the older one moves it to the front.
        await AddAsync(client, A, first, new { kind = "GeneratedImage", generatedImageId = imageId });

        var items = (await BodyOf(await client.GetAsync(ContextsIn(A), Ct))).GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(["First", "Second"], items.Select(item => item.GetProperty("workingTitle").GetString()));
        Assert.Equal(1, items[0].GetProperty("referenceCount").GetInt32());
        Assert.Equal(0, items[1].GetProperty("referenceCount").GetInt32());
        Assert.Equal(["blog", "instagram"], items[0].GetProperty("channelKeys").EnumerateArray().Select(key => key.GetString()));

        // A summary is for finding a piece, not reading it.
        Assert.False(items[0].TryGetProperty("pictureBrief", out _));
    }

    [Fact]
    public async Task The_list_pages_by_cursor_without_repeating_or_skipping()
    {
        using var client = await SignInAsync(A.OwnerEmail);

        foreach (var index in Enumerable.Range(0, 5))
        {
            await CreatedAsync(client, A, Piece($"Piece {index}"));
        }

        var seen = new List<string>();
        string? cursor = null;

        do
        {
            var query = cursor is null ? "?limit=2" : $"?limit=2&cursor={Uri.EscapeDataString(cursor)}";
            var page = await BodyOf(await client.GetAsync(ContextsIn(A) + query, Ct));

            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("workingTitle").GetString()!));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public async Task A_cursor_from_another_workspace_or_from_nowhere_is_refused()
    {
        using var ownerA = await SignInAsync(A.OwnerEmail);
        using var ownerB = await SignInAsync(B.OwnerEmail);

        foreach (var index in Enumerable.Range(0, 3))
        {
            await CreatedAsync(ownerA, A, Piece($"Mine {index}"));
            await CreatedAsync(ownerB, B, Piece($"Theirs {index}"));
        }

        var cursor = (await BodyOf(await ownerA.GetAsync(ContextsIn(A) + "?limit=2", Ct)))
            .GetProperty("nextCursor").GetString()!;

        var replayed = await ownerB.GetAsync($"{ContextsIn(B)}?cursor={Uri.EscapeDataString(cursor)}", Ct);
        var invented = await ownerA.GetAsync($"{ContextsIn(A)}?cursor=not-a-cursor%21", Ct);

        foreach (var response in new[] { replayed, invented })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(ContentErrorCodes.CreativeContextCursorInvalid, Code(await BodyOf(response)));
        }
    }

    // ---- two-workspace isolation ------------------------------------------------------------------------

    [Fact]
    public async Task Each_workspace_lists_only_its_own_contexts()
    {
        using var ownerA = await SignInAsync(A.OwnerEmail);
        using var ownerB = await SignInAsync(B.OwnerEmail);

        var mine = await CreatedAsync(ownerA, A, Piece("Mine"));
        var theirs = await CreatedAsync(ownerB, B, Piece("Theirs"));

        var listA = (await BodyOf(await ownerA.GetAsync(ContextsIn(A), Ct))).GetProperty("items").EnumerateArray().ToList();
        var listB = (await BodyOf(await ownerB.GetAsync(ContextsIn(B), Ct))).GetProperty("items").EnumerateArray().ToList();

        Assert.Equal([Id(mine)], listA.Select(Id));
        Assert.Equal([Id(theirs)], listB.Select(Id));
    }

    [Fact]
    public async Task Another_workspaces_context_cannot_be_read_edited_added_to_or_removed_from()
    {
        using var ownerA = await SignInAsync(A.OwnerEmail);
        using var ownerB = await SignInAsync(B.OwnerEmail);

        var imageB = await InAsync(B, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));
        var theirs = await CreatedAsync(
            ownerB, B, new { workingTitle = "Theirs", from = new { kind = "GeneratedImage", generatedImageId = imageB } });
        var theirReference = References(theirs)[0].GetProperty("id").GetGuid();
        var imageA = await InAsync(A, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));

        // B's context id, B's real token, addressed through A's route by A's owner.
        var attempts = new[]
        {
            await ownerA.GetAsync(ContextIn(A, Id(theirs)), Ct),
            await ownerA.PatchAsJsonAsync(
                ContextIn(A, Id(theirs)), new { expectedConcurrencyToken = Token(theirs), workingTitle = "Taken" }, Ct),
            await ownerA.PostAsJsonAsync(
                $"{ContextIn(A, Id(theirs))}/references",
                new { expectedConcurrencyToken = Token(theirs), reference = new { kind = "GeneratedImage", generatedImageId = imageA } },
                Ct),
            await RemoveAsync(ownerA, A, Id(theirs), theirReference, Token(theirs)),
        };

        var unknown = WithoutTraceId(await BodyOf(await ownerA.GetAsync(ContextIn(A, Guid.NewGuid()), Ct)));

        foreach (var response in attempts)
        {
            // 404 and never the 409 a stale token would earn on a context the caller can see, with the body an
            // unknown id gets.
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(unknown, WithoutTraceId(await BodyOf(response)));
        }

        var read = await BodyOf(await ownerB.GetAsync(ContextIn(B, Id(theirs)), Ct));
        Assert.Equal("Theirs", read.GetProperty("workingTitle").GetString());
        Assert.Equal(theirReference, Assert.Single(References(read)).GetProperty("id").GetGuid());
        Assert.Equal(Token(theirs), Token(read));
    }

    [Fact]
    public async Task A_member_of_one_workspace_cannot_reach_the_others_route_at_all()
    {
        using var ownerA = await SignInAsync(A.OwnerEmail);
        using var ownerB = await SignInAsync(B.OwnerEmail);
        var theirs = await CreatedAsync(ownerB, B);

        var responses = new[]
        {
            await ownerA.GetAsync(ContextsIn(B), Ct),
            await ownerA.GetAsync(ContextIn(B, Id(theirs)), Ct),
            await ownerA.PostAsJsonAsync(ContextsIn(B), Piece(), Ct),
            await ownerA.PatchAsJsonAsync(
                ContextIn(B, Id(theirs)), new { expectedConcurrencyToken = Token(theirs), workingTitle = "Taken" }, Ct),
        };

        // An inaccessible workspace is indistinguishable from one that does not exist (tenancy.md).
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.Equal(1, await CountAsync(B));
    }

    [Fact]
    public async Task Half_a_source_from_another_workspace_is_still_not_a_source()
    {
        // Each pair is one real id of this workspace and one real id of the other: the recipe is A's and the
        // version is B's; the concept request is A's and the concept is B's.
        using var client = await SignInAsync(A.OwnerEmail);
        var context = await CreatedAsync(client, A);

        var (recipeId, _) = await CreateRecipeAsync(A);
        var (_, theirVersionId) = await CreateRecipeAsync(B);
        var (requestId, _) = await InAsync(A, db => CreativeContextSeeds.ConceptAsync(db, Now, Ct));
        var (_, theirConceptId) = await InAsync(B, db => CreativeContextSeeds.ConceptAsync(db, Now, Ct));

        var unknownVersion = WithoutTraceId(await BodyOf(await AddAsync(
            client, A, context, new { kind = "Recipe", recipeId, recipeVersionId = Guid.NewGuid() })));

        var borrowedVersion = await AddAsync(
            client, A, context, new { kind = "Recipe", recipeId, recipeVersionId = theirVersionId });
        var borrowedConcept = await AddAsync(
            client, A, context, new { kind = "RecipeConcept", conceptRequestId = requestId, conceptId = theirConceptId });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, borrowedVersion.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, borrowedConcept.StatusCode);
        Assert.Equal(unknownVersion, WithoutTraceId(await BodyOf(borrowedVersion)));
        Assert.Equal(unknownVersion, WithoutTraceId(await BodyOf(borrowedConcept)));
    }

    [Fact]
    public async Task Another_workspaces_source_is_a_422_even_beside_a_source_of_the_same_kind()
    {
        // The duplicate check runs before the source is resolved. It compares against this context's own
        // references only, so a neighbour's recipe can never match one and never earns the 409.
        using var client = await SignInAsync(A.OwnerEmail);
        var (mine, _) = await CreateRecipeAsync(A);
        var (theirs, _) = await CreateRecipeAsync(B);
        var context = await CreatedAsync(client, A, new { from = new { kind = "Recipe", recipeId = mine } });

        var response = await AddAsync(client, A, context, new { kind = "Recipe", recipeId = theirs });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Another_workspaces_picture_cannot_be_kept_on_your_own_work()
    {
        // The purpose changes nothing about who may be named (AF.4.3): a neighbour's picture is refused as a
        // keeper exactly as it is as a source, and with the same answer, so neither discloses the other.
        using var client = await SignInAsync(A.OwnerEmail);
        var mine = await InAsync(A, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));
        var theirs = await InAsync(B, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));
        var context = await CreatedAsync(client, A);

        var kept = await AddAsync(
            client, A, context, new { kind = "GeneratedImage", generatedImageId = mine, purpose = "Keeper" });
        Assert.Equal(HttpStatusCode.Created, kept.StatusCode);
        // Read once: the response's stream is consumed by the first read.
        var keptBody = await BodyOf(kept);

        var borrowed = await AddAsync(
            client, A, keptBody, new { kind = "GeneratedImage", generatedImageId = theirs, purpose = "Keeper" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, borrowed.StatusCode);
        Assert.Equal(ContentErrorCodes.CreativeContextReferenceUnprocessable, Code(await BodyOf(borrowed)));

        // And nothing of B's is on A's work.
        Assert.Equal(
            [mine.ToString()],
            References(keptBody).Select(each => each.GetProperty("generatedImageId").GetString()));
    }

    [Fact]
    public async Task Another_workspaces_reference_id_cannot_be_removed_through_your_own_context()
    {
        using var ownerA = await SignInAsync(A.OwnerEmail);
        using var ownerB = await SignInAsync(B.OwnerEmail);

        var imageB = await InAsync(B, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));
        var theirs = await CreatedAsync(ownerB, B, new { from = new { kind = "GeneratedImage", generatedImageId = imageB } });
        var theirReference = References(theirs)[0].GetProperty("id").GetGuid();
        var mine = await CreatedAsync(ownerA, A);

        // A's own context and A's own valid token, naming a reference that exists — on B's context.
        var response = await RemoveAsync(ownerA, A, Id(mine), theirReference, Token(mine));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(References(await BodyOf(await ownerB.GetAsync(ContextIn(B, Id(theirs)), Ct))));
    }

    [Fact]
    public async Task The_reference_routes_of_another_workspace_are_not_reachable_either()
    {
        using var ownerA = await SignInAsync(A.OwnerEmail);
        using var ownerB = await SignInAsync(B.OwnerEmail);

        var imageB = await InAsync(B, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));
        var promptB = await InAsync(B, db => CreativeContextSeeds.PromptRecordAsync(db, Now, Ct));
        var theirs = await CreatedAsync(ownerB, B, new { from = new { kind = "GeneratedImage", generatedImageId = imageB } });

        var responses = new[]
        {
            await AddAsync(ownerA, B, theirs, new { kind = "PromptRecord", promptRecordId = promptB }),
            await RemoveAsync(ownerA, B, Id(theirs), References(theirs)[0].GetProperty("id").GetGuid(), Token(theirs)),
            await ownerA.PostAsJsonAsync(ContextsIn(B), Piece(), Guid.NewGuid().ToString(), Ct),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.Single(References(await BodyOf(await ownerB.GetAsync(ContextIn(B, Id(theirs)), Ct))));
        Assert.Equal(1, await CountAsync(B));
    }

    [Fact]
    public async Task Paging_one_workspaces_list_never_surfaces_the_others_or_an_archived_context()
    {
        using var ownerA = await SignInAsync(A.OwnerEmail);
        using var ownerB = await SignInAsync(B.OwnerEmail);
        var mine = new List<Guid>();

        // Interleaved, so the two workspaces' rows sit next to each other in update order.
        foreach (var index in Enumerable.Range(0, 4))
        {
            mine.Add(Id(await CreatedAsync(ownerA, A, Piece($"Mine {index}"))));
            await CreatedAsync(ownerB, B, Piece($"Theirs {index}"));
        }

        foreach (var (client, workspace) in new[] { (ownerA, A), (ownerB, B) })
        {
            var archived = await CreatedAsync(client, workspace, Piece("Archived"));
            await client.PatchAsJsonAsync(
                ContextIn(workspace, Id(archived)), new { expectedConcurrencyToken = Token(archived), archived = true }, Ct);
        }

        var seen = new List<Guid>();
        string? cursor = null;

        do
        {
            var query = cursor is null ? "?limit=2" : $"?limit=2&cursor={Uri.EscapeDataString(cursor)}";
            var page = await BodyOf(await ownerA.GetAsync(ContextsIn(A) + query, Ct));

            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(Id));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(mine.Order(), seen.Order());
    }

    [Fact]
    public async Task A_reference_publishes_no_field_for_a_kind_this_seam_refuses()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var imageId = await InAsync(A, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));

        var context = await CreatedAsync(client, A, new { from = new { kind = "GeneratedImage", generatedImageId = imageId } });

        Assert.False(References(context)[0].TryGetProperty("socialPackageId", out _));
    }

    /// <summary>A problem body with the per-request ids removed, so two refusals can be compared whole.</summary>
    private static string WithoutTraceId(JsonElement body) =>
        JsonSerializer.Serialize(body.EnumerateObject()
            .Where(property => property.Name is not "traceId" and not "correlationId" and not "instance")
            .ToDictionary(property => property.Name, property => property.Value.GetRawText()));
}
