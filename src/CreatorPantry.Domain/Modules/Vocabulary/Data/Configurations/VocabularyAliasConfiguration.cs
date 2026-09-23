using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Configurations;

/// <summary>
/// The mapping every <see cref="CreatorPantry.Domain.Modules.Vocabulary.Data.Entities.VocabularyAlias"/> shares: its own table, a cascading foreign key to the
/// entry it resolves to, and a lookup key unique across the whole vocabulary.
/// </summary>
/// <param name="tableName">The table and the prefix its index names are built from.</param>
/// <param name="ownerColumnName">
/// The column <see cref="VocabularyAlias.VocabularyId"/> is stored in — <c>CuisineId</c>, <c>CourseId</c>
/// — so the table reads plainly to anyone looking at the database rather than at this class.
/// </param>
internal abstract class VocabularyAliasConfiguration<TAlias, TVocabulary>(string tableName, string ownerColumnName)
    : IEntityTypeConfiguration<TAlias>
    where TAlias : VocabularyAlias
    where TVocabulary : ControlledVocabulary
{
    public void Configure(EntityTypeBuilder<TAlias> builder)
    {
        builder.ToTable(tableName);
        builder.HasKey(alias => alias.Id);

        builder.Property(alias => alias.VocabularyId)
            .HasColumnName(ownerColumnName);

        builder.Property(alias => alias.Alias)
            .IsRequired()
            .HasMaxLength(VocabularyPolicy.AliasMaxLength);

        builder.Property(alias => alias.NormalizedAlias)
            .IsRequired()
            .HasMaxLength(VocabularyPolicy.AliasMaxLength);

        // An alias has no meaning without the entry it names, so it goes when that entry goes.
        builder.HasOne<TVocabulary>()
            .WithMany()
            .HasForeignKey(alias => alias.VocabularyId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique across the vocabulary, not per entry: an alias matching two entries would make a recipe's
        // cuisine — or course, or technique — ambiguous, and there is no context at this layer to break the
        // tie. See VocabularyAlias.NormalizedAlias for the one collision this cannot catch.
        builder.HasIndex(alias => alias.NormalizedAlias)
            .IsUnique()
            .HasDatabaseName($"UX_{tableName}_NormalizedAlias");

        // Listing one entry's aliases; also covers the foreign key, which the unique index does not.
        builder.HasIndex(alias => alias.VocabularyId)
            .HasDatabaseName($"IX_{tableName}_{ownerColumnName}");
    }
}
