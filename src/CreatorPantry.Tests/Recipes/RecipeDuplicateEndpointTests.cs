using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>POST /api/v1/workspaces/{slug}/recipes/{recipeId}/duplicate</c> through the real Gateway — real cookie
/// session, real gateway-signed internal token, real API — for what a copy is, who may make one, what it
/// refuses, and what the other workspace can see.
/// </summary>
/// <remarks>
/// <para>
/// Asset links are the one part of the approved policy these tests cannot reach: no route creates one yet,
/// so every recipe here has none. <c>RecipeSnapshotDuplicatorTests</c> covers the copying, and this file
/// covers everything the wire can actually produce.
/// </para>
/// <para>
/// Bodies are anonymous objects on purpose, so what is on the wire is what the test wrote.
/// </para>
/// </remarks>
public sealed class RecipeDuplicateEndpointTests : IAsyncLifetime
{
    /// <summary>
    /// The role that proves copying is not gated like restoring: the fixture seeds Editor in A, and a
    /// Contributor has to be added to show the bar is one step lower.
    /// </summary>
    private const string ContributorEmail = "duplicate-contributor-a@example.com";

    private const string Password = "correct horse battery";

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

        await AddMemberAsync(ContributorEmail, WorkspaceRole.Contributor, _fixture.WorkspaceA.Id);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string RecipeIn(SeededWorkspace workspace, Guid recipeId) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}";

    private static string DuplicateIn(SeededWorkspace workspace, Guid recipeId) =>
        $"{RecipeIn(workspace, recipeId)}/duplicate";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    /// <summary>A recipe with content worth copying: tags, a headnote and a two-step method.</summary>
    private async Task<(Guid RecipeId, string Token, JsonElement Detail)> SeedAsync(
        GatewayClient client, SeededWorkspace workspace, object? body = null)
    {
        var cancellation = TestContext.Current.CancellationToken;

        var created = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes",
            body ?? new
            {
                title = "Olive oil cake",
                headnote = "The one my grandmother made.",
                attributionText = "Adapted from my grandmother's card.",
                prepTimeMinutes = 20,
                yieldText = "makes 12 muffins",
                status = "Ready",
                tags = new[] { "weeknight" },
                instructions = new object[]
                {
                    new
                    {
                        title = "Batter",
                        steps = new object[] { new { text = "Whisk the eggs." }, new { text = "Fold in the flour." } },
                    },
                },
            },
            cancellation);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var detail = await BodyOf(await client.GetAsync(created.Headers.Location!.ToString(), cancellation));

        return (detail.GetProperty("id").GetGuid(), detail.GetProperty("concurrencyToken").GetString()!, detail);
    }

    /// <summary>Every stable child id a recipe detail carries, for proving two recipes share none.</summary>
    private static IReadOnlyList<Guid> ChildIds(JsonElement detail) =>
    [
        .. detail.GetProperty("instructionGroups").EnumerateArray()
            .SelectMany(group => group.GetProperty("steps").EnumerateArray()
                .Select(step => step.GetProperty("id").GetGuid())
                .Append(group.GetProperty("id").GetGuid())),
        .. detail.GetProperty("ingredientGroups").EnumerateArray()
            .Select(group => group.GetProperty("id").GetGuid()),
    ];

    // ---- What a copy is ----

    [Fact]
    public async Task A_duplicate_creates_a_new_recipe_from_the_source_content()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, source) = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId),
            new { title = "Olive oil and rosemary cake" },
            cancellation);
        var created = await BodyOf(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Olive oil and rosemary cake", created.GetProperty("title").GetString());
        Assert.Equal(1, created.GetProperty("versionNumber").GetInt32());

        // The location points at the copy, never at the recipe in the route.
        var copyId = created.GetProperty("recipeId").GetGuid();
        Assert.NotEqual(recipeId, copyId);
        Assert.EndsWith(copyId.ToString("D"), response.Headers.Location!.ToString(), StringComparison.Ordinal);

        var copy = await BodyOf(await client.GetAsync(response.Headers.Location!.ToString(), cancellation));

        // Everything the creator wrote came across.
        Assert.Equal("The one my grandmother made.", copy.GetProperty("headnote").GetString());
        Assert.Equal("Adapted from my grandmother's card.", copy.GetProperty("attributionText").GetString());
        Assert.Equal(20, copy.GetProperty("prepTimeMinutes").GetInt32());
        Assert.Equal("makes 12 muffins", copy.GetProperty("yieldText").GetString());
        Assert.Equal("weeknight", Assert.Single(copy.GetProperty("tags").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(
            ["Whisk the eggs.", "Fold in the flour."],
            copy.GetProperty("instructionGroups").EnumerateArray()
                .Single().GetProperty("steps").EnumerateArray()
                .Select(step => step.GetProperty("text").GetString()));

        // And the title is the only thing that differs from the source's own content.
        Assert.Equal("Olive oil cake", source.GetProperty("title").GetString());
    }

    /// <summary>
    /// Whatever the source said — here a <c>Ready</c> recipe. A copy exists to be taken somewhere else, so it
    /// starts as a draft.
    /// </summary>
    [Fact]
    public async Task A_copy_is_a_draft_even_when_its_source_was_ready()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, source) = await SeedAsync(client, _fixture.WorkspaceA);

        Assert.Equal("Ready", source.GetProperty("status").GetString());

        var created = await BodyOf(await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), new { title = "A copy" }, cancellation));

        Assert.Equal("Draft", created.GetProperty("status").GetString());
    }

    /// <summary>And a copy of an archived recipe is not archived, or the creator would have to go and find it.</summary>
    [Fact]
    public async Task A_copy_of_an_archived_recipe_is_a_draft()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var archived = await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/archive",
            new { expectedConcurrencyToken = token },
            cancellation);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        // Copying an archived recipe is permitted, and deliberately so: it is the ordinary way to pick
        // shelved work back up, and refusing it would make the archive a place content goes to become
        // unreachable. Only changes to the archived recipe itself are frozen.
        var created = await BodyOf(await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), new { title = "A copy" }, cancellation));

        Assert.Equal("Draft", created.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_copy_shares_no_child_identity_with_its_source()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, source) = await SeedAsync(client, _fixture.WorkspaceA);

        var location = (await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), new { title = "A copy" }, cancellation))
            .Headers.Location!.ToString();
        var copy = await BodyOf(await client.GetAsync(location, cancellation));

        Assert.NotEmpty(ChildIds(source));
        Assert.Empty(ChildIds(source).Intersect(ChildIds(copy)));
    }

    /// <summary>
    /// The attribution the requirement asks for: enough to name the source and navigate to it, published on
    /// the copy and absent from a recipe someone wrote.
    /// </summary>
    [Fact]
    public async Task A_copy_records_the_recipe_and_version_it_came_from()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, source) = await SeedAsync(client, _fixture.WorkspaceA);

        Assert.Equal(JsonValueKind.Null, source.GetProperty("duplicatedFrom").ValueKind);

        var location = (await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), new { title = "A copy" }, cancellation))
            .Headers.Location!.ToString();
        var copy = await BodyOf(await client.GetAsync(location, cancellation));

        var lineage = copy.GetProperty("duplicatedFrom");
        Assert.Equal(recipeId, lineage.GetProperty("recipeId").GetGuid());
        Assert.Equal("Olive oil cake", lineage.GetProperty("recipeTitle").GetString());
        Assert.Equal(1, lineage.GetProperty("versionNumber").GetInt32());
        Assert.Equal(
            source.GetProperty("currentVersion").GetProperty("id").GetGuid(),
            lineage.GetProperty("versionId").GetGuid());

        // The source recipe id is navigable, which is the whole reason the object carries more than an id.
        var followed = await client.GetAsync(
            RecipeIn(_fixture.WorkspaceA, lineage.GetProperty("recipeId").GetGuid()), cancellation);

        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
    }

    /// <summary>
    /// The source's title as it stands now, not as it was when the copy was made. A creator renaming a recipe
    /// expects everything pointing at it to follow, and a frozen name would leave a banner naming a recipe
    /// that no longer exists by that name.
    /// </summary>
    [Fact]
    public async Task The_lineage_follows_the_sources_current_title()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var created = await BodyOf(await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), new { title = "A copy" }, cancellation));
        var copyId = created.GetProperty("recipeId").GetGuid();

        await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, title = "Renamed original" },
            cancellation);

        var copy = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, copyId), cancellation));

        Assert.Equal("Renamed original", copy.GetProperty("duplicatedFrom").GetProperty("recipeTitle").GetString());
    }

    /// <summary>
    /// The lineage survives writing to the copy. Every seam that answers with a recipe detail resolves it, so
    /// a <c>PATCH</c> response cannot contradict the <c>GET</c> that follows it.
    /// </summary>
    [Fact]
    public async Task The_lineage_is_carried_by_every_response_that_returns_the_copy()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var location = (await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), new { title = "A copy" }, cancellation))
            .Headers.Location!.ToString();
        var copy = await BodyOf(await client.GetAsync(location, cancellation));
        var copyId = copy.GetProperty("id").GetGuid();

        // The edit response.
        var edited = await BodyOf(await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, copyId),
            new { expectedConcurrencyToken = copy.GetProperty("concurrencyToken").GetString(), headnote = "Mine now." },
            cancellation));

        Assert.Equal(recipeId, edited.GetProperty("duplicatedFrom").GetProperty("recipeId").GetGuid());

        // And the restore response, which rebuilds the detail from a different path again.
        var restored = await BodyOf(await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, copyId)}/versions/1/restore",
            new { expectedConcurrencyToken = edited.GetProperty("concurrencyToken").GetString() },
            cancellation));

        Assert.Equal(recipeId, restored.GetProperty("duplicatedFrom").GetProperty("recipeId").GetGuid());
    }

    /// <summary>
    /// The copy's history begins at 1 and says a duplication produced it — not that a creator typed it.
    /// </summary>
    [Fact]
    public async Task A_copys_history_begins_at_one_and_says_it_was_duplicated()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var created = await BodyOf(await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), new { title = "A copy" }, cancellation));
        var copyId = created.GetProperty("recipeId").GetGuid();

        var history = await BodyOf(await client.GetAsync(
            $"{RecipeIn(_fixture.WorkspaceA, copyId)}/versions", cancellation));

        var version = Assert.Single(history.GetProperty("items").EnumerateArray());
        Assert.Equal(1, version.GetProperty("versionNumber").GetInt32());
        Assert.Equal("Duplicate", version.GetProperty("source").GetString());
        Assert.Null(version.GetProperty("parentVersionId").GetString());

        // A duplicate's provenance is on the recipe; the restore edge stays null.
        Assert.Null(version.GetProperty("restoredFromVersionId").GetString());
    }

    /// <summary>
    /// The independence the requirement turns on, proved in both directions through the wire: editing either
    /// recipe leaves the other exactly as it was.
    /// </summary>
    [Fact]
    public async Task Editing_either_recipe_leaves_the_other_alone()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, sourceToken, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var location = (await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), new { title = "A copy" }, cancellation))
            .Headers.Location!.ToString();
        var copy = await BodyOf(await client.GetAsync(location, cancellation));
        var copyId = copy.GetProperty("id").GetGuid();

        await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, copyId),
            new { expectedConcurrencyToken = copy.GetProperty("concurrencyToken").GetString(), headnote = "Rewritten for the copy." },
            cancellation);

        await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = sourceToken, title = "Source moved on" },
            cancellation);

        var sourceAfter = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipeId), cancellation));
        var copyAfter = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, copyId), cancellation));

        Assert.Equal("Source moved on", sourceAfter.GetProperty("title").GetString());
        Assert.Equal("The one my grandmother made.", sourceAfter.GetProperty("headnote").GetString());

        Assert.Equal("A copy", copyAfter.GetProperty("title").GetString());
        Assert.Equal("Rewritten for the copy.", copyAfter.GetProperty("headnote").GetString());
    }

    // ---- Choosing a source version ----

    [Fact]
    public async Task Omitting_the_version_copies_the_recipe_as_it_stands()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, title = "Lemon olive oil cake", headnote = "Rewritten." },
            cancellation);

        var location = (await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), new { title = "A copy" }, cancellation))
            .Headers.Location!.ToString();
        var copy = await BodyOf(await client.GetAsync(location, cancellation));

        Assert.Equal("Rewritten.", copy.GetProperty("headnote").GetString());
    }

    /// <summary>
    /// Naming an older version copies what that version said, not what the recipe says now — the whole point
    /// of choosing one.
    /// </summary>
    [Fact]
    public async Task Naming_a_version_copies_what_that_version_said()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, headnote = "Rewritten." },
            cancellation);

        var created = await BodyOf(await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId),
            new { title = "A copy of the first draft", sourceVersionNumber = 1 },
            cancellation));

        var copy = await BodyOf(await client.GetAsync(
            RecipeIn(_fixture.WorkspaceA, created.GetProperty("recipeId").GetGuid()), cancellation));

        Assert.Equal("The one my grandmother made.", copy.GetProperty("headnote").GetString());

        // And the lineage names version 1 rather than the current one.
        var lineage = copy.GetProperty("duplicatedFrom");
        Assert.Equal(1, lineage.GetProperty("versionNumber").GetInt32());

        var history = await BodyOf(await client.GetAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/versions", cancellation));
        var first = history.GetProperty("items").EnumerateArray()
            .Single(version => version.GetProperty("versionNumber").GetInt32() == 1);

        Assert.Equal(first.GetProperty("id").GetGuid(), lineage.GetProperty("versionId").GetGuid());
    }

    // ---- Roles ----

    /// <summary>
    /// The bar is Contributor, not the Editor a restore needs: copying takes nothing away from anyone.
    /// </summary>
    [Fact]
    public async Task A_contributor_may_duplicate()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, _) = await SeedAsync(owner, _fixture.WorkspaceA);

        using var contributor = await _fixture.SignInAsync(ContributorEmail, cancellationToken: cancellation);
        var response = await contributor.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), new { title = "A contributor's copy" }, cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // And the copy is theirs to read, at the location the create pointed them to.
        var copy = await BodyOf(await contributor.GetAsync(response.Headers.Location!.ToString(), cancellation));
        Assert.Equal("A contributor's copy", copy.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_viewer_may_not_duplicate()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, _) = await SeedAsync(owner, _fixture.WorkspaceB);

        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: cancellation);
        var response = await viewer.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceB, recipeId), new { title = "Not allowed" }, cancellation);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Refusals ----

    [Fact]
    public async Task A_duplicate_with_no_title_is_a_bad_request_naming_the_field()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), new { sourceVersionNumber = 1 }, cancellation);
        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeInvalidRequest, problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("title", out _));
    }

    /// <summary>
    /// Its own 404 code, naming the body field at fault — and the field name Business uses has to be the one
    /// the client sent.
    /// </summary>
    [Fact]
    public async Task A_version_the_source_does_not_have_is_a_not_found_naming_the_field()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId),
            new { title = "A copy", sourceVersionNumber = 9 },
            cancellation);
        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.VersionNotFound, problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("sourceVersionNumber", out _));
    }

    [Fact]
    public async Task An_unknown_recipe_is_a_not_found()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);

        var response = await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, Guid.NewGuid()), new { title = "A copy" }, cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// There is no concurrency token on this route and no conflict to report, so a body carrying one is
    /// simply ignored rather than validated — the field does not exist on the contract.
    /// </summary>
    [Fact]
    public async Task A_body_carrying_an_unknown_field_is_ignored()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId),
            new { title = "A copy", expectedConcurrencyToken = "CAcGBQQDAgE=", status = "Ready" },
            cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // And the status the body tried to set had no effect.
        Assert.Equal("Draft", (await BodyOf(response)).GetProperty("status").GetString());
    }

    // ---- Idempotency ----

    [Fact]
    public async Task A_replayed_duplicate_returns_the_original_response_and_creates_one_recipe()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, _) = await SeedAsync(client, _fixture.WorkspaceA);
        var body = new { title = "A copy" };

        var first = await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), body, "copy-key-1", cancellation);
        var second = await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), body, "copy-key-1", cancellation);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.False(first.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal("true", second.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());
        Assert.Equal((await BodyOf(first)).GetRawText(), (await BodyOf(second)).GetRawText());

        // One copy, not two — which is the whole point: without the key the creator would be left with two
        // recipes under one name and no way to tell which the retry made.
        var library = await BodyOf(await client.GetAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipes", cancellation));

        Assert.Equal(
            1,
            library.GetProperty("items").EnumerateArray()
                .Count(recipe => recipe.GetProperty("title").GetString() == "A copy"));
    }

    [Fact]
    public async Task The_same_key_for_a_different_title_is_refused()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, _) = await SeedAsync(client, _fixture.WorkspaceA);

        await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), new { title = "One copy" }, "copy-key-2", cancellation);
        var second = await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), new { title = "Another copy" }, "copy-key-2", cancellation);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, (await BodyOf(second)).GetProperty("code").GetString());
    }

    /// <summary>
    /// Without a key, two identical requests are two copies — the honest answer, because a duplicate is a
    /// create and nothing about the second request says it is a retry.
    /// </summary>
    [Fact]
    public async Task Without_a_key_two_identical_requests_make_two_copies()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var first = await BodyOf(await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), new { title = "A copy" }, cancellation));
        var second = await BodyOf(await client.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), new { title = "A copy" }, cancellation));

        Assert.NotEqual(first.GetProperty("recipeId").GetGuid(), second.GetProperty("recipeId").GetGuid());
    }

    // ---- Isolation ----

    [Fact]
    public async Task The_other_workspace_cannot_duplicate_this_recipe()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var inA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, _) = await SeedAsync(inA, _fixture.WorkspaceA);

        using var inB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellation);

        // Through B's own slug, the only route B may use, naming A's recipe id exactly.
        var throughB = await inB.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceB, recipeId), new { title = "Stolen copy" }, cancellation);

        Assert.Equal(HttpStatusCode.NotFound, throughB.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, (await BodyOf(throughB)).GetProperty("code").GetString());

        // And through A's slug, which B is not a member of: the workspace is hidden before any recipe is
        // considered.
        var throughA = await inB.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), new { title = "Stolen copy" }, cancellation);

        Assert.Equal(HttpStatusCode.NotFound, throughA.StatusCode);

        // Nothing was created in either workspace.
        var libraryB = await BodyOf(await inB.GetAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}/recipes", cancellation));
        Assert.Empty(libraryB.GetProperty("items").EnumerateArray());

        var libraryA = await BodyOf(await inA.GetAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipes", cancellation));
        Assert.Single(libraryA.GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// The copy's tag links point at its own workspace's vocabulary row, not at the other workspace's
    /// identically named one. Checked with the filters ignored, because the point is what is in the table.
    /// </summary>
    [Fact]
    public async Task A_copied_tag_links_to_this_workspaces_own_vocabulary_row()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var inA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        using var inB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellation);

        // B first, so a "weeknight" row exists in the other workspace before A's copy is made.
        await SeedAsync(inB, _fixture.WorkspaceB);
        var (recipeId, _, _) = await SeedAsync(inA, _fixture.WorkspaceA);

        var location = (await inA.PostAsJsonAsync(
            DuplicateIn(_fixture.WorkspaceA, recipeId), new { title = "A copy" }, cancellation))
            .Headers.Location!.ToString();
        var copy = await BodyOf(await inA.GetAsync(location, cancellation));

        var linked = Assert.Single(copy.GetProperty("tags").EnumerateArray()).GetProperty("workspaceTagId").GetGuid();

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var tag = await db.WorkspaceTags.IgnoreQueryFilters().SingleAsync(candidate => candidate.Id == linked, cancellation);

        Assert.Equal(_fixture.WorkspaceA.Id, tag.WorkspaceId);
        Assert.Equal("weeknight", tag.NormalizedName);

        // Reusing the existing row rather than creating a second one for the same name.
        Assert.Equal(
            1,
            await db.WorkspaceTags.IgnoreQueryFilters().CountAsync(
                candidate => candidate.WorkspaceId == _fixture.WorkspaceA.Id && candidate.NormalizedName == "weeknight",
                cancellation));
    }

    /// <inheritdoc cref="RecipeUpdateEndpointTests"/>
    private async Task<IReadOnlyDictionary<Guid, Guid>> AddMemberAsync(
        string email, WorkspaceRole role, params Guid[] workspaceIds)
    {
        var userId = await _fixture.Api.CreateUserAsync(email, Password);
        var memberships = workspaceIds.ToDictionary(workspaceId => workspaceId, _ => Guid.NewGuid());

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        foreach (var (workspaceId, membershipId) in memberships)
        {
            db.WorkspaceMemberships.Add(new WorkspaceMembership
            {
                Id = membershipId,
                WorkspaceId = workspaceId,
                UserId = userId,
                Role = role,
                Status = WorkspaceMembershipStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync();

        return memberships;
    }
}
