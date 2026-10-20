using CreatorPantry.Domain.Modules.Nutrition.Data.Entities;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Nutrition.Data.Configurations;

internal sealed class NutritionMappingRejectionConfiguration : IEntityTypeConfiguration<NutritionMappingRejection>
{
    public void Configure(EntityTypeBuilder<NutritionMappingRejection> builder)
    {
        builder.ToTable("NutritionMappingRejections");

        builder.HasKey(rejection => rejection.Id);
        builder.Property(rejection => rejection.Id).ValueGeneratedNever();

        builder.Property(rejection => rejection.SourceCode)
            .IsRequired()
            .HasMaxLength(NutritionMappingPolicy.SourceCodeMaxLength);
        builder.Property(rejection => rejection.ExternalRecordId)
            .IsRequired()
            .HasMaxLength(NutritionMappingPolicy.ExternalRecordIdMaxLength);
        builder.Property(rejection => rejection.RejectedAt).IsRequired();
        builder.Property(rejection => rejection.RejectedByMembershipId).IsRequired();

        // No key of its own to Workspaces: the mapping's composite key already cascades from there, and a second
        // path would be refused by SQL Server as a multiple cascade path.
        builder.HasIndex(rejection => new
            {
                rejection.WorkspaceId,
                rejection.NutritionMappingId,
                rejection.SourceCode,
                rejection.ExternalRecordId,
            })
            .IsUnique()
            .HasDatabaseName("UX_NutritionMappingRejections_Mapping_Reference");
    }
}
