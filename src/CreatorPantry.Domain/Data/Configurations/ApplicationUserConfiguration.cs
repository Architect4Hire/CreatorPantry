using CreatorPantry.Domain.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Data.Configurations;

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(user => user.DisplayName)
            .IsRequired()
            .HasMaxLength(AccountPolicy.DisplayNameMaxLength);

        builder.Property(user => user.CreatedAt)
            .IsRequired();

        // A navigation hint only; it never authorizes access, so a deleted workspace clears it rather
        // than blocking the delete or cascading to the user.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(user => user.LastWorkspaceId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
