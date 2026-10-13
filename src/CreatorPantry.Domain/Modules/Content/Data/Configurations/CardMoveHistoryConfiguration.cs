using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class CardMoveHistoryConfiguration : IEntityTypeConfiguration<CardMoveHistory>
{
    public void Configure(EntityTypeBuilder<CardMoveHistory> builder)
    {
        builder.ToTable("CardMoveHistory", table =>
        {
            table.HasCheckConstraint(
                "CK_CardMoveHistory_ToColumn_Range",
                $"ToColumn >= {(int)ContentCardColumn.ToDo} AND ToColumn <= {(int)ContentCardColumn.Approved}");
            table.HasCheckConstraint(
                "CK_CardMoveHistory_FromColumn_Range",
                $"FromColumn IS NULL OR (FromColumn >= {(int)ContentCardColumn.ToDo} "
                    + $"AND FromColumn <= {(int)ContentCardColumn.Approved})");

            // A reorder is not a move: a row that stays in its column records nothing that happened.
            table.HasCheckConstraint(
                "CK_CardMoveHistory_Columns_Differ", "FromColumn IS NULL OR FromColumn <> ToColumn");

            // The first row is the card's creation and the only one that came from nowhere; every later row
            // left a column. Together with the unique sequence, a card has exactly one creation row.
            table.HasCheckConstraint(
                "CK_CardMoveHistory_Sequence_Creation",
                "(Sequence = 1 AND FromColumn IS NULL) OR (Sequence > 1 AND FromColumn IS NOT NULL)");
        });

        builder.HasKey(move => move.Id);

        // Set by the application, never the store.
        builder.Property(move => move.Id).ValueGeneratedNever();

        builder.Property(move => move.Sequence).IsRequired();
        builder.Property(move => move.ToColumn).IsRequired();
        builder.Property(move => move.Note).HasMaxLength(ContentCardPolicy.MoveNoteMaxLength);
        builder.Property(move => move.OccurredAt).IsRequired();

        // Reaches the workspace only through its card, so there is one cascade path into this table, and a
        // card's history goes with the card.
        builder.HasOne<ContentCard>()
            .WithMany()
            .HasForeignKey(move => new { move.WorkspaceId, move.ContentCardId })
            .HasPrincipalKey(card => new { card.WorkspaceId, card.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // The order of a card's history, and the guard on it: two moves composed against the same state take
        // the same number, and the second is refused rather than recorded beside the first.
        builder.HasIndex(move => new { move.WorkspaceId, move.ContentCardId, move.Sequence })
            .IsUnique()
            .HasDatabaseName("UX_CardMoveHistory_Workspace_Card_Sequence");
    }
}
