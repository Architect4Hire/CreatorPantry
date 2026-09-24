using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>POST .../recipes/{recipeId}/archive</c> and <c>.../unarchive</c> through the real Gateway — the state
/// machine, what an archived recipe refuses, what it keeps, what the library shows, and what the other
/// workspace can see.
/// </summary>
/// <remarks>
/// The load-bearing claim of REC-006 is negative — archiving is <em>not</em> a delete — so much of this file
/// asserts what survives rather than what changed: the versions, the tags, the content, the readability of
/// the recipe by id, and the ability to copy it.
/// </remarks>
public sealed class RecipeArchiveEndpointTests : IAsyncLifetime
{
    /// <summary>
    /// A Contributor may edit a recipe and may not shelve one, which is the distinction the Editor bar
    /// exists to draw. The shared fixture seeds Owner plus Editor in A and Viewer in B, so this role has to
    /// be added.
    /// </summary>
    private const string ContributorEmail = "archive-contributor-a@example.com";

    private const string Password = "correct horse battery";

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

        await AddMemberAsync(ContributorEmail, WorkspaceRole.Contributor, _fixture.WorkspaceA.Id);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string RecipesIn(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes";

    private static string RecipeIn(SeededWorkspace workspace, Guid recipeId) =>
        $"{RecipesIn(workspace)}/{recipeId}";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private async Task<(Guid RecipeId, string Token, JsonElement Detail)> SeedAsync(
        GatewayClient client, SeededWorkspace workspace, string title = "Olive oil cake")
    {
        var cancellation = TestContext.Current.CancellationToken;

        var created = await client.PostAsJsonAsync(
            RecipesIn(workspace),
            new
            {
                title,
                headnote = "The one my grandmother made.",
                status = "Ready",
                tags = new[] { "weeknight" },
                instructions = new object[]
                {
                    new { title = "Batter", steps = new object[] { new { text = "Whisk the eggs." } } },
                },
            },
            cancellation);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var detail = await BodyOf(await client.GetAsync(created.Headers.Location!.ToString(), cancellation));

        return (detail.GetProperty("id").GetGuid(), detail.GetProperty("concurrencyToken").GetString()!, detail);
    }

    /// <summary>Archives a recipe and returns it as the command answered.</summary>
    private async Task<JsonElement> ArchiveAsync(GatewayClient client, SeededWorkspace workspace, Guid recipeId, string token)
    {
        var response = await client.PostAsJsonAsync(
            $"{RecipeIn(workspace, recipeId)}/archive",
            new { expectedConcurrencyToken = token },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    // ---- The state machine ----

    [Fact]
    public async Task Archiving_moves_a_recipe_to_archived()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var archived = await ArchiveAsync(client, _fixture.WorkspaceA, recipeId, token);

        Assert.Equal("Archived", archived.GetProperty("status").GetString());

        // The response is the whole recipe in the shape a read returns, so a client can rebind from it.
        // That the token actually moves is not assertable here — SqliteModelCustomizer fills RowVersion on
        // insert and never bumps it on update — and is covered against real SQL Server in
        // RecipeDataLayerTests.
        Assert.Equal(recipeId, archived.GetProperty("id").GetGuid());
        Assert.False(string.IsNullOrEmpty(archived.GetProperty("concurrencyToken").GetString()));
    }

    [Fact]
    public async Task Unarchiving_brings_it_back_as_a_draft()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var archived = await ArchiveAsync(client, _fixture.WorkspaceA, recipeId, token);

        var response = await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/unarchive",
            new { expectedConcurrencyToken = archived.GetProperty("concurrencyToken").GetString() },
            cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Draft, not the Ready it was before it was shelved: nothing records the earlier state, and calling
        // it finished again is the creator's to do.
        Assert.Equal("Draft", (await BodyOf(response)).GetProperty("status").GetString());
    }

    // ---- Repeated commands ----

    [Fact]
    public async Task Archiving_an_archived_recipe_succeeds_and_changes_nothing()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var first = await ArchiveAsync(client, _fixture.WorkspaceA, recipeId, token);
        var second = await ArchiveAsync(
            client, _fixture.WorkspaceA, recipeId, first.GetProperty("concurrencyToken").GetString()!);

        Assert.Equal("Archived", second.GetProperty("status").GetString());

        // No second transition: the timestamp and the token are the ones the first command produced, so no
        // collaborator's token was invalidated for nothing.
        Assert.Equal(first.GetProperty("updatedAt").GetString(), second.GetProperty("updatedAt").GetString());
        Assert.Equal(first.GetProperty("concurrencyToken").GetString(), second.GetProperty("concurrencyToken").GetString());

        // And exactly one audit entry, not two — a log that recorded a transition that did not happen would
        // be worse than one that recorded nothing.
        Assert.Equal(1, await AuditCountAsync(RecipeAuditActions.Archived, recipeId));
    }

    [Fact]
    public async Task Unarchiving_a_recipe_that_is_not_archived_succeeds_and_changes_nothing()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, created) = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/unarchive",
            new { expectedConcurrencyToken = token },
            cancellation);
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Untouched — including its Ready status, which unarchiving must not quietly downgrade.
        Assert.Equal("Ready", body.GetProperty("status").GetString());
        Assert.Equal(created.GetProperty("updatedAt").GetString(), body.GetProperty("updatedAt").GetString());
        Assert.Equal(0, await AuditCountAsync(RecipeAuditActions.Unarchived, recipeId));
    }

    // ---- Audit ----

    [Fact]
    public async Task Archiving_records_an_audit_entry_naming_the_transition()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        await ArchiveAsync(client, _fixture.WorkspaceA, recipeId, token);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var entry = await db.AuditLogs
            .IgnoreQueryFilters()
            .SingleAsync(log => log.ResourceId == recipeId.ToString("D"), cancellation);

        Assert.Equal(RecipeAuditActions.Archived, entry.Action);
        Assert.Equal(RecipeAuditActions.ResourceType, entry.ResourceType);
        Assert.Equal(_fixture.WorkspaceA.Id, entry.WorkspaceId);
        Assert.False(string.IsNullOrWhiteSpace(entry.ActorUserId));
        Assert.Equal("Ready", entry.BeforeReference);
        Assert.Equal("Archived", entry.AfterReference);
        Assert.NotEqual(Guid.Empty, entry.CorrelationId);

        // Safe to display: state names and a sentence, never the creator's recipe.
        Assert.DoesNotContain("grandmother", entry.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Olive oil cake", entry.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A refused command records nothing. The audit entry and the status change commit together or neither
    /// does, which is the only reason the entry can be trusted.
    /// </summary>
    [Fact]
    public async Task A_refused_archive_records_nothing()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/archive",
            new { expectedConcurrencyToken = "CAcGBQQDAgE=" },
            cancellation);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await AuditCountAsync(RecipeAuditActions.Archived, recipeId));

        var reread = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipeId), cancellation));
        Assert.Equal("Ready", reread.GetProperty("status").GetString());
    }

    // ---- Archive is not a delete ----

    /// <summary>
    /// The central claim of REC-006. Everything the recipe holds survives, and the recipe stays readable by
    /// id — what changes is the library listing and what it will accept.
    /// </summary>
    [Fact]
    public async Task An_archived_recipe_keeps_its_content_versions_tags_and_history()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, before) = await SeedAsync(client, _fixture.WorkspaceA);

        // A second version, so the history has something to lose.
        var edited = await BodyOf(await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, notes = "Halve the sugar." },
            cancellation));

        await ArchiveAsync(client, _fixture.WorkspaceA, recipeId, edited.GetProperty("concurrencyToken").GetString()!);

        // Still readable by id, with every part of it intact.
        var after = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipeId), cancellation));

        Assert.Equal("Archived", after.GetProperty("status").GetString());
        Assert.Equal("Olive oil cake", after.GetProperty("title").GetString());
        Assert.Equal("The one my grandmother made.", after.GetProperty("headnote").GetString());
        Assert.Equal("Halve the sugar.", after.GetProperty("notes").GetString());
        Assert.Equal("weeknight", Assert.Single(after.GetProperty("tags").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(
            before.GetProperty("instructionGroups").EnumerateArray().Single().GetProperty("id").GetGuid(),
            after.GetProperty("instructionGroups").EnumerateArray().Single().GetProperty("id").GetGuid());

        // And its history is readable and unchanged — archiving writes no version.
        var history = await BodyOf(await client.GetAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/versions", cancellation));

        Assert.Equal([2, 1], history.GetProperty("items").EnumerateArray()
            .Select(version => version.GetProperty("versionNumber").GetInt32()));

        // No snapshot can therefore say "Archived", which is what keeps a version restore from moving a
        // recipe into the archive without this command.
        Assert.DoesNotContain(
            history.GetProperty("items").EnumerateArray(),
            version => version.GetProperty("source").GetString() == "Duplicate");
        Assert.Equal(2, after.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
    }

    /// <summary>Comparing and copying an archived recipe both keep working: neither changes it.</summary>
    [Fact]
    public async Task An_archived_recipe_can_still_be_compared_and_copied()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var edited = await BodyOf(await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, notes = "Halve the sugar." },
            cancellation));

        await ArchiveAsync(client, _fixture.WorkspaceA, recipeId, edited.GetProperty("concurrencyToken").GetString()!);

        var compared = await client.GetAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/versions/compare?from=1&to=2", cancellation);
        Assert.Equal(HttpStatusCode.OK, compared.StatusCode);

        var copied = await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/duplicate",
            new { title = "Picked back up" },
            cancellation);

        Assert.Equal(HttpStatusCode.Created, copied.StatusCode);
        Assert.Equal("Draft", (await BodyOf(copied)).GetProperty("status").GetString());
    }

    // ---- Forbidden while archived ----

    [Fact]
    public async Task An_archived_recipe_refuses_an_edit()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var archived = await ArchiveAsync(client, _fixture.WorkspaceA, recipeId, token);

        var response = await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new
            {
                expectedConcurrencyToken = archived.GetProperty("concurrencyToken").GetString(),
                title = "Should not land",
            },
            cancellation);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeArchivedConflict, (await BodyOf(response)).GetProperty("code").GetString());

        var reread = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipeId), cancellation));
        Assert.Equal("Olive oil cake", reread.GetProperty("title").GetString());
    }

    /// <summary>
    /// A version restore is a content write, so it is frozen too — and its own code, not the edit's, because
    /// the remedy is to bring the recipe back rather than to re-read it.
    /// </summary>
    [Fact]
    public async Task An_archived_recipe_refuses_a_version_restore()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var edited = await BodyOf(await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, notes = "Halve the sugar." },
            cancellation));

        var archived = await ArchiveAsync(
            client, _fixture.WorkspaceA, recipeId, edited.GetProperty("concurrencyToken").GetString()!);

        var response = await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/versions/1/restore",
            new { expectedConcurrencyToken = archived.GetProperty("concurrencyToken").GetString() },
            cancellation);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeArchivedConflict, (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// And the freeze lifts the moment the recipe comes back — the point of the archive being reversible.
    /// </summary>
    [Fact]
    public async Task Bringing_a_recipe_back_makes_it_editable_again()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var archived = await ArchiveAsync(client, _fixture.WorkspaceA, recipeId, token);

        var restored = await BodyOf(await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/unarchive",
            new { expectedConcurrencyToken = archived.GetProperty("concurrencyToken").GetString() },
            cancellation));

        var edited = await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new
            {
                expectedConcurrencyToken = restored.GetProperty("concurrencyToken").GetString(),
                title = "Lemon olive oil cake",
            },
            cancellation);

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("Lemon olive oil cake", (await BodyOf(edited)).GetProperty("title").GetString());
    }

    // ---- Search exclusion ----

    [Fact]
    public async Task An_archived_recipe_drops_out_of_the_default_library()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (shelved, token, _) = await SeedAsync(client, _fixture.WorkspaceA, "Shelved cake");
        await SeedAsync(client, _fixture.WorkspaceA, "Working cake");

        await ArchiveAsync(client, _fixture.WorkspaceA, shelved, token);

        var library = await BodyOf(await client.GetAsync(RecipesIn(_fixture.WorkspaceA), cancellation));

        Assert.Equal(["Working cake"], library.GetProperty("items").EnumerateArray()
            .Select(recipe => recipe.GetProperty("title").GetString()));

        // The total agrees with the rows rather than counting what the page excluded.
        Assert.Equal(1, library.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Asking_for_archived_recipes_lists_them()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (shelved, token, _) = await SeedAsync(client, _fixture.WorkspaceA, "Shelved cake");
        await SeedAsync(client, _fixture.WorkspaceA, "Working cake");

        await ArchiveAsync(client, _fixture.WorkspaceA, shelved, token);

        var archive = await BodyOf(await client.GetAsync(
            $"{RecipesIn(_fixture.WorkspaceA)}?status=Archived", cancellation));

        Assert.Equal(["Shelved cake"], archive.GetProperty("items").EnumerateArray()
            .Select(recipe => recipe.GetProperty("title").GetString()));
    }

    /// <summary>
    /// And it comes back into the library when it comes back — the exclusion follows the state rather than
    /// being a one-way door.
    /// </summary>
    [Fact]
    public async Task An_unarchived_recipe_returns_to_the_default_library()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA, "Shelved cake");

        var archived = await ArchiveAsync(client, _fixture.WorkspaceA, recipeId, token);

        await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/unarchive",
            new { expectedConcurrencyToken = archived.GetProperty("concurrencyToken").GetString() },
            cancellation);

        var library = await BodyOf(await client.GetAsync(RecipesIn(_fixture.WorkspaceA), cancellation));

        Assert.Equal(["Shelved cake"], library.GetProperty("items").EnumerateArray()
            .Select(recipe => recipe.GetProperty("title").GetString()));
    }

    // ---- Roles ----

    [Fact]
    public async Task An_editor_may_archive()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(owner, _fixture.WorkspaceA);

        Assert.Equal(WorkspaceRole.Editor, _fixture.WorkspaceA.MemberRole);

        using var editor = await _fixture.SignInAsync(_fixture.WorkspaceA.MemberEmail, cancellationToken: cancellation);
        var response = await editor.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/archive",
            new { expectedConcurrencyToken = token },
            cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The distinction the Editor bar draws: a Contributor may change this recipe and may not take it out of
    /// everyone's library.
    /// </summary>
    [Theory]
    [InlineData("archive")]
    [InlineData("unarchive")]
    public async Task A_contributor_may_not_run_either_command(string command)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(owner, _fixture.WorkspaceA);

        using var contributor = await _fixture.SignInAsync(ContributorEmail, cancellationToken: cancellation);

        var refused = await contributor.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/{command}",
            new { expectedConcurrencyToken = token },
            cancellation);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var edited = await contributor.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, notes = "An ordinary contribution." },
            cancellation);

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
    }

    [Fact]
    public async Task A_viewer_may_not_archive()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(owner, _fixture.WorkspaceB);

        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: cancellation);
        var response = await viewer.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceB, recipeId)}/archive",
            new { expectedConcurrencyToken = token },
            cancellation);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Refusals ----

    [Fact]
    public async Task A_command_with_no_token_is_a_bad_request_naming_the_field()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/archive", new { }, cancellation);
        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeInvalidRequest, problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("expectedConcurrencyToken", out _));
    }

    [Fact]
    public async Task An_unknown_recipe_is_a_not_found()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);

        var response = await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, Guid.NewGuid())}/archive",
            new { expectedConcurrencyToken = "AQIDBAUGBwg=" },
            cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, (await BodyOf(response)).GetProperty("code").GetString());
    }

    // ---- Isolation ----

    [Fact]
    public async Task The_other_workspace_cannot_archive_this_recipe()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var inA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(inA, _fixture.WorkspaceA);

        using var inB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellation);

        // Through B's own slug, the only route B may use, naming A's recipe id exactly.
        var throughB = await inB.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceB, recipeId)}/archive",
            new { expectedConcurrencyToken = token },
            cancellation);

        Assert.Equal(HttpStatusCode.NotFound, throughB.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, (await BodyOf(throughB)).GetProperty("code").GetString());

        // And through A's slug, which B is not a member of.
        var throughA = await inB.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/archive",
            new { expectedConcurrencyToken = token },
            cancellation);

        Assert.Equal(HttpStatusCode.NotFound, throughA.StatusCode);

        // A's recipe is untouched and no audit entry was written for either attempt.
        var reread = await BodyOf(await inA.GetAsync(RecipeIn(_fixture.WorkspaceA, recipeId), cancellation));
        Assert.Equal("Ready", reread.GetProperty("status").GetString());
        Assert.Equal(0, await AuditCountAsync(RecipeAuditActions.Archived, recipeId));
    }

    /// <summary>
    /// Archiving in one workspace changes nothing about the other's library — including when both hold a
    /// recipe of the same name.
    /// </summary>
    [Fact]
    public async Task Archiving_in_one_workspace_leaves_the_others_library_alone()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var inA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        using var inB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellation);

        var (recipeA, tokenA, _) = await SeedAsync(inA, _fixture.WorkspaceA, "Shared name");
        await SeedAsync(inB, _fixture.WorkspaceB, "Shared name");

        await ArchiveAsync(inA, _fixture.WorkspaceA, recipeA, tokenA);

        var libraryA = await BodyOf(await inA.GetAsync(RecipesIn(_fixture.WorkspaceA), cancellation));
        var libraryB = await BodyOf(await inB.GetAsync(RecipesIn(_fixture.WorkspaceB), cancellation));

        Assert.Empty(libraryA.GetProperty("items").EnumerateArray());
        Assert.Single(libraryB.GetProperty("items").EnumerateArray());
    }

    /// <summary>The audit entry is workspace-owned like any other insert, so it is B's business only if B wrote it.</summary>
    [Fact]
    public async Task An_audit_entry_belongs_to_the_workspace_that_wrote_it()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var inB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(inB, _fixture.WorkspaceB);

        await ArchiveAsync(inB, _fixture.WorkspaceB, recipeId, token);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var entry = await db.AuditLogs
            .IgnoreQueryFilters()
            .SingleAsync(log => log.ResourceId == recipeId.ToString("D"), cancellation);

        Assert.Equal(_fixture.WorkspaceB.Id, entry.WorkspaceId);
        Assert.NotEqual(_fixture.WorkspaceA.Id, entry.WorkspaceId);
    }

    private async Task<int> AuditCountAsync(string action, Guid recipeId)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.AuditLogs
            .IgnoreQueryFilters()
            .CountAsync(
                log => log.Action == action && log.ResourceId == recipeId.ToString("D"),
                TestContext.Current.CancellationToken);
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
