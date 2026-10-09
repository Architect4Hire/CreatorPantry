using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Media;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// One of every record a creative context can point at, for the SQLite and SQL Server suites alike.
/// </summary>
/// <remarks>
/// Every reference but two is a workspace-paired foreign key, so a fabricated id is refused before the rule a
/// test is about gets a say. Each method seeds the smallest row its constraints allow, into whichever workspace
/// the supplied context is resolved to, and leaves <c>WorkspaceId</c> to the ownership interceptor.
/// </remarks>
internal static class CreativeContextSeeds
{
    public static CreativeContext NewContext(DateTimeOffset now, string? title = "Soda bread, autumn") => new()
    {
        Id = Guid.NewGuid(),
        WorkingTitle = title,
        PictureBrief = "Overhead, the loaf torn open on linen, soft window light.",
        Day = DayOfWeek.Monday,
        WeeklyThemeKey = "meat-free-monday",
        CreatedByMembershipId = Guid.NewGuid(),
        CreatedAt = now,
        UpdatedAt = now,
    };

    public static CreativeContextChannel Channel(CreativeContext context, string key, int sortOrder) => new()
    {
        Id = Guid.NewGuid(),
        CreativeContextId = context.Id,
        ChannelKey = key,
        SortOrder = sortOrder,
    };

    public static CreativeContextReference Reference(
        CreativeContext context,
        CreativeContextReferenceKind kind,
        int sortOrder,
        DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            CreativeContextId = context.Id,
            Kind = kind,
            SortOrder = sortOrder,
            AddedAt = now,
        };

    public static async Task<(Guid RecipeId, Guid VersionId)> RecipeAsync(
        CreatorPantryDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Soda bread",
            Status = RecipeStatus.Draft,
            CreatedByMembershipId = Guid.NewGuid(),
            UpdatedByMembershipId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(cancellationToken);

        var version = new RecipeVersion
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            VersionNumber = 1,
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = now,
        };
        db.RecipeVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);

        return (recipe.Id, version.Id);
    }

    /// <summary>A concept request. The concept inside it is not a row, so any id stands for one.</summary>
    public static async Task<Guid> ConceptRequestAsync(
        CreatorPantryDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();

        db.AiOperations.Add(new AiOperation
        {
            Id = operationId,
            TaskType = AiTaskType.RecipeConcepts,
            Scope = AiOperationScope.NotApplicable,
            Status = AiOperationStatus.Proposed,
            IdempotencyKey = $"concepts-{operationId}",
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = now,
            StatusChangedAt = now,
            AvailableAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);

        return operationId;
    }

    /// <summary>
    /// A concept request that has answered, and one concept it offered — what a lookup through the Ai module
    /// has to find for a concept to count as real.
    /// </summary>
    public static async Task<(Guid RequestId, Guid ConceptId)> ConceptAsync(
        CreatorPantryDbContext db,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string title = "Brown-butter soda bread",
        string? summary = null)
    {
        var requestId = await ConceptRequestAsync(db, now, cancellationToken);
        var proposalId = Guid.NewGuid();
        var conceptId = Guid.NewGuid();

        db.AiProposals.Add(new AiProposal
        {
            Id = proposalId,
            AiOperationId = requestId,
            OutputSchemaVersion = "recipe.concepts.v1",
            PromptTemplateId = "recipe.concepts",
            PromptTemplateVersion = "1.0.0",
            PromptTemplateBodyChecksum = "sha256:seed",
            ProviderName = "test-provider",
            ModelName = "test-model",
            CreatedAt = now,
        });

        db.AiStructuredChanges.Add(new AiStructuredChange
        {
            Id = Guid.NewGuid(),
            AiProposalId = proposalId,
            ChangeKind = AiChangeKind.Add,
            TargetKind = AiChangeTargetKind.RecipeConcept,
            TargetId = conceptId,
            AfterValue = title,
            ProposedPosition = 0,
            SortOrder = 0,
        });

        if (summary is not null)
        {
            db.AiStructuredChanges.Add(new AiStructuredChange
            {
                Id = Guid.NewGuid(),
                AiProposalId = proposalId,
                ChangeKind = AiChangeKind.Set,
                TargetKind = AiChangeTargetKind.RecipeConcept,
                TargetId = conceptId,
                FieldName = AiConceptFields.Summary,
                AfterValue = summary,
                SortOrder = 1,
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        return (requestId, conceptId);
    }

    /// <summary>A DAM asset with its first version.</summary>
    public static async Task<Guid> MediaAssetAsync(
        CreatorPantryDbContext db, Guid workspaceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var asset = SeededMediaAsset.For(workspaceId, at: now);
        db.MediaAssets.Add(asset);
        db.MediaAssetVersions.Add(SeededMediaAsset.VersionOf(asset));
        await db.SaveChangesAsync(cancellationToken);

        return asset.Id;
    }

    public static async Task<Guid> GeneratedImageAsync(
        CreatorPantryDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var imageId = Guid.NewGuid();

        db.GeneratedImageOperations.Add(new GeneratedImageOperation
        {
            Id = operationId,
            Status = GeneratedImageOperationStatus.Succeeded,
            PromptText = "Overhead shot of soda bread on linen.",
            VariantCount = 1,
            IdempotencyKey = $"image-{operationId}",
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = now,
            StatusChangedAt = now,
            AvailableAt = now,
        });

        db.GeneratedImages.Add(new GeneratedImage
        {
            Id = imageId,
            GeneratedImageOperationId = operationId,
            VariantIndex = 0,
            Status = GeneratedImageStatus.Staged,
            ObjectKey = $"staging/{imageId:N}.png",
            MediaType = "image/png",
            Width = 1024,
            Height = 1024,
            SizeBytes = 2048,
            ContentChecksum = "sha256:" + new string('a', 64),
            ProviderName = "test-provider",
            ModelName = "test-model",
            RetentionExpiresAt = now.AddDays(7),
            CreatedAt = now,
            StatusChangedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);

        return imageId;
    }

    public static async Task<Guid> PromptRecordAsync(
        CreatorPantryDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var record = new PromptRecord
        {
            Id = Guid.NewGuid(),
            ChannelKey = "instagram",
            ImageKind = PromptImageKind.Hero,
            Text = "Overhead shot of soda bread on linen, soft window light.",
            Source = PromptRecordSource.Manual,
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = now,
        };
        db.PromptRecords.Add(record);
        await db.SaveChangesAsync(cancellationToken);

        return record.Id;
    }
}
