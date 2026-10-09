using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class CreativeContextReferenceConfiguration : IEntityTypeConfiguration<CreativeContextReference>
{
    /// <summary>Every id column a reference can carry. A kind sets its own and leaves the rest null.</summary>
    private static readonly string[] TargetColumns =
    [
        nameof(CreativeContextReference.RecipeId),
        nameof(CreativeContextReference.RecipeVersionId),
        nameof(CreativeContextReference.ConceptRequestId),
        nameof(CreativeContextReference.ConceptId),
        nameof(CreativeContextReference.MediaAssetId),
        nameof(CreativeContextReference.MediaAssetVersionNumber),
        nameof(CreativeContextReference.GeneratedImageId),
        nameof(CreativeContextReference.PromptRecordId),
        nameof(CreativeContextReference.SocialPackageId),
    ];

    public void Configure(EntityTypeBuilder<CreativeContextReference> builder)
    {
        builder.ToTable("CreativeContextReferences", table =>
        {
            table.HasCheckConstraint("CK_CreativeContextReferences_SortOrder_NonNegative", "SortOrder >= 0");

            // Stored as an integer, and an enum column accepts any integer. Zero is not a kind.
            table.HasCheckConstraint(
                "CK_CreativeContextReferences_Kind_Range",
                $"Kind >= {(int)CreativeContextReferenceKind.Recipe} AND "
                    + $"Kind <= {(int)CreativeContextReferenceKind.SocialPackage}");

            // The same reasoning for the purpose, and the same reason it starts at one: a row whose purpose
            // was never set must be refused rather than read as a source the work draws on.
            table.HasCheckConstraint(
                "CK_CreativeContextReferences_Purpose_Range",
                $"Purpose >= {(int)CreativeContextReferencePurpose.Source} AND "
                    + $"Purpose <= {(int)CreativeContextReferencePurpose.Keeper}");

            // One row, one kind, and only that kind's columns. Without it a row could name a recipe and an
            // image at once, and nothing reading it could say which id was the real one.
            table.HasCheckConstraint(
                "CK_CreativeContextReferences_Kind_Columns",
                string.Join(
                    " OR ",
                    Shape(
                        CreativeContextReferenceKind.Recipe,
                        required: [nameof(CreativeContextReference.RecipeId)],
                        optional: [nameof(CreativeContextReference.RecipeVersionId)]),
                    Shape(
                        CreativeContextReferenceKind.RecipeConcept,
                        required:
                        [
                            nameof(CreativeContextReference.ConceptRequestId),
                            nameof(CreativeContextReference.ConceptId),
                        ]),
                    Shape(
                        CreativeContextReferenceKind.DamAsset,
                        required: [nameof(CreativeContextReference.MediaAssetId)],
                        optional: [nameof(CreativeContextReference.MediaAssetVersionNumber)]),
                    Shape(
                        CreativeContextReferenceKind.GeneratedImage,
                        required: [nameof(CreativeContextReference.GeneratedImageId)]),
                    Shape(
                        CreativeContextReferenceKind.PromptRecord,
                        required: [nameof(CreativeContextReference.PromptRecordId)]),
                    Shape(
                        CreativeContextReferenceKind.SocialPackage,
                        required: [nameof(CreativeContextReference.SocialPackageId)])));

            // Version numbers start at one. Zero or a negative could never match a version, so it is refused
            // here with a name rather than surfacing as a foreign-key violation.
            table.HasCheckConstraint(
                "CK_CreativeContextReferences_VersionPin_Positive",
                "MediaAssetVersionNumber IS NULL OR MediaAssetVersionNumber >= 1");
        });

        builder.HasKey(reference => reference.Id);

        // Set by the application, never the store. See CreativeContextChannelConfiguration.
        builder.Property(reference => reference.Id).ValueGeneratedNever();

        builder.Property(reference => reference.Kind).IsRequired();

        // Defaulted in the store as well as in Business, because the column arrives on a table that already
        // has rows: every reference made before AF.4.3 is something the work draws on. The sentinel is stated
        // rather than left implicit — zero is not a purpose, so a row that reaches an insert without one takes
        // the store's default, and the range check never sees a zero.
        builder.Property(reference => reference.Purpose)
            .IsRequired()
            .HasDefaultValue(CreativeContextReferencePurpose.Source)
            .HasSentinel(default(CreativeContextReferencePurpose));

        builder.Property(reference => reference.SortOrder).IsRequired();
        builder.Property(reference => reference.AddedAt).IsRequired();

        // Reaches the workspace only through its context, so there is one cascade path into this table. Every
        // key below is restricted for that reason as much as for the obvious one: each target cascades from
        // the same workspace, and a second cascade path is what SQL Server refuses.
        builder.HasOne<CreativeContext>()
            .WithMany(context => context.References)
            .HasForeignKey(reference => new { reference.WorkspaceId, reference.CreativeContextId })
            .HasPrincipalKey(context => new { context.WorkspaceId, context.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace-paired, so one workspace's context cannot name another's recipe — unrepresentable rather
        // than merely refused.
        builder.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(reference => new { reference.WorkspaceId, reference.RecipeId })
            .HasPrincipalKey(recipe => new { recipe.WorkspaceId, recipe.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // A version of *that recipe*, in this workspace.
        builder.HasOne<RecipeVersion>()
            .WithMany()
            .HasForeignKey(reference => new { reference.WorkspaceId, reference.RecipeId, reference.RecipeVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.RecipeId, version.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // The concept request. The concept inside it is not a row, so ConceptId has no key of its own.
        builder.HasOne<AiOperation>()
            .WithMany()
            .HasForeignKey(reference => new { reference.WorkspaceId, reference.ConceptRequestId })
            .HasPrincipalKey(operation => new { operation.WorkspaceId, operation.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MediaAsset>()
            .WithMany()
            .HasForeignKey(reference => new { reference.WorkspaceId, reference.MediaAssetId })
            .HasPrincipalKey(asset => new { asset.WorkspaceId, asset.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // The pin: unpinned rows carry a null and are not checked, and a pinned one cannot name a version the
        // asset does not have.
        builder.HasOne<MediaAssetVersion>()
            .WithMany()
            .HasForeignKey(reference => new
            {
                reference.WorkspaceId,
                reference.MediaAssetId,
                reference.MediaAssetVersionNumber,
            })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.MediaAssetId, version.VersionNumber })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<GeneratedImage>()
            .WithMany()
            .HasForeignKey(reference => new { reference.WorkspaceId, reference.GeneratedImageId })
            .HasPrincipalKey(image => new { image.WorkspaceId, image.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PromptRecord>()
            .WithMany()
            .HasForeignKey(reference => new { reference.WorkspaceId, reference.PromptRecordId })
            .HasPrincipalKey(record => new { record.WorkspaceId, record.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(reference => new { reference.WorkspaceId, reference.CreativeContextId, reference.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_CreativeContextReferences_Workspace_Context_Order");

        // A context names a given source once. One filtered index per kind, because each kind's identity is a
        // different column and a single index over all of them would treat every null as a collision.
        builder.HasIndex(reference => new { reference.WorkspaceId, reference.CreativeContextId, reference.RecipeId })
            .IsUnique()
            .HasFilter("RecipeId IS NOT NULL")
            .HasDatabaseName("UX_CreativeContextReferences_Context_Recipe");

        builder.HasIndex(reference => new
            {
                reference.WorkspaceId,
                reference.CreativeContextId,
                reference.ConceptRequestId,
                reference.ConceptId,
            })
            .IsUnique()
            .HasFilter("ConceptId IS NOT NULL")
            .HasDatabaseName("UX_CreativeContextReferences_Context_Concept");

        // The two picture kinds are unique per *purpose* rather than per target, and they are the only ones
        // that are. A work takes cues from a picture and also makes pictures of its own, so one picture can
        // honestly be both — this run's output, and the cue the next prompt is planned from (AF.4.3). Naming
        // it once would force the two facts to overwrite each other, and the surface that replaces a cue would
        // then remove a keeper.
        builder.HasIndex(reference => new
            {
                reference.WorkspaceId,
                reference.CreativeContextId,
                reference.MediaAssetId,
                reference.Purpose,
            })
            .IsUnique()
            .HasFilter("MediaAssetId IS NOT NULL")
            .HasDatabaseName("UX_CreativeContextReferences_Context_MediaAsset");

        builder.HasIndex(reference => new
            {
                reference.WorkspaceId,
                reference.CreativeContextId,
                reference.GeneratedImageId,
                reference.Purpose,
            })
            .IsUnique()
            .HasFilter("GeneratedImageId IS NOT NULL")
            .HasDatabaseName("UX_CreativeContextReferences_Context_GeneratedImage");

        builder.HasIndex(reference => new { reference.WorkspaceId, reference.CreativeContextId, reference.PromptRecordId })
            .IsUnique()
            .HasFilter("PromptRecordId IS NOT NULL")
            .HasDatabaseName("UX_CreativeContextReferences_Context_PromptRecord");

        builder.HasIndex(reference => new { reference.WorkspaceId, reference.CreativeContextId, reference.SocialPackageId })
            .IsUnique()
            .HasFilter("SocialPackageId IS NOT NULL")
            .HasDatabaseName("UX_CreativeContextReferences_Context_SocialPackage");
    }

    /// <summary>
    /// One kind's clause: its required columns set, its optional ones free, and every other column null.
    /// </summary>
    private static string Shape(CreativeContextReferenceKind kind, string[] required, string[]? optional = null)
    {
        var free = optional ?? [];

        var terms = TargetColumns
            .Where(column => !free.Contains(column))
            .Select(column => required.Contains(column) ? $"{column} IS NOT NULL" : $"{column} IS NULL");

        return $"(Kind = {(int)kind} AND {string.Join(" AND ", terms)})";
    }
}
