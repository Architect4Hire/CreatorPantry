using CreatorPantry.Domain.Modules.Publishing.Data.Entities;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Publishing.Data.Configurations;

internal sealed class PublishingProfileConfiguration : IEntityTypeConfiguration<PublishingProfile>
{
    public void Configure(EntityTypeBuilder<PublishingProfile> builder)
    {
        builder.ToTable("PublishingProfiles", table =>
        {
            table.HasCheckConstraint("CK_PublishingProfiles_ProviderKey_NotBlank", "trim(ProviderKey) <> ''");
            table.HasCheckConstraint("CK_PublishingProfiles_DisplayName_NotBlank", "trim(DisplayName) <> ''");

            // Stored as integers, and an enum column accepts any integer. Zero is neither a platform nor a status.
            table.HasCheckConstraint(
                "CK_PublishingProfiles_Platform_Range",
                $"Platform >= {(int)PublishingPlatform.Other} AND Platform <= {(int)PublishingPlatform.WhatsApp}");
            table.HasCheckConstraint(
                "CK_PublishingProfiles_Status_Range",
                $"Status >= {(int)PublishingProfileStatus.Ready} AND Status <= {(int)PublishingProfileStatus.Removed}");
        });

        builder.HasKey(profile => profile.Id);
        builder.Property(profile => profile.Id).ValueGeneratedNever();

        // Target of the reference's composite key, so a mapping owned by a different workspace than its
        // profile is unrepresentable rather than merely refused.
        builder.HasAlternateKey(profile => new { profile.WorkspaceId, profile.Id })
            .HasName("AK_PublishingProfiles_Workspace_Id");

        builder.Property(profile => profile.ProviderKey).IsRequired().HasMaxLength(PublishingPolicy.ProviderKeyMaxLength);
        builder.Property(profile => profile.Platform).IsRequired();
        builder.Property(profile => profile.DisplayName).IsRequired().HasMaxLength(PublishingPolicy.DisplayNameMaxLength);
        builder.Property(profile => profile.TimeZoneId).HasMaxLength(PublishingPolicy.TimeZoneIdMaxLength);
        builder.Property(profile => profile.Status).IsRequired();
        builder.Property(profile => profile.FirstSeenAt).IsRequired();
        builder.Property(profile => profile.LastSeenAt).IsRequired();

        // The erasure path, and the only cascade into this table.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(profile => profile.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // The list: one workspace's profiles that are still there.
        builder.HasIndex(profile => new { profile.WorkspaceId, profile.Status })
            .HasDatabaseName("IX_PublishingProfiles_Workspace_Status");
    }
}

internal sealed class ExternalPublishingProfileReferenceConfiguration
    : IEntityTypeConfiguration<ExternalPublishingProfileReference>
{
    public void Configure(EntityTypeBuilder<ExternalPublishingProfileReference> builder)
    {
        builder.ToTable("ExternalPublishingProfileReferences", table =>
        {
            table.HasCheckConstraint("CK_ExternalPublishingProfileReferences_ExternalId_NotBlank", "trim(ExternalId) <> ''");
            table.HasCheckConstraint(
                "CK_ExternalPublishingProfileReferences_ExternalAccountId_NotBlank", "trim(ExternalAccountId) <> ''");
        });

        builder.HasKey(reference => reference.Id);
        builder.Property(reference => reference.Id).ValueGeneratedNever();

        builder.Property(reference => reference.ProviderKey).IsRequired().HasMaxLength(PublishingPolicy.ProviderKeyMaxLength);
        builder.Property(reference => reference.ExternalId).IsRequired().HasMaxLength(PublishingPolicy.ExternalIdMaxLength);
        builder.Property(reference => reference.ExternalAccountId).IsRequired().HasMaxLength(PublishingPolicy.ExternalIdMaxLength);

        // Workspace-paired, and the one cascade: the mapping goes with its profile, which goes with its
        // workspace. No second path from the workspace, which SQL Server would refuse.
        builder.HasOne<PublishingProfile>()
            .WithMany()
            .HasForeignKey(reference => new { reference.WorkspaceId, reference.PublishingProfileId })
            .HasPrincipalKey(profile => new { profile.WorkspaceId, profile.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // What makes reading the same provider profile twice one row here: a retry, or two reads at once, can
        // never map one provider profile to two of CreatorPantry's.
        builder.HasIndex(reference => new { reference.WorkspaceId, reference.ProviderKey, reference.ExternalId })
            .IsUnique()
            .HasDatabaseName("UX_ExternalPublishingProfileReferences_Workspace_Provider_External");

        // One mapping per profile.
        builder.HasIndex(reference => new { reference.WorkspaceId, reference.PublishingProfileId })
            .IsUnique()
            .HasDatabaseName("UX_ExternalPublishingProfileReferences_Workspace_Profile");
    }
}

internal sealed class PublishingProviderSyncConfiguration : IEntityTypeConfiguration<PublishingProviderSync>
{
    public void Configure(EntityTypeBuilder<PublishingProviderSync> builder)
    {
        builder.ToTable("PublishingProviderSyncs", table =>
        {
            table.HasCheckConstraint("CK_PublishingProviderSyncs_ProviderKey_NotBlank", "trim(ProviderKey) <> ''");

            // An attempt is one of the four ways asking a provider can end. "Not configured" is not asking.
            table.HasCheckConstraint(
                "CK_PublishingProviderSyncs_LastOutcome_Attempt",
                $"LastOutcome IN ({(int)PublishingConnectionStatus.Connected}, {(int)PublishingConnectionStatus.CredentialRejected}, "
                    + $"{(int)PublishingConnectionStatus.RateLimited}, {(int)PublishingConnectionStatus.Unavailable})");
        });

        builder.HasKey(sync => sync.Id);
        builder.Property(sync => sync.Id).ValueGeneratedNever();

        builder.Property(sync => sync.ProviderKey).IsRequired().HasMaxLength(PublishingPolicy.ProviderKeyMaxLength);
        builder.Property(sync => sync.LastAttemptAt).IsRequired();
        builder.Property(sync => sync.LastOutcome).IsRequired();
        builder.Property(sync => sync.RowVersion).IsRowVersion();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(sync => sync.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(sync => new { sync.WorkspaceId, sync.ProviderKey })
            .IsUnique()
            .HasDatabaseName("UX_PublishingProviderSyncs_Workspace_Provider");
    }
}
