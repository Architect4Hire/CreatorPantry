using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Data.Configurations;

internal sealed class WorkspaceMembershipConfiguration : IEntityTypeConfiguration<WorkspaceMembership>
{
    public void Configure(EntityTypeBuilder<WorkspaceMembership> builder)
    {
        builder.ToTable("WorkspaceMemberships");
        builder.HasKey(membership => membership.Id);

        builder.Property(membership => membership.UserId)
            .IsRequired()
            .HasMaxLength(450); // Matches the Identity primary key column length.

        builder.Property(membership => membership.Role)
            .IsRequired();

        builder.Property(membership => membership.Status)
            .IsRequired();

        builder.Property(membership => membership.JoinedAt)
            .IsRequired();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(membership => membership.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(membership => membership.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // One membership per user per workspace; a role change updates this row rather than adding another.
        builder.HasIndex(membership => new { membership.WorkspaceId, membership.UserId })
            .IsUnique()
            .HasDatabaseName("UX_WorkspaceMemberships_Workspace_User");

        // Reverse lookup: every workspace a user belongs to. The composite unique index above leads with
        // WorkspaceId, so it does not efficiently serve this direction.
        builder.HasIndex(membership => membership.UserId)
            .HasDatabaseName("IX_WorkspaceMemberships_UserId");
    }
}
