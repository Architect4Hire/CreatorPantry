using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Ingredients.Data.Configurations;

internal sealed class IngredientDietaryTraitConfiguration : IEntityTypeConfiguration<IngredientDietaryTrait>
{
    public void Configure(EntityTypeBuilder<IngredientDietaryTrait> builder)
    {
        var notedStates = string.Join(
            ", ",
            Enum.GetValues<DietaryCompatibility>()
                .Where(TraitPolicy.RequiresEvidenceNote)
                .Select(compatibility => (int)compatibility));

        builder.ToTable("IngredientDietaryTraits", table =>
        {
            // As on the allergen and density tables: a model-generated claim is recordable but never approvable.
            table.HasCheckConstraint(
                "CK_IngredientDietaryTraits_AiEstimate_NotApproved",
                $"NOT (ReferenceSourceKind = {(int)ReferenceSourceKind.AiEstimated} "
                    + $"AND ReviewStatus = {(int)TraitReviewStatus.Approved})");

            // "It depends" is useless, and quietly alarming, unless the row says what it depends on.
            table.HasCheckConstraint(
                "CK_IngredientDietaryTraits_EvidenceNote_Required",
                $"Compatibility NOT IN ({notedStates}) OR EvidenceNote <> '{TraitPolicy.NoEvidenceNote}'");

            // Note what is deliberately absent: there is no counterpart to
            // CK_IngredientAllergenTraits_Absence_RequiresVettedSource. A community claim that an ingredient is
            // vegan is an ordinary unvetted fact, correctable in review. The allergen table restricts absence
            // claims because being wrong there reaches an allergic person, and that asymmetry is the model.
        });

        builder.HasKey(trait => trait.Id);

        builder.Property(trait => trait.Compatibility)
            .IsRequired();

        builder.Property(trait => trait.ReferenceSourceKind)
            .IsRequired();

        // Never nullable; see IngredientAllergenTraitConfiguration.
        builder.Property(trait => trait.EvidenceNote)
            .IsRequired()
            .HasMaxLength(TraitPolicy.EvidenceNoteMaxLength);

        builder.Property(trait => trait.EffectiveFrom)
            .IsRequired();

        builder.Property(trait => trait.ReviewStatus)
            .IsRequired();

        builder.HasOne<Ingredient>()
            .WithMany()
            .HasForeignKey(trait => trait.IngredientId)
            .OnDelete(DeleteBehavior.Cascade);

        // Retiring a profile must not delete the evidence recorded against it.
        builder.HasOne<DietaryProfile>()
            .WithMany()
            .HasForeignKey(trait => trait.DietaryProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ReferenceSource>()
            .WithMany()
            .HasForeignKey(trait => new { trait.ReferenceSourceId, trait.ReferenceSourceKind })
            .HasPrincipalKey(source => new { source.Id, source.Kind })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(trait => new
            {
                trait.IngredientId,
                trait.DietaryProfileId,
                trait.ReferenceSourceId,
                trait.EffectiveFrom,
            })
            .IsUnique()
            .HasDatabaseName("UX_IngredientDietaryTraits_Ingredient_Profile_Source_Effective");

        builder.HasIndex(trait => new { trait.IngredientId, trait.ReviewStatus, trait.EffectiveFrom })
            .HasDatabaseName("IX_IngredientDietaryTraits_Ingredient_Status_Effective");
    }
}
