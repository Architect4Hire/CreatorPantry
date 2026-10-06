using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The prompt library's schema and isolation, over the same two-workspace SQLite fixture the recipe, brand and
/// weekly-theme aggregates use. Proves the EF configuration — the composite lineage keys, the filtered unique
/// index, the check constraints, the query filter and write-once enforcement — not that SQL Server accepts the DDL,
/// which the migration's own verification covers.
/// </summary>
public sealed class PromptRecordAggregateTests : IDisposable
{
    private static readonly DateTimeOffset Now = RecipeAggregateFixture.Now;

    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PromptRecord NewRecord(
        Guid workspaceId,
        string text = "Overhead shot of soda bread on a linen cloth, soft window light.",
        string channelKey = "instagram",
        string? label = "Soda bread hero",
        PromptRecordSource source = PromptRecordSource.Manual,
        Guid? recipeId = null,
        Guid? recipeVersionId = null,
        Guid? generatedImageId = null) => new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ChannelKey = channelKey,
            ImageKind = PromptImageKind.Hero,
            Text = text,
            GeneratedText = source == PromptRecordSource.Manual ? null : text,
            Label = label,
            Source = source,
            AiProposalId = null,
            RecipeId = recipeId,
            RecipeVersionId = recipeVersionId,
            GeneratedImageId = generatedImageId,
            PromptTemplateId = source == PromptRecordSource.Manual ? null : "image.prompt",
            PromptTemplateVersion = source == PromptRecordSource.Manual ? null : "1.0.0",
            PromptTemplateBodyChecksum = source == PromptRecordSource.Manual ? null : $"sha256:{new string('a', 64)}",
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
        };

    private async Task<PromptRecord> SeedAsync(Guid workspaceId, PromptRecord record)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);
        db.PromptRecords.Add(record);
        await db.SaveChangesAsync(Ct);

        return record;
    }

    /// <summary>A recipe and one version of it, so the lineage pins have something real to point at.</summary>
    private async Task<(Guid RecipeId, Guid VersionId)> SeedRecipeAsync(Guid workspaceId)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);

        var recipe = RecipeAggregateFixture.NewRecipe(
            "Soda bread", RecipeAggregateFixture.MediaAssetIdFor(workspaceId));
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(Ct);

        var version = new RecipeVersion
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            RecipeId = recipe.Id,
            VersionNumber = 1,
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
        };
        db.RecipeVersions.Add(version);
        await db.SaveChangesAsync(Ct);

        return (recipe.Id, version.Id);
    }

    /// <summary>An AI operation and a proposal from it, so the provenance pin has something real to point at.</summary>
    private async Task<Guid> SeedProposalAsync(Guid workspaceId)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);

        var operationId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();

        db.AiOperations.Add(new AiOperation
        {
            Id = operationId,
            WorkspaceId = workspaceId,
            TaskType = AiTaskType.RecipeConcepts,
            Scope = AiOperationScope.NotApplicable,
            Status = AiOperationStatus.Proposed,
            IdempotencyKey = $"prompt-{operationId}",
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = Now,
            StatusChangedAt = Now,
            AvailableAt = Now,
        });

        db.AiProposals.Add(new AiProposal
        {
            Id = proposalId,
            WorkspaceId = workspaceId,
            AiOperationId = operationId,
            OutputSchemaVersion = "image.prompt.v1",
            PromptTemplateId = "image.prompt",
            PromptTemplateVersion = "1.0.0",
            PromptTemplateBodyChecksum = "sha256:seed",
            ProviderName = "test-provider",
            ModelName = "test-model",
            CreatedAt = Now,
        });

        await db.SaveChangesAsync(Ct);

        return proposalId;
    }

    /// <summary>
    /// A staged generated image and the operation that produced it, so the image pin has something real to
    /// point at.
    /// </summary>
    /// <remarks>
    /// Fabricated ids worked here until 12.6, when <c>(WorkspaceId, GeneratedImageId)</c> gained a foreign key
    /// — which is what 12.3's note meant by "expect the migration to fail if any stored id is wrong". A test
    /// fixture is the one place a wrong id could still be found, because the save seam refuses to accept one.
    /// </remarks>
    private async Task<Guid> SeedGeneratedImageAsync(Guid workspaceId)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);

        var operationId = Guid.NewGuid();
        var imageId = Guid.NewGuid();

        db.GeneratedImageOperations.Add(new GeneratedImageOperation
        {
            Id = operationId,
            WorkspaceId = workspaceId,
            Status = GeneratedImageOperationStatus.Succeeded,
            PromptText = "Overhead shot of soda bread on linen.",
            VariantCount = 1,
            IdempotencyKey = $"image-{operationId}",
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = Now,
            StatusChangedAt = Now,
            AvailableAt = Now,
        });

        db.GeneratedImages.Add(new GeneratedImage
        {
            Id = imageId,
            WorkspaceId = workspaceId,
            GeneratedImageOperationId = operationId,
            VariantIndex = 0,
            Status = GeneratedImageStatus.Staged,
            ObjectKey = $"staging/{workspaceId:N}/{imageId:N}.png",
            MediaType = "image/png",
            Width = 1024,
            Height = 1024,
            SizeBytes = 2048,
            ContentChecksum = "sha256:" + new string('a', 64),
            ProviderName = "test-provider",
            ModelName = "test-model",
            RetentionExpiresAt = Now + MediaPolicy.StagedImageTimeToLive,
            CreatedAt = Now,
            StatusChangedAt = Now,
        });

        await db.SaveChangesAsync(Ct);

        return imageId;
    }

    [Fact]
    public async Task Each_workspace_holds_its_own_library_and_cannot_see_the_others()
    {
        // The same label, channel and prompt text in both: a prompt is creator IP, never deduplicated.
        var a = await SeedAsync(RecipeAggregateFixture.WorkspaceA, NewRecord(RecipeAggregateFixture.WorkspaceA, label: "Hero"));
        var b = await SeedAsync(RecipeAggregateFixture.WorkspaceB, NewRecord(RecipeAggregateFixture.WorkspaceB, label: "Hero"));

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        Assert.Equal([a.Id], (await db.PromptRecords.ToListAsync(Ct)).Select(record => record.Id));
        Assert.Null(await db.PromptRecords.FirstOrDefaultAsync(record => record.Id == b.Id, Ct));
    }

    [Fact]
    public async Task A_library_cannot_be_read_before_a_workspace_is_resolved()
    {
        await SeedAsync(RecipeAggregateFixture.WorkspaceA, NewRecord(RecipeAggregateFixture.WorkspaceA));

        await using var scope = _fixture.UnresolvedScope();
        var db = RecipeAggregateFixture.Db(scope);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.PromptRecords.ToListAsync(Ct));
    }

    [Fact]
    public async Task A_record_is_write_once()
    {
        var record = await SeedAsync(RecipeAggregateFixture.WorkspaceA, NewRecord(RecipeAggregateFixture.WorkspaceA));

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var loaded = await db.PromptRecords.SingleAsync(item => item.Id == record.Id, Ct);

        // Editing the text would make the row lie about what produced an image that may already be published.
        loaded.Text = "Something else entirely.";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_record_cannot_be_deleted_through_the_application()
    {
        var record = await SeedAsync(RecipeAggregateFixture.WorkspaceA, NewRecord(RecipeAggregateFixture.WorkspaceA));

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.PromptRecords.Remove(await db.PromptRecords.SingleAsync(item => item.Id == record.Id, Ct));

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Even_the_label_cannot_be_changed()
    {
        // Stated as a test because it is the documented cost of immutability, not an oversight: reorganising a
        // library later needs its own annotation row rather than an edit here.
        var record = await SeedAsync(RecipeAggregateFixture.WorkspaceA, NewRecord(RecipeAggregateFixture.WorkspaceA));

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var loaded = await db.PromptRecords.SingleAsync(item => item.Id == record.Id, Ct);
        loaded.Label = "Renamed";

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_recipe_pin_must_name_a_recipe_in_the_same_workspace()
    {
        var (recipeInB, _) = await SeedRecipeAsync(RecipeAggregateFixture.WorkspaceB);

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.PromptRecords.Add(NewRecord(RecipeAggregateFixture.WorkspaceA, recipeId: recipeInB));

        // Unrepresentable rather than merely refused: the foreign key is workspace-paired.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_version_pin_must_be_a_version_of_the_pinned_recipe()
    {
        var first = await SeedRecipeAsync(RecipeAggregateFixture.WorkspaceA);
        var second = await SeedRecipeAsync(RecipeAggregateFixture.WorkspaceA);

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.PromptRecords.Add(NewRecord(
            RecipeAggregateFixture.WorkspaceA, recipeId: first.RecipeId, recipeVersionId: second.VersionId));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_matching_recipe_and_version_pin_is_accepted()
    {
        var (recipeId, versionId) = await SeedRecipeAsync(RecipeAggregateFixture.WorkspaceA);

        var record = await SeedAsync(
            RecipeAggregateFixture.WorkspaceA,
            NewRecord(RecipeAggregateFixture.WorkspaceA, recipeId: recipeId, recipeVersionId: versionId));

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var loaded = await db.PromptRecords.SingleAsync(item => item.Id == record.Id, Ct);

        Assert.Equal(recipeId, loaded.RecipeId);
        Assert.Equal(versionId, loaded.RecipeVersionId);
    }

    [Fact]
    public async Task A_version_pin_cannot_name_another_workspaces_version()
    {
        // The version leg of "lineage is unrepresentable", in its cross-workspace form: A's recipe id paired with a
        // version row that belongs to B. The composite key carries the workspace, so the pair simply does not exist.
        var inA = await SeedRecipeAsync(RecipeAggregateFixture.WorkspaceA);
        var inB = await SeedRecipeAsync(RecipeAggregateFixture.WorkspaceB);

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.PromptRecords.Add(NewRecord(
            RecipeAggregateFixture.WorkspaceA, recipeId: inA.RecipeId, recipeVersionId: inB.VersionId));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_proposal_pin_cannot_name_another_workspaces_proposal()
    {
        // The third leg. The proposal is what makes a generated prompt's provenance checkable, so a record in A
        // citing B's proposal would be a provenance claim about work this workspace never did.
        var proposalInB = await SeedProposalAsync(RecipeAggregateFixture.WorkspaceB);

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var record = NewRecord(RecipeAggregateFixture.WorkspaceA, source: PromptRecordSource.ImagePromptComposition);
        record.AiProposalId = proposalInB;
        db.PromptRecords.Add(record);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_proposal_pin_in_the_same_workspace_is_accepted()
    {
        var proposal = await SeedProposalAsync(RecipeAggregateFixture.WorkspaceA);

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var record = NewRecord(RecipeAggregateFixture.WorkspaceA, source: PromptRecordSource.ImagePromptComposition);
        record.AiProposalId = proposal;
        db.PromptRecords.Add(record);
        await db.SaveChangesAsync(Ct);

        Assert.Equal(proposal, (await db.PromptRecords.AsNoTracking().SingleAsync(Ct)).AiProposalId);
    }

    [Fact]
    public async Task An_insert_naming_another_workspace_is_rejected_before_it_reaches_the_database()
    {
        // The ownership interceptor's job, proved for this entity: a row built with B's id while A is resolved is
        // refused rather than written. Every other test here sets the id to the resolved workspace, so without this
        // one the interceptor's refusal path would be untested for PromptRecord.
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.PromptRecords.Add(NewRecord(RecipeAggregateFixture.WorkspaceB));

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_version_pin_without_its_recipe_is_refused()
    {
        var (_, versionId) = await SeedRecipeAsync(RecipeAggregateFixture.WorkspaceA);

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.PromptRecords.Add(NewRecord(RecipeAggregateFixture.WorkspaceA, recipeVersionId: versionId));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task One_committed_image_holds_one_prompt_record()
    {
        // The schema's answer to "no duplicate prompt records on DAM retry": a retry loses the index.
        var imageId = await SeedGeneratedImageAsync(RecipeAggregateFixture.WorkspaceA);
        await SeedAsync(RecipeAggregateFixture.WorkspaceA, NewRecord(RecipeAggregateFixture.WorkspaceA, generatedImageId: imageId));

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.PromptRecords.Add(NewRecord(RecipeAggregateFixture.WorkspaceA, generatedImageId: imageId));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    /// <summary>
    /// Each workspace's own image holds its own prompt record, and the unique index on one does not reach the
    /// other.
    /// </summary>
    /// <remarks>
    /// <strong>This asked a weaker question until 12.6.</strong> It used to seed one fabricated id into both
    /// workspaces and assert the workspace-leading unique index did not refuse the second, because "the image
    /// tables do not exist yet to say whose it is". They exist now, and the composite foreign key makes that
    /// collision unconstructible: an image id resolves in exactly one workspace, so a shared id is no longer
    /// something a test can even set up. What is left to prove is that the index is still per-workspace, which
    /// is what two separate images each holding a record shows.
    /// </remarks>
    [Fact]
    public async Task Each_workspace_pins_its_own_image_without_colliding_with_the_other()
    {
        var mine = await SeedGeneratedImageAsync(RecipeAggregateFixture.WorkspaceA);
        var theirs = await SeedGeneratedImageAsync(RecipeAggregateFixture.WorkspaceB);

        await SeedAsync(RecipeAggregateFixture.WorkspaceA, NewRecord(RecipeAggregateFixture.WorkspaceA, generatedImageId: mine));
        await SeedAsync(RecipeAggregateFixture.WorkspaceB, NewRecord(RecipeAggregateFixture.WorkspaceB, generatedImageId: theirs));

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var only = Assert.Single(await RecipeAggregateFixture.Db(scope).PromptRecords.ToListAsync(Ct));

        Assert.Equal(mine, only.GeneratedImageId);
    }

    [Fact]
    public async Task Many_records_may_have_no_committed_image()
    {
        // The unique index is filtered, so an uncommitted prompt does not collide with every other one.
        await SeedAsync(RecipeAggregateFixture.WorkspaceA, NewRecord(RecipeAggregateFixture.WorkspaceA, label: "One"));
        await SeedAsync(RecipeAggregateFixture.WorkspaceA, NewRecord(RecipeAggregateFixture.WorkspaceA, label: "Two"));

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        Assert.Equal(2, await RecipeAggregateFixture.Db(scope).PromptRecords.CountAsync(Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_prompt_is_refused(string text)
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.PromptRecords.Add(NewRecord(RecipeAggregateFixture.WorkspaceA, text: text));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_channel_key_is_refused(string channelKey)
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.PromptRecords.Add(NewRecord(RecipeAggregateFixture.WorkspaceA, channelKey: channelKey));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_manual_prompt_may_not_claim_a_template_or_a_draft()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var withTemplate = NewRecord(RecipeAggregateFixture.WorkspaceA);
        withTemplate.PromptTemplateId = "image.prompt";
        db.PromptRecords.Add(withTemplate);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        db.ChangeTracker.Clear();

        var withDraft = NewRecord(RecipeAggregateFixture.WorkspaceA);
        withDraft.GeneratedText = "A draft nobody generated.";
        db.PromptRecords.Add(withDraft);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_generated_prompt_needs_the_whole_template_pin()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        // A version without the checksum is the case the triple exists to refuse: a version string alone is a claim.
        var record = NewRecord(RecipeAggregateFixture.WorkspaceA, source: PromptRecordSource.ImagePromptComposition);
        record.AiProposalId = null;
        record.PromptTemplateBodyChecksum = null;
        db.PromptRecords.Add(record);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_generated_prompt_must_name_its_proposal()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        // AiProposalId is left null by NewRecord, so a non-manual source alone violates the provenance constraint.
        db.PromptRecords.Add(NewRecord(
            RecipeAggregateFixture.WorkspaceA, source: PromptRecordSource.PhotographyConcept));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Theory]
    [InlineData(9, 0)]
    [InlineData(0, 9)]
    public async Task A_source_or_kind_outside_its_enum_is_refused(int source, int imageKind)
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var record = NewRecord(RecipeAggregateFixture.WorkspaceA);
        record.Source = (PromptRecordSource)source;
        record.ImageKind = (PromptImageKind)imageKind;

        // A non-manual source also needs a proposal and a template, so give it them to isolate the range check.
        if (source != 0)
        {
            record.GeneratedText = record.Text;
            record.PromptTemplateId = "image.prompt";
            record.PromptTemplateVersion = "1.0.0";
            record.PromptTemplateBodyChecksum = $"sha256:{new string('a', 64)}";
        }

        db.PromptRecords.Add(record);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }
}
