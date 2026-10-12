using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai.EditorialPackage;

/// <summary>
/// LF.5.3's request seam: the same capability asked about an accepted long-form draft. What must be true of
/// the draft, what is stored, that a recipe request stores what it always did, and that a request is found
/// only by the subject and the workspace it was made for.
/// </summary>
/// <remarks>
/// The draft is scripted (<see cref="FakeLongFormDocumentFacade"/>); that the content module reads only the
/// resolved workspace's drafts is its own tests' and the endpoint tests'. The operation rows are real.
/// </remarks>
public sealed partial class AiSeoPackageRequestBusinessTests
{
    private static readonly Guid Draft = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid OtherDraft = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    // ---- what is queued ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_request_about_an_accepted_piece_queues_the_same_task_naming_the_draft_and_no_recipe()
    {
        _documents.Hold(Draft, "## One\n\nText.\n");

        var outcome = await RequestForLongFormAsync(WorkspaceA, Draft, new() { RevisionNumber = 4 });

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
        var status = outcome.Result.Value!;

        // One capability: the same task, charged and gated as a recipe's package is.
        Assert.Equal(AiTaskType.SeoPackage, status.TaskType);
        Assert.Equal(AiOperationStatus.Requested, status.Status);

        // It names no recipe, so nothing about the row has a path to one.
        Assert.Equal(AiOperationScope.NotApplicable, status.Scope);
        Assert.Null(status.SourceVersionId);

        var inputs = await StoredInputsAsync(WorkspaceA);
        Assert.Equal(Draft.ToString("D"), inputs[AiSeoPackageInputs.LongFormDraftId]);
        Assert.Equal("4", inputs[AiSeoPackageInputs.LongFormRevisionNumber]);

        // Every section but alt text: an article has no image the package was given a fact about.
        Assert.Equal("seoTitle,metaDescription,keyPhrases,slug,internalLinks", inputs[AiSeoPackageInputs.Sections]);
        Assert.True(inputs.ContainsKey(AiSeoPackageInputs.RuleSet));
    }

    /// <summary>
    /// The restriction, held to the stored row: adding a second subject changed nothing a recipe request
    /// writes. These are the inputs a replayed key is compared with and that operations already stored hold.
    /// </summary>
    [Fact]
    public async Task A_recipe_request_stores_exactly_what_it_stored_before_there_was_a_second_subject()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe));
        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
        Assert.Equal(AiOperationScope.Advisory, outcome.Result.Value!.Scope);
        Assert.Equal(recipe.VersionId, outcome.Result.Value.SourceVersionId);

        var inputs = await StoredInputsAsync(WorkspaceA);

        Assert.Equal([AiSeoPackageInputs.Sections, AiSeoPackageInputs.RuleSet], inputs.Keys);
        Assert.Equal("seoTitle,metaDescription,keyPhrases,slug,altText,internalLinks", inputs[AiSeoPackageInputs.Sections]);
        Assert.Equal(new CreatorPantry.Domain.Managers.Reference.SeoRules().Version, inputs[AiSeoPackageInputs.RuleSet]);
        Assert.Equal(0, _documents.Reads);
    }

    // ---- what must be true of the piece ------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_draft_is_not_found_and_nothing_is_queued()
    {
        var outcome = await RequestForLongFormAsync(WorkspaceA, Draft, new() { RevisionNumber = 4 });

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiSeoPackageRequestErrors.DraftNotFound, outcome.Result.Error!.Code);
        Assert.Equal(0, await CountOperationsAsync(WorkspaceA));
    }

    [Theory]
    [InlineData(LongFormDraftStatus.Outlined)]
    [InlineData(LongFormDraftStatus.Writing)]
    [InlineData(LongFormDraftStatus.Assembled)]
    [InlineData(LongFormDraftStatus.NeedsReview)]
    public async Task A_piece_that_is_not_accepted_or_whose_sources_have_moved_is_refused(LongFormDraftStatus status)
    {
        _documents.Hold(Draft, "## One\n\nText.\n", status, status is LongFormDraftStatus.NeedsReview ? 4 : null);

        var outcome = await RequestForLongFormAsync(WorkspaceA, Draft, new() { RevisionNumber = 4 });

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiSeoPackageRequestErrors.DraftNotAccepted, outcome.Result.Error!.Code);
        Assert.Equal(0, await CountOperationsAsync(WorkspaceA));
    }

    [Fact]
    public async Task A_revision_that_is_not_the_accepted_one_is_refused()
    {
        _documents.Hold(Draft, "## One\n\nText.\n", acceptedRevisionNumber: 5);

        var outcome = await RequestForLongFormAsync(WorkspaceA, Draft, new() { RevisionNumber = 4 });

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiSeoPackageRequestErrors.DraftRevisionInvalid, outcome.Result.Error!.Code);
    }

    // ---- the request's shape -----------------------------------------------------------------------------

    [Theory]
    [InlineData(4, null, true)]
    [InlineData(4, "seoTitle,slug,internalLinks", true)]
    [InlineData(0, null, false)]
    [InlineData(4, "altText", false)]
    [InlineData(4, "seoTitle,altText", false)]
    [InlineData(4, "slug", false)]
    [InlineData(4, "seoTitle,seoTitle", false)]
    [InlineData(4, "ranking", false)]
    public void An_article_request_names_an_accepted_revision_and_only_sections_an_article_has(
        int revisionNumber, string? sections, bool valid)
    {
        var model = new RequestLongFormSeoPackageViewModel
        {
            RevisionNumber = revisionNumber,
            Sections = sections?.Split(',').ToList(),
        };

        Assert.Equal(valid, new RequestLongFormSeoPackageViewModelValidator().Validate(model).IsValid);
    }

    // ---- idempotency -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_key_replays_for_the_same_piece_and_is_refused_for_another_piece_or_revision()
    {
        _documents.Hold(Draft, "## One\n\nText.\n");
        _documents.Hold(OtherDraft, "## Two\n\nOther text.\n");

        var first = await RequestForLongFormAsync(WorkspaceA, Draft, new() { RevisionNumber = 4 }, "key-1");
        var again = await RequestForLongFormAsync(WorkspaceA, Draft, new() { RevisionNumber = 4 }, "key-1");

        Assert.False(first.Replayed);
        Assert.True(again.Replayed);
        Assert.Equal(first.Result.Value!.AiProposalRequestId, again.Result.Value!.AiProposalRequestId);
        Assert.Equal(1, await CountOperationsAsync(WorkspaceA));

        // The draft is part of what was asked: the same sections about another piece is another request.
        var other = await RequestForLongFormAsync(WorkspaceA, OtherDraft, new() { RevisionNumber = 4 }, "key-1");
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, other.Result.Error!.Code);

        // And so is the revision: the piece accepted again is not the piece the first request was about.
        _documents.Hold(Draft, "## One\n\nNew text.\n", acceptedRevisionNumber: 5);
        var later = await RequestForLongFormAsync(WorkspaceA, Draft, new() { RevisionNumber = 5 }, "key-1");
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, later.Result.Error!.Code);

        Assert.Equal(1, await CountOperationsAsync(WorkspaceA));
    }

    // ---- a request is found only where it was made -------------------------------------------------------

    [Fact]
    public async Task A_request_is_read_only_for_its_own_draft_its_own_subject_and_its_own_workspace()
    {
        _documents.Hold(Draft, "## One\n\nText.\n");
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var article = (await RequestForLongFormAsync(WorkspaceA, Draft, new() { RevisionNumber = 4 }, "key-article")).Result.Value!;
        var ofRecipe = (await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe), "key-recipe")).Result.Value!;

        var found = await GetForLongFormAsync(WorkspaceA, Draft, article.AiProposalRequestId);
        Assert.True(found.Succeeded, found.Error?.Message);
        Assert.Equal(article.AiProposalRequestId, found.Value!.AiProposalRequestId);

        foreach (var missing in new[]
        {
            // Another draft; a recipe's request asked for as an article's; and the other way about.
            await GetForLongFormAsync(WorkspaceA, OtherDraft, article.AiProposalRequestId),
            await GetForLongFormAsync(WorkspaceA, Draft, ofRecipe.AiProposalRequestId),
            await GetAsync(WorkspaceA, recipe.RecipeId, article.AiProposalRequestId),

            // Another workspace, holding the right draft id and the right request id.
            await GetForLongFormAsync(WorkspaceB, Draft, article.AiProposalRequestId),
        })
        {
            Assert.False(missing.Succeeded);
            Assert.Equal(AiSeoPackageRequestErrors.RequestNotFound, missing.Error!.Code);
        }

        // The recipe's own request is still read where it always was.
        Assert.True((await GetAsync(WorkspaceA, recipe.RecipeId, ofRecipe.AiProposalRequestId)).Succeeded);
    }

    [Fact]
    public async Task One_workspaces_request_about_a_piece_is_not_among_anothers_operations()
    {
        _documents.Hold(Draft, "## One\n\nText.\n");

        await RequestForLongFormAsync(WorkspaceA, Draft, new() { RevisionNumber = 4 });

        Assert.Equal(1, await CountOperationsAsync(WorkspaceA));
        Assert.Equal(0, await CountOperationsAsync(WorkspaceB));
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestForLongFormAsync(
        Guid workspaceId, Guid draftId, RequestLongFormSeoPackageViewModel model, string key = "key-1")
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiSeoPackageRequestBusiness>()
            .RequestForLongFormAsync(draftId, model, key, TestContext.Current.CancellationToken);
    }

    private async Task<OperationResult<AiProposalStatusServiceModel>> GetForLongFormAsync(
        Guid workspaceId, Guid draftId, Guid requestId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiSeoPackageRequestBusiness>()
            .GetForLongFormAsync(draftId, requestId, TestContext.Current.CancellationToken);
    }

    private async Task<int> CountOperationsAsync(Guid workspaceId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
            scope.ServiceProvider.GetRequiredService<CreatorPantry.Domain.Managers.Persistence.CreatorPantryDbContext>().AiOperations,
            TestContext.Current.CancellationToken);
    }
}
