using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The accepted-SEO read an export draws its description and keywords from: which revision counts, when it is
/// current, and that another workspace's acceptance can never be seen.
/// </summary>
public sealed class ContentSeoBusinessTests : IDisposable
{
    private static readonly DateTimeOffset Now = RecipeAggregateFixture.Now;
    private static readonly Guid WsA = RecipeAggregateFixture.WorkspaceA;
    private static readonly Guid WsB = RecipeAggregateFixture.WorkspaceB;
    private static readonly Guid Author = Guid.NewGuid();

    private const string Seo =
        """{"schemaVersion":"content.seo-package.v1","sections":{"metaDescription":{"text":"A dense, tangy loaf."},"keyPhrases":[{"phrase":"soda bread"},{"phrase":"no yeast"}]}}""";

    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seeded(Guid RecipeId, Guid Version1, Guid Version2, Guid ProposalId);

    /// <summary>A recipe with two versions and an SEO proposal whose accepted revision (number 1) is pinned to version 1.</summary>
    private async Task<Seeded> SeedAcceptedAsync(
        Guid workspaceId,
        string content = Seo,
        ContentProposalStatus status = ContentProposalStatus.Accepted,
        ContentPackageKind kind = ContentPackageKind.Seo)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);

        var recipe = RecipeAggregateFixture.NewRecipe(
            "Soda bread", RecipeAggregateFixture.MediaAssetIdFor(workspaceId));
        db.Recipes.Add(recipe);
        var v1 = NewVersion(workspaceId, recipe.Id, 1);
        var v2 = NewVersion(workspaceId, recipe.Id, 2);
        db.RecipeVersions.AddRange(v1, v2);

        var proposal = new ContentProposal
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, RecipeId = recipe.Id, Kind = kind,
            Status = ContentProposalStatus.Proposed, CreatedByMembershipId = Author, CreatedAt = Now, UpdatedAt = Now,
        };
        var revision = new ContentRevision
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, ContentProposalId = proposal.Id, RecipeId = recipe.Id,
            RevisionNumber = 1, Source = ContentRevisionSource.CreatorEdit, RecipeVersionId = v1.Id,
            SchemaVersion = 1, Content = content, CreatedByMembershipId = Author, CreatedAt = Now,
        };
        db.ContentProposals.Add(proposal);
        db.ContentRevisions.Add(revision);
        await db.SaveChangesAsync(Ct);

        proposal.Status = status;
        proposal.AcceptedRevisionId = revision.Id;
        if (status == ContentProposalStatus.NeedsReview)
        {
            proposal.StaleSince = Now;
            proposal.StaleReasons = ContentStaleReasons.RecipeChanged;
        }

        await db.SaveChangesAsync(Ct);
        return new Seeded(recipe.Id, v1.Id, v2.Id, proposal.Id);
    }

    private static RecipeVersion NewVersion(Guid workspaceId, Guid recipeId, int number) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = workspaceId, RecipeId = recipeId, VersionNumber = number,
        Source = RecipeVersionSource.CreatorEdit, Readiness = RecipeVersionReadiness.Draft,
        CreatedByMembershipId = Author, CreatedAt = Now,
        SnapshotSchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
    };

    private async Task<Domain.Managers.Results.OperationResult<AcceptedSeoServiceModel?>> ReadAsync(
        Guid workspaceId, Guid recipeId, Guid versionId, int? revision = null)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);
        var business = new ContentSeoBusiness(new ContentSeoDataLayer(new ContentSeoRepository(db)));

        return await business.GetAcceptedSeoAsync(recipeId, versionId, revision, Ct);
    }

    // ---- What counts ----

    [Fact]
    public async Task An_accepted_revision_pinned_to_the_exported_version_is_current_and_carries_its_words()
    {
        var seeded = await SeedAcceptedAsync(WsA);

        var result = await ReadAsync(WsA, seeded.RecipeId, seeded.Version1);

        var seo = result.Value!;
        Assert.True(result.Succeeded);
        Assert.True(seo.IsCurrent);
        Assert.Equal(1, seo.RevisionNumber);
        Assert.Equal("A dense, tangy loaf.", seo.MetaDescription);
        Assert.Equal(["soda bread", "no yeast"], seo.KeyPhrases);
    }

    [Fact]
    public async Task An_acceptance_pinned_to_another_version_is_returned_but_not_current()
    {
        var seeded = await SeedAcceptedAsync(WsA);

        var result = await ReadAsync(WsA, seeded.RecipeId, seeded.Version2);

        Assert.False(result.Value!.IsCurrent);
    }

    [Fact]
    public async Task An_acceptance_marked_for_review_is_not_current_even_for_its_own_version()
    {
        var seeded = await SeedAcceptedAsync(WsA, status: ContentProposalStatus.NeedsReview);

        var result = await ReadAsync(WsA, seeded.RecipeId, seeded.Version1);

        Assert.False(result.Value!.IsCurrent);
    }

    [Fact]
    public async Task A_later_rejection_leaves_the_earlier_acceptance_in_force()
    {
        var seeded = await SeedAcceptedAsync(WsA, status: ContentProposalStatus.Rejected);

        var result = await ReadAsync(WsA, seeded.RecipeId, seeded.Version1);

        Assert.True(result.Value!.IsCurrent);
    }

    [Fact]
    public async Task Nothing_accepted_is_a_success_with_no_revision()
    {
        var seeded = await SeedAcceptedAsync(WsA, kind: ContentPackageKind.Editorial);

        var result = await ReadAsync(WsA, seeded.RecipeId, seeded.Version1);

        Assert.True(result.Succeeded);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task A_document_that_cannot_be_read_yields_no_words_rather_than_invented_ones()
    {
        var seeded = await SeedAcceptedAsync(WsA, content: """{"headnote":"not an seo package"}""");

        var result = await ReadAsync(WsA, seeded.RecipeId, seeded.Version1);

        Assert.Null(result.Value!.MetaDescription);
        Assert.Empty(result.Value.KeyPhrases);
    }

    // ---- Naming a revision ----

    [Fact]
    public async Task The_accepted_revisions_own_number_can_be_named()
    {
        var seeded = await SeedAcceptedAsync(WsA);

        var result = await ReadAsync(WsA, seeded.RecipeId, seeded.Version1, revision: 1);

        Assert.Equal(1, result.Value!.RevisionNumber);
    }

    [Fact]
    public async Task A_revision_number_that_was_never_accepted_is_not_found_naming_the_parameter()
    {
        var seeded = await SeedAcceptedAsync(WsA);

        var result = await ReadAsync(WsA, seeded.RecipeId, seeded.Version1, revision: 7);

        Assert.False(result.Succeeded);
        Assert.Equal(ContentErrorCodes.RevisionNotFound, result.Error!.Code);
        Assert.Contains("seoRevision", result.Error.FieldErrors.Keys);
    }

    [Fact]
    public async Task Naming_a_revision_when_nothing_was_accepted_is_not_found()
    {
        var seeded = await SeedAcceptedAsync(WsA, kind: ContentPackageKind.Editorial);

        var result = await ReadAsync(WsA, seeded.RecipeId, seeded.Version1, revision: 1);

        Assert.Equal(ContentErrorCodes.RevisionNotFound, result.Error!.Code);
    }

    // ---- Isolation ----

    [Fact]
    public async Task Another_workspaces_acceptance_is_invisible_and_cannot_be_named()
    {
        var a = await SeedAcceptedAsync(WsA);
        var b = await SeedAcceptedAsync(WsB, content: Seo.Replace("A dense, tangy loaf.", "B's words."));

        // Workspace B reading workspace A's recipe id: nothing, exactly as for an unknown recipe.
        var foreign = await ReadAsync(WsB, a.RecipeId, a.Version1);
        var foreignNamed = await ReadAsync(WsB, a.RecipeId, a.Version1, revision: 1);

        Assert.True(foreign.Succeeded);
        Assert.Null(foreign.Value);
        Assert.Equal(ContentErrorCodes.RevisionNotFound, foreignNamed.Error!.Code);

        // And each workspace still reads its own.
        Assert.Equal("A dense, tangy loaf.", (await ReadAsync(WsA, a.RecipeId, a.Version1)).Value!.MetaDescription);
        Assert.Equal("B's words.", (await ReadAsync(WsB, b.RecipeId, b.Version1)).Value!.MetaDescription);
    }

    // ---- The document reader ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"schemaVersion":"content.seo-package.v2","sections":{}}""")]
    [InlineData("""{"schemaVersion":"content.seo-package.v1"}""")]
    [InlineData("""{"schemaVersion":"content.seo-package.v1","sections":{"metaDescription":"text","keyPhrases":"x"}}""")]
    public void The_reader_yields_nothing_for_anything_it_cannot_read(string? content)
    {
        var (description, phrases) = SeoPackageDocumentReader.Read(content);

        Assert.Null(description);
        Assert.Empty(phrases);
    }

    [Fact]
    public void The_reader_skips_blank_key_phrases_and_keeps_order()
    {
        var (_, phrases) = SeoPackageDocumentReader.Read(
            """{"schemaVersion":"content.seo-package.v1","sections":{"keyPhrases":[{"phrase":"b"},{"phrase":" "},{"x":1},{"phrase":"a"}]}}""");

        Assert.Equal(["b", "a"], phrases);
    }
}
