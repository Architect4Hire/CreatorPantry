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

        // No foreign key until the Workspace entity exists (Phase 2).
        builder.Property(user => user.LastWorkspaceId);
    }
}
