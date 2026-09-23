using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Configurations;

/// <summary>
/// The mapping every <see cref="ControlledVocabulary"/> shares: its own table, a permanent unique code, a
/// required display name, and an active flag.
/// </summary>
/// <remarks>
/// Written once and closed per vocabulary below, rather than copied four times, so the rules that make a
/// code a stable key cannot drift apart between tables. A vocabulary needing more than this shape adds it
/// in <see cref="ConfigureVocabulary"/>.
/// </remarks>
/// <param name="tableName">The table and the prefix its index names are built from.</param>
internal abstract class ControlledVocabularyConfiguration<TVocabulary>(string tableName)
    : IEntityTypeConfiguration<TVocabulary>
    where TVocabulary : ControlledVocabulary
{
    public void Configure(EntityTypeBuilder<TVocabulary> builder)
    {
        builder.ToTable(tableName);
        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Code)
            .IsRequired()
            .HasMaxLength(VocabularyPolicy.CodeMaxLength);

        builder.Property(entry => entry.DisplayName)
            .IsRequired()
            .HasMaxLength(VocabularyPolicy.DisplayNameMaxLength);

        builder.Property(entry => entry.IsActive)
            .IsRequired();

        // The permanent key seed data and later recipe references resolve an entry by. Lowercase form is
        // held by VocabularyPolicy.CodePattern rather than by collation, which differs between SQL Server
        // (case-insensitive by default) and the SQLite database the constraint tests run against.
        builder.HasIndex(entry => entry.Code)
            .IsUnique()
            .HasDatabaseName($"UX_{tableName}_Code");

        ConfigureVocabulary(builder);
    }

    /// <summary>Anything specific to one vocabulary. Nothing by default.</summary>
    protected virtual void ConfigureVocabulary(EntityTypeBuilder<TVocabulary> builder)
    {
    }
}
