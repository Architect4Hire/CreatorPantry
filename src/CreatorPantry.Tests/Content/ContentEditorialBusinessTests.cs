using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Recipes;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The accepted-editorial read an export draws its sections from: which revision counts, when it is current,
/// that SEO and editorial acceptances never stand in for each other, and that another workspace's acceptance
/// can never be seen.
/// </summary>
public sealed class ContentEditorialBusinessTests : IDisposable
{
    private static readonly DateTimeOffset Now = RecipeAggregateFixture.Now;
    private static readonly Guid WsA = RecipeAggregateFixture.WorkspaceA;
    private static readonly Guid WsB = RecipeAggregateFixture.WorkspaceB;
    private static readonly Guid Author = Guid.NewGuid();
    private static readonly Guid LineId = Guid.NewGuid();

    private static readonly string Package = Lined(
        """
        {"schemaVersion":"content.editorial-package.v1","sections":{
          "headnote":{"text":"A headnote."},
          "introduction":{"text":"An intro."},
          "tips":[{"text":"Tip one."},{"text":"Tip two."}],
          "substitutions":[{"lineId":"__LINE__","suggestion":"Use oat flour","culinaryNote":"Denser."}],
          "storageReheating":{"text":"Keep covered."},
          "faq":[{"question":"Freeze?","answer":"Yes."}],
          "cta":{"text":"Tag us!"}}}
        """);

    private static string Lined(string json) => json.Replace("__LINE__", LineId.ToString());

    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seeded(Guid RecipeId, Guid Version1, Guid Version2);

    private async Task<Seeded> SeedAsync(
        Guid workspaceId,
        ContentPackageKind kind = ContentPackageKind.Editorial,
        string content = "",
        ContentProposalStatus status = ContentProposalStatus.Accepted)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);

        var recipe = RecipeAggregateFixture.NewRecipe("Soda bread");
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
            SchemaVersion = 1, Content = content.Length == 0 ? Package : content,
            CreatedByMembershipId = Author, CreatedAt = Now,
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
        return new Seeded(recipe.Id, v1.Id, v2.Id);
    }

    private static RecipeVersion NewVersion(Guid workspaceId, Guid recipeId, int number) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = workspaceId, RecipeId = recipeId, VersionNumber = number,
        Source = RecipeVersionSource.CreatorEdit, Readiness = RecipeVersionReadiness.Draft,
        CreatedByMembershipId = Author, CreatedAt = Now,
        SnapshotSchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
    };

    private async Task<Domain.Managers.Results.OperationResult<AcceptedEditorialServiceModel?>> ReadAsync(
        Guid workspaceId, Guid recipeId, Guid versionId, int? revision = null)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);
        var business = new ContentEditorialBusiness(new ContentSeoDataLayer(new ContentSeoRepository(db)));

        return await business.GetAcceptedEditorialAsync(recipeId, versionId, revision, Ct);
    }

    // ---- What counts ----

    [Fact]
    public async Task An_accepted_revision_for_the_exported_version_is_current_and_carries_every_section()
    {
        var seeded = await SeedAsync(WsA);

        var result = await ReadAsync(WsA, seeded.RecipeId, seeded.Version1);

        var editorial = result.Value!;
        Assert.True(editorial.IsCurrent);
        Assert.Equal(1, editorial.RevisionNumber);
        Assert.Equal("A headnote.", editorial.Headnote);
        Assert.Equal("An intro.", editorial.Introduction);
        Assert.Equal(["Tip one.", "Tip two."], editorial.Tips);
        Assert.Equal(new AcceptedEditorialSubstitution(LineId, "Use oat flour", "Denser."), Assert.Single(editorial.Substitutions));
        Assert.Equal("Keep covered.", editorial.StorageReheating);
        Assert.Equal(new AcceptedEditorialFaq("Freeze?", "Yes."), Assert.Single(editorial.Faq));
        Assert.Equal("Tag us!", editorial.Cta);
    }

    [Fact]
    public async Task An_acceptance_for_another_version_or_marked_for_review_is_not_current()
    {
        var other = await SeedAsync(WsA);
        var review = await SeedAsync(WsA, status: ContentProposalStatus.NeedsReview);

        Assert.False((await ReadAsync(WsA, other.RecipeId, other.Version2)).Value!.IsCurrent);
        Assert.False((await ReadAsync(WsA, review.RecipeId, review.Version1)).Value!.IsCurrent);
    }

    [Fact]
    public async Task A_later_rejection_leaves_the_earlier_acceptance_in_force()
    {
        var seeded = await SeedAsync(WsA, status: ContentProposalStatus.Rejected);

        Assert.True((await ReadAsync(WsA, seeded.RecipeId, seeded.Version1)).Value!.IsCurrent);
    }

    [Fact]
    public async Task An_seo_acceptance_is_never_read_as_editorial()
    {
        var seeded = await SeedAsync(WsA, kind: ContentPackageKind.Seo);

        var result = await ReadAsync(WsA, seeded.RecipeId, seeded.Version1);

        Assert.True(result.Succeeded);
        Assert.Null(result.Value);
    }

    // ---- Naming a revision ----

    [Fact]
    public async Task Only_the_accepted_revisions_own_number_can_be_named()
    {
        var seeded = await SeedAsync(WsA);

        var accepted = await ReadAsync(WsA, seeded.RecipeId, seeded.Version1, revision: 1);
        var other = await ReadAsync(WsA, seeded.RecipeId, seeded.Version1, revision: 4);

        Assert.Equal(1, accepted.Value!.RevisionNumber);
        Assert.Equal(ContentErrorCodes.RevisionNotFound, other.Error!.Code);
        Assert.Contains("editorialRevision", other.Error.FieldErrors.Keys);
    }

    // ---- Isolation ----

    [Fact]
    public async Task Another_workspaces_acceptance_is_invisible_and_cannot_be_named()
    {
        var a = await SeedAsync(WsA);
        var b = await SeedAsync(WsB, content: Package.Replace("An intro.", "B's intro."));

        var foreign = await ReadAsync(WsB, a.RecipeId, a.Version1);
        var foreignNamed = await ReadAsync(WsB, a.RecipeId, a.Version1, revision: 1);

        Assert.Null(foreign.Value);
        Assert.Equal(ContentErrorCodes.RevisionNotFound, foreignNamed.Error!.Code);
        Assert.Equal("An intro.", (await ReadAsync(WsA, a.RecipeId, a.Version1)).Value!.Introduction);
        Assert.Equal("B's intro.", (await ReadAsync(WsB, b.RecipeId, b.Version1)).Value!.Introduction);
    }

    // ---- The document reader ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"schemaVersion":"content.editorial-package.v2","sections":{"cta":{"text":"x"}}}""")]
    [InlineData("""{"schemaVersion":"content.editorial-package.v1"}""")]
    [InlineData("""{"schemaVersion":"content.seo-package.v1","sections":{"cta":{"text":"x"}}}""")]
    public void The_reader_yields_an_empty_model_for_anything_it_cannot_read(string? content)
    {
        var editorial = EditorialPackageDocumentReader.Read(3, content, isCurrent: true);

        Assert.Equal(3, editorial.RevisionNumber);
        Assert.True(editorial.IsCurrent);
        Assert.Null(editorial.Headnote);
        Assert.Null(editorial.Introduction);
        Assert.Null(editorial.Cta);
        Assert.Empty(editorial.Tips);
        Assert.Empty(editorial.Substitutions);
        Assert.Empty(editorial.Faq);
    }

    [Fact]
    public void The_reader_skips_malformed_items_and_keeps_the_rest_in_order()
    {
        var editorial = EditorialPackageDocumentReader.Read(1, Lined(
            """
            {"schemaVersion":"content.editorial-package.v1","sections":{
              "tips":[{"text":"b"},{"text":" "},{"x":1},"str",{"text":"a"}],
              "substitutions":[{"lineId":"not-a-guid","suggestion":"s","culinaryNote":"n"},
                               {"lineId":"__LINE__","suggestion":"s"},
                               {"lineId":"__LINE__","suggestion":"s","culinaryNote":"n"}],
              "faq":[{"question":"q"},{"question":"q2","answer":"a2"}],
              "headnote":"not an object"}}
            """), isCurrent: false);

        Assert.Equal(["b", "a"], editorial.Tips);
        Assert.Single(editorial.Substitutions);
        Assert.Equal("q2", Assert.Single(editorial.Faq).Question);
        Assert.Null(editorial.Headnote);
        Assert.False(editorial.IsCurrent);
    }
}
