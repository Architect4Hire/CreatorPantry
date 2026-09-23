using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Ingredients.Data.Configurations;

internal sealed class IngredientAliasConfiguration : IEntityTypeConfiguration<IngredientAlias>
{
    public void Configure(EntityTypeBuilder<IngredientAlias> builder)
    {
        builder.ToTable("IngredientAliases");
        builder.HasKey(alias => alias.Id);

        builder.Property(alias => alias.Alias)
            .IsRequired()
            .HasMaxLength(IngredientPolicy.NameMaxLength);

        builder.Property(alias => alias.NormalizedAlias)
            .IsRequired()
            .HasMaxLength(IngredientPolicy.NameMaxLength);

        // An alias has no meaning without its ingredient, so it goes when the ingredient goes.
        builder.HasOne<Ingredient>()
            .WithMany()
            .HasForeignKey(alias => alias.IngredientId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique across every ingredient, not per ingredient: an alias matching two ingredients would make a
        // recipe line ambiguous. See IngredientAlias.NormalizedAlias for the one collision this cannot catch.
        builder.HasIndex(alias => alias.NormalizedAlias)
            .IsUnique()
            .HasDatabaseName("UX_IngredientAliases_NormalizedAlias");

        // Listing one ingredient's aliases; also covers the foreign key, which the unique index does not.
        builder.HasIndex(alias => alias.IngredientId)
            .HasDatabaseName("IX_IngredientAliases_IngredientId");
    }
}
