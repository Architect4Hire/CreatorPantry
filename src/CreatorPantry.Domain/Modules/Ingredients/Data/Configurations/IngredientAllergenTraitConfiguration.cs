using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Ingredients.Data.Configurations;

internal sealed class IngredientAllergenTraitConfiguration : IEntityTypeConfiguration<IngredientAllergenTrait>
{
    public void Configure(EntityTypeBuilder<IngredientAllergenTrait> builder)
    {
        // Built from the policy predicates rather than hand-written literals, so the rule has one definition
        // and the SQL cannot drift from the code that later validates the same thing. Enum values are emitted
        // as their integers and identifiers left unquoted, keeping every expression valid on SQL Server and on
        // the SQLite database the constraint tests run against.
        var notedStates = States(TraitPolicy.RequiresEvidenceNote);
        var unvettedKinds = Kinds(kind => !TraitPolicy.MayAssertAllergenAbsence(kind));

        builder.ToTable("IngredientAllergenTraits", table =>
        {
            // The same rule density carries, and for the same reason: a model-generated claim may be recorded
            // but can never reach Approved, which is the only state an analysis will act on. Backed by the
            // composite foreign key below, so a row cannot misreport its source's kind to slip past this.
            table.HasCheckConstraint(
                "CK_IngredientAllergenTraits_AiEstimate_NotApproved",
                $"NOT (ReferenceSourceKind = {(int)ReferenceSourceKind.AiEstimated} "
                    + $"AND ReviewStatus = {(int)TraitReviewStatus.Approved})");

            // The asymmetric one, and the reason this table is worth its complexity. A wrong Present costs a
            // creator an ingredient; a wrong absence reaches somebody who is allergic. So the two source kinds
            // that amount to hearsay — an unvetted contributor and a model — cannot record an absence claim at
            // all. Not "cannot have it approved": cannot store it (recipes.md, ai.md).
            table.HasCheckConstraint(
                "CK_IngredientAllergenTraits_Absence_RequiresVettedSource",
                $"NOT (Presence = {(int)AllergenPresence.NotListedBySource} "
                    + $"AND ReferenceSourceKind IN ({unvettedKinds}))");

            // PossiblePresence and NotListedBySource are the two states a reader could mistake for safety
            // guidance, so neither may be stored bare: each must say what the source actually stated.
            table.HasCheckConstraint(
                "CK_IngredientAllergenTraits_EvidenceNote_Required",
                $"Presence NOT IN ({notedStates}) OR EvidenceNote <> '{TraitPolicy.NoEvidenceNote}'");
        });

        builder.HasKey(trait => trait.Id);

        builder.Property(trait => trait.Presence)
            .IsRequired();

        builder.Property(trait => trait.ReferenceSourceKind)
            .IsRequired();

        // Never nullable: the note-required constraint compares against '', and a NULL would make that
        // comparison evaluate to unknown and pass silently on both providers.
        builder.Property(trait => trait.EvidenceNote)
            .IsRequired()
            .HasMaxLength(TraitPolicy.EvidenceNoteMaxLength);

        builder.Property(trait => trait.EffectiveFrom)
            .IsRequired();

        builder.Property(trait => trait.ReviewStatus)
            .IsRequired();

        // A trait is about its ingredient and means nothing without it.
        builder.HasOne<Ingredient>()
            .WithMany()
            .HasForeignKey(trait => trait.IngredientId)
            .OnDelete(DeleteBehavior.Cascade);

        // Retiring an allergen must not delete the evidence recorded against it: IsActive removes it from
        // pickers, and the traits stay readable and auditable.
        builder.HasOne<Allergen>()
            .WithMany()
            .HasForeignKey(trait => trait.AllergenId)
            .OnDelete(DeleteBehavior.Restrict);

        // Onto (Id, Kind) rather than the primary key alone, which is what lets the two provenance check
        // constraints above trust the locally stored kind.
        builder.HasOne<ReferenceSource>()
            .WithMany()
            .HasForeignKey(trait => new { trait.ReferenceSourceId, trait.ReferenceSourceKind })
            .HasPrincipalKey(source => new { source.Id, source.Kind })
            .OnDelete(DeleteBehavior.Restrict);

        // The natural key, permissive by intent: two sources may disagree about the same ingredient and
        // allergen, and one source may revise itself on a later date. Both are real and both are recorded.
        builder.HasIndex(trait => new
            {
                trait.IngredientId,
                trait.AllergenId,
                trait.ReferenceSourceId,
                trait.EffectiveFrom,
            })
            .IsUnique()
            .HasDatabaseName("UX_IngredientAllergenTraits_Ingredient_Allergen_Source_Effective");

        // Reading everything known about one ingredient, which is how an analysis asks the question: it looks
        // for evidence and treats whatever it does not find as unknown.
        builder.HasIndex(trait => new { trait.IngredientId, trait.ReviewStatus, trait.EffectiveFrom })
            .HasDatabaseName("IX_IngredientAllergenTraits_Ingredient_Status_Effective");
    }

    private static string States(Func<AllergenPresence, bool> predicate) =>
        string.Join(", ", Enum.GetValues<AllergenPresence>().Where(predicate).Select(presence => (int)presence));

    private static string Kinds(Func<ReferenceSourceKind, bool> predicate) =>
        string.Join(", ", Enum.GetValues<ReferenceSourceKind>().Where(predicate).Select(kind => (int)kind));
}
