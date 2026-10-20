using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Nutrition.Data.Entities;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Nutrition.Data.Configurations;

internal sealed class NutritionMappingConfiguration : IEntityTypeConfiguration<NutritionMapping>
{
    public void Configure(EntityTypeBuilder<NutritionMapping> builder)
    {
        const int candidate = (int)NutritionMappingStatus.Candidate;
        const int confirmed = (int)NutritionMappingStatus.Confirmed;
        const int needsReview = (int)NutritionMappingStatus.NeedsReview;
        const int unresolved = (int)NutritionMappingStatus.Unresolved;
        const int automatic = (int)NutritionMatchMethod.AutomaticSearch;

        builder.ToTable("NutritionMappings", table =>
        {
            table.HasCheckConstraint(
                "CK_NutritionMappings_Status_Range",
                $"Status IN ({candidate}, {confirmed}, {needsReview}, {unresolved})");

            // A reference is whole or absent, and absent exactly when unresolved. Every comparison is guarded
            // by IS NULL / IS NOT NULL: a CHECK passes on UNKNOWN, so a bare comparison with a NULL proves nothing.
            table.HasCheckConstraint(
                "CK_NutritionMappings_Reference_Whole",
                $"(Status = {unresolved} AND ReferenceSourceId IS NULL AND SourceCode IS NULL AND ExternalRecordId IS NULL "
                    + "AND ReferenceDescription IS NULL AND SourceVersion IS NULL AND MatchMethod IS NULL) OR "
                    + $"(Status <> {unresolved} AND ReferenceSourceId IS NOT NULL AND SourceCode IS NOT NULL "
                    + "AND ExternalRecordId IS NOT NULL AND ReferenceDescription IS NOT NULL AND SourceVersion IS NOT NULL "
                    + "AND MatchMethod IS NOT NULL)");

            // A confidence belongs to an automatic proposal, and an automatic proposal has one.
            table.HasCheckConstraint(
                "CK_NutritionMappings_Confidence_Automatic",
                $"(Confidence IS NULL AND (MatchMethod IS NULL OR MatchMethod <> {automatic})) OR "
                    + $"(Confidence IS NOT NULL AND MatchMethod IS NOT NULL AND MatchMethod = {automatic})");

            table.HasCheckConstraint(
                "CK_NutritionMappings_ReviewVersion_NeedsReview",
                $"(Status = {needsReview} AND ReviewSourceVersion IS NOT NULL) OR "
                    + $"(Status <> {needsReview} AND ReviewSourceVersion IS NULL)");

            table.HasCheckConstraint(
                "CK_NutritionMappings_UnresolvedReason",
                $"(Status = {unresolved} AND UnresolvedReason IS NOT NULL) OR "
                    + $"(Status <> {unresolved} AND UnresolvedReason IS NULL)");

            // A confirmation is kept while the confirmed reference is under review, and exists only then.
            table.HasCheckConstraint(
                "CK_NutritionMappings_Confirmation",
                $"(Status IN ({confirmed}, {needsReview}) AND ConfirmedAt IS NOT NULL AND ConfirmedByMembershipId IS NOT NULL) OR "
                    + $"(Status NOT IN ({confirmed}, {needsReview}) AND ConfirmedAt IS NULL AND ConfirmedByMembershipId IS NULL)");

            // A portion is whole or absent, weighs something, and needs a reference to be a portion of.
            table.HasCheckConstraint(
                "CK_NutritionMappings_Portion_Whole",
                "(PortionMeasurementUnitId IS NULL AND PortionAmount IS NULL AND PortionGrams IS NULL "
                    + "AND PortionOrigin IS NULL AND PortionLabel IS NULL) OR "
                    + "(PortionMeasurementUnitId IS NOT NULL AND PortionAmount IS NOT NULL AND PortionAmount > 0 "
                    + "AND PortionGrams IS NOT NULL AND PortionGrams > 0 AND PortionOrigin IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_NutritionMappings_Portion_NeedsReference",
                $"Status <> {unresolved} OR PortionMeasurementUnitId IS NULL");
        });

        builder.HasKey(mapping => mapping.Id);
        builder.Property(mapping => mapping.Id).ValueGeneratedNever();

        // Target of the rejections' composite key, so a rejection cannot claim another workspace's mapping.
        builder.HasAlternateKey(mapping => new { mapping.WorkspaceId, mapping.Id })
            .HasName("AK_NutritionMappings_Workspace_Id");

        builder.Property(mapping => mapping.Status).IsRequired();
        builder.Property(mapping => mapping.SourceCode).HasMaxLength(NutritionMappingPolicy.SourceCodeMaxLength);
        builder.Property(mapping => mapping.ExternalRecordId).HasMaxLength(NutritionMappingPolicy.ExternalRecordIdMaxLength);
        builder.Property(mapping => mapping.ReferenceDescription).HasMaxLength(NutritionMappingPolicy.DescriptionMaxLength);
        builder.Property(mapping => mapping.SourceVersion).HasMaxLength(NutritionMappingPolicy.SourceVersionMaxLength);
        builder.Property(mapping => mapping.ReviewSourceVersion).HasMaxLength(NutritionMappingPolicy.SourceVersionMaxLength);
        builder.Property(mapping => mapping.PortionAmount)
            .HasPrecision(NutritionMappingPolicy.PortionPrecision, NutritionMappingPolicy.PortionScale);
        builder.Property(mapping => mapping.PortionGrams)
            .HasPrecision(NutritionMappingPolicy.PortionPrecision, NutritionMappingPolicy.PortionScale);
        builder.Property(mapping => mapping.PortionLabel).HasMaxLength(NutritionMappingPolicy.PortionLabelMaxLength);
        builder.Property(mapping => mapping.CreatedAt).IsRequired();
        builder.Property(mapping => mapping.UpdatedAt).IsRequired();
        builder.Property(mapping => mapping.RowVersion).IsRowVersion();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(mapping => mapping.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Platform reference data, referenced and never owned: none of these can be deleted from under a mapping.
        builder.HasOne<Ingredient>()
            .WithMany()
            .HasForeignKey(mapping => mapping.IngredientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ReferenceSource>()
            .WithMany()
            .HasForeignKey(mapping => mapping.ReferenceSourceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MeasurementUnit>()
            .WithMany()
            .HasForeignKey(mapping => mapping.PortionMeasurementUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(mapping => mapping.Rejections)
            .WithOne()
            .HasForeignKey(rejection => new { rejection.WorkspaceId, rejection.NutritionMappingId })
            .HasPrincipalKey(mapping => new { mapping.WorkspaceId, mapping.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // One decision per ingredient per workspace: the same ingredient in two workspaces is two decisions.
        builder.HasIndex(mapping => new { mapping.WorkspaceId, mapping.IngredientId })
            .IsUnique()
            .HasDatabaseName("UX_NutritionMappings_Workspace_Ingredient");

        // The mappings that need a creator: candidates, reviews and unresolved ones.
        builder.HasIndex(mapping => new { mapping.WorkspaceId, mapping.Status })
            .HasDatabaseName("IX_NutritionMappings_Workspace_Status");
    }
}
