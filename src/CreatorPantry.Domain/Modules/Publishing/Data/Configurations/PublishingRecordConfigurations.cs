using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Publishing.Data.Entities;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Publishing.Data.Configurations;

internal sealed class PublishingRecordConfiguration : IEntityTypeConfiguration<PublishingRecord>
{
    public void Configure(EntityTypeBuilder<PublishingRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PublishingRecords", table =>
        {
            table.HasCheckConstraint("CK_PublishingRecords_ContentText_NotBlank", "trim(ContentText) <> ''");
            table.HasCheckConstraint(
                "CK_PublishingRecords_ScheduleTimeZoneId_NotBlank", "trim(ScheduleTimeZoneId) <> ''");

            // Blank is not a key. A column that is required but empty would satisfy the unique index once and
            // then collide with the next confirmation that also forgot — turning a missing key into a refusal
            // of somebody else's post.
            table.HasCheckConstraint(
                "CK_PublishingRecords_IdempotencyKey_NotBlank", "trim(IdempotencyKey) <> ''");

            // Stored as integers, and an enum column accepts any integer. Zero is neither a status nor a platform.
            table.HasCheckConstraint(
                "CK_PublishingRecords_Status_Range",
                $"Status >= {(int)PublishingRecordStatus.Pending} AND Status <= {(int)PublishingRecordStatus.NeedsAttention}");
            table.HasCheckConstraint(
                "CK_PublishingRecords_Platform_Range",
                $"Platform >= {(int)PublishingPlatform.Other} AND Platform <= {(int)PublishingPlatform.WhatsApp}");

            // A composite key with a null in it is not checked at all, so a version named without its asset
            // would be a pin on nothing — and an asset named without a version would be a picture nobody can
            // reproduce. Both halves travel together.
            //
            // The IS NOT NULL on the version is load-bearing, not belt-and-braces: a check constraint passes
            // unless it evaluates to false, and `NULL >= 1` is UNKNOWN. Without it, an asset with no version
            // would be accepted by the very constraint written to refuse it.
            table.HasCheckConstraint(
                "CK_PublishingRecords_AssetVersion_WithAsset",
                "(MediaAssetId IS NULL AND MediaAssetVersionNumber IS NULL) OR "
                    + "(MediaAssetId IS NOT NULL AND MediaAssetVersionNumber IS NOT NULL "
                    + "AND MediaAssetVersionNumber >= 1)");

            // Not knowing has a what and a since. Half of that pair is a row that cannot say what it is
            // waiting to find out, or how long it has been waiting.
            table.HasCheckConstraint(
                "CK_PublishingRecords_Uncertainty_Paired",
                "(UncertainOperation IS NULL AND UncertainSince IS NULL) OR "
                    + "(UncertainOperation IS NOT NULL AND UncertainSince IS NOT NULL)");

            // Only a write can leave an outcome in doubt. Reconciliation is a read: repeating it is always
            // safe, so an unanswered one settles nothing and blocks nothing.
            table.HasCheckConstraint(
                "CK_PublishingRecords_Uncertain_Operation_Writes",
                $"UncertainOperation IS NULL OR UncertainOperation IN "
                    + $"({(int)PublishingOperation.Submit}, {(int)PublishingOperation.Cancel})");

            // "Needs attention" is a request for a person's time, so it has to say what about. A row in this
            // state with neither an unknown outcome nor a failure category is asking for nothing.
            table.HasCheckConstraint(
                "CK_PublishingRecords_NeedsAttention_HasReason",
                $"Status <> {(int)PublishingRecordStatus.NeedsAttention} OR "
                    + "UncertainOperation IS NOT NULL OR FailureCategory IS NOT NULL");

            table.HasCheckConstraint(
                "CK_PublishingRecords_Attempts_NotNegative", "SubmitAttempts >= 0 AND CancelAttempts >= 0");

            // A claim is a worker and an expiry together. A lease with no expiry could never be recovered
            // from a worker that died holding it.
            table.HasCheckConstraint(
                "CK_PublishingRecords_Lease_Paired",
                "(LeasedBy IS NULL AND LeaseExpiresAt IS NULL) OR "
                    + "(LeasedBy IS NOT NULL AND LeaseExpiresAt IS NOT NULL)");
        });

        builder.HasKey(record => record.Id);

        // Set by the application, never the store.
        builder.Property(record => record.Id).ValueGeneratedNever();

        // Target of every child's composite key, so an external reference or an attempt owned by a different
        // workspace than its record is unrepresentable rather than merely refused.
        builder.HasAlternateKey(record => new { record.WorkspaceId, record.Id })
            .HasName("AK_PublishingRecords_Workspace_Id");

        builder.Property(record => record.Platform).IsRequired();
        builder.Property(record => record.ContentText).IsRequired().HasMaxLength(PublishingLedgerPolicy.ContentTextMaxLength);
        builder.Property(record => record.ScheduledAt).IsRequired();
        builder.Property(record => record.ScheduleTimeZoneId).IsRequired().HasMaxLength(PublishingPolicy.TimeZoneIdMaxLength);
        builder.Property(record => record.ScheduledLocal).IsRequired();
        builder.Property(record => record.Status).IsRequired();
        builder.Property(record => record.StatusChangedAt).IsRequired();
        builder.Property(record => record.FailureCategory).HasMaxLength(PublishingLedgerPolicy.FailureCategoryMaxLength);
        builder.Property(record => record.FailureSummary).HasMaxLength(PublishingLedgerPolicy.FailureSummaryMaxLength);
        builder.Property(record => record.SubmitAttempts).IsRequired();
        builder.Property(record => record.CancelAttempts).IsRequired();
        builder.Property(record => record.AvailableAt).IsRequired();
        builder.Property(record => record.ConfirmedByMembershipId).IsRequired();
        builder.Property(record => record.CreatedAt).IsRequired();

        // The shared kernel's length, because the key on this row is the one the caller sent on the command
        // (api-contract.md). Two different ceilings would make a key that the command accepted unstorable.
        builder.Property(record => record.IdempotencyKey).IsRequired().HasMaxLength(IdempotencyPolicy.KeyMaxLength);

        builder.Property(record => record.RowVersion).IsRowVersion();

        // The erasure path, and the only cascade into this table.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(record => record.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // The plan this was confirmed from. Workspace-paired, so one workspace's record cannot name another's
        // card, and restricted rather than cascading or set-null: the card's delete decides what happens to a
        // record (13.6c), and SQL Server allows one action path from Workspaces into this table, which the
        // cascade above already is.
        builder.HasOne<ContentCard>()
            .WithMany()
            .HasForeignKey(record => new { record.WorkspaceId, record.ContentCardId })
            .HasPrincipalKey(card => new { card.WorkspaceId, card.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Where it went. Required, and restricted for a second reason as well: a profile the provider stopped
        // listing becomes Removed rather than being deleted, precisely so this row can still say where it went.
        builder.HasOne<PublishingProfile>()
            .WithMany()
            .HasForeignKey(record => new { record.WorkspaceId, record.PublishingProfileId })
            .HasPrincipalKey(profile => new { profile.WorkspaceId, profile.Id })
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);

        // Through the asset as well as the workspace, so a version of some other asset — or of another
        // workspace's — is unrepresentable. A version is immutable, so what was published stays reproducible.
        builder.HasOne<MediaAssetVersion>()
            .WithMany()
            .HasForeignKey(record => new { record.WorkspaceId, record.MediaAssetId, record.MediaAssetVersionNumber })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.MediaAssetId, version.VersionNumber })
            .OnDelete(DeleteBehavior.Restrict);

        // What stops a replayed confirmation buying a second post. The shared IdempotencyRecord replays a
        // result for a day and then expires; this index does not.
        builder.HasIndex(record => new { record.WorkspaceId, record.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("UX_PublishingRecords_Workspace_IdempotencyKey");

        // What makes the duplicate guard a guarantee rather than a check. Business asks whether this
        // publication is already going out before it writes — but two confirmations with two different
        // idempotency keys can both pass that question and both insert, and the result is two posts a
        // creator's audience sees. Filtered to the live states, because a failed or cancelled publication is
        // one a creator may legitimately confirm again, and to a card that is still there, because a
        // detached record is history rather than something going out (13.10e).
        builder.HasIndex(record => new
            {
                record.WorkspaceId,
                record.ContentCardId,
                record.PublishingProfileId,
                record.ScheduledAt,
            })
            .IsUnique()
            .HasFilter(
                $"ContentCardId IS NOT NULL AND Status IN ({(int)PublishingRecordStatus.Pending}, "
                    + $"{(int)PublishingRecordStatus.Submitting}, {(int)PublishingRecordStatus.Scheduled}, "
                    + $"{(int)PublishingRecordStatus.Cancelling}, {(int)PublishingRecordStatus.NeedsAttention})")
            .HasDatabaseName("UX_PublishingRecords_Live_Card_Profile_Time");

        // One card's publishing history, newest first: the card's publishing panel (13.10d), and the check the
        // card's delete makes before it removes the plan.
        builder.HasIndex(record => new { record.WorkspaceId, record.ContentCardId, record.ScheduledAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("IX_PublishingRecords_Workspace_Card_ScheduledAt");

        // What is coming up, and what went wrong: the board's status column and the dashboard read model.
        builder.HasIndex(record => new { record.WorkspaceId, record.Status, record.ScheduledAt })
            .HasDatabaseName("IX_PublishingRecords_Workspace_Status_ScheduledAt");

        // The reconciliation worker's claim (13.10c), and deliberately not led by WorkspaceId: a claim looks
        // for due work before it knows whose it is, which is tenancy.md's documented queue-claim carve-out.
        // Filtered to the states a worker can act on, so a table of finished posts costs the sweep nothing.
        // This prompt adds the index only — nothing claims yet, and no IgnoreQueryFilters path exists.
        builder.HasIndex(record => new { record.Status, record.AvailableAt })
            .HasFilter(
                $"Status IN ({(int)PublishingRecordStatus.Pending}, {(int)PublishingRecordStatus.Submitting}, "
                    + $"{(int)PublishingRecordStatus.Scheduled}, {(int)PublishingRecordStatus.Cancelling})")
            .HasDatabaseName("IX_PublishingRecords_Status_AvailableAt");
    }
}

internal sealed class ExternalPublicationReferenceConfiguration
    : IEntityTypeConfiguration<ExternalPublicationReference>
{
    public void Configure(EntityTypeBuilder<ExternalPublicationReference> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ExternalPublicationReferences", table =>
        {
            table.HasCheckConstraint("CK_ExternalPublicationReferences_ProviderKey_NotBlank", "trim(ProviderKey) <> ''");
            table.HasCheckConstraint(
                "CK_ExternalPublicationReferences_ExternalUpdateId_NotBlank", "trim(ExternalUpdateId) <> ''");
        });

        builder.HasKey(reference => reference.Id);
        builder.Property(reference => reference.Id).ValueGeneratedNever();

        builder.Property(reference => reference.ProviderKey).IsRequired().HasMaxLength(PublishingPolicy.ProviderKeyMaxLength);
        builder.Property(reference => reference.ExternalUpdateId).IsRequired().HasMaxLength(PublishingPolicy.ExternalIdMaxLength);
        builder.Property(reference => reference.FirstSeenAt).IsRequired();

        // Workspace-paired, and the one cascade: the mapping goes with its record, which goes with its
        // workspace. No second path from the workspace, which SQL Server would refuse.
        builder.HasOne<PublishingRecord>()
            .WithMany()
            .HasForeignKey(reference => new { reference.WorkspaceId, reference.PublishingRecordId })
            .HasPrincipalKey(record => new { record.WorkspaceId, record.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // One provider post is one ledger row. This is what lets a reconciliation that finds a post resolve it
        // to the record that made it instead of concluding there is a second one to make.
        builder.HasIndex(reference => new { reference.WorkspaceId, reference.ProviderKey, reference.ExternalUpdateId })
            .IsUnique()
            .HasDatabaseName("UX_ExternalPublicationReferences_Workspace_Provider_External");

        // And one post per record: a retry that reached the provider twice cannot be recorded as if both
        // posts were wanted.
        builder.HasIndex(reference => new { reference.WorkspaceId, reference.PublishingRecordId })
            .IsUnique()
            .HasDatabaseName("UX_ExternalPublicationReferences_Workspace_Record");
    }
}

internal sealed class PublishingDeliveryAttemptConfiguration : IEntityTypeConfiguration<PublishingDeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<PublishingDeliveryAttempt> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PublishingDeliveryAttempts", table =>
        {
            table.HasCheckConstraint("CK_PublishingDeliveryAttempts_AttemptNumber_Positive", "AttemptNumber >= 1");

            table.HasCheckConstraint(
                "CK_PublishingDeliveryAttempts_Operation_Range",
                $"Operation >= {(int)PublishingOperation.Submit} AND Operation <= {(int)PublishingOperation.Reconcile}");
            table.HasCheckConstraint(
                "CK_PublishingDeliveryAttempts_Outcome_Range",
                $"Outcome >= {(int)PublishingAttemptOutcome.Accepted} AND Outcome <= {(int)PublishingAttemptOutcome.Unknown}");

            // An HTTP status or nothing. A number outside the range is not a provider's answer, and a zero
            // would read as one.
            table.HasCheckConstraint(
                "CK_PublishingDeliveryAttempts_ProviderStatusCode_Range",
                "ProviderStatusCode IS NULL OR (ProviderStatusCode >= 100 AND ProviderStatusCode <= 599)");
        });

        builder.HasKey(attempt => attempt.Id);
        builder.Property(attempt => attempt.Id).ValueGeneratedNever();

        builder.Property(attempt => attempt.AttemptNumber).IsRequired();
        builder.Property(attempt => attempt.Operation).IsRequired();
        builder.Property(attempt => attempt.Outcome).IsRequired();
        builder.Property(attempt => attempt.StartedAt).IsRequired();
        builder.Property(attempt => attempt.CorrelationId).IsRequired();
        builder.Property(attempt => attempt.FailureCategory).HasMaxLength(PublishingLedgerPolicy.FailureCategoryMaxLength);
        builder.Property(attempt => attempt.FailureSummary).HasMaxLength(PublishingLedgerPolicy.FailureSummaryMaxLength);

        // As the external reference: workspace-paired, and the trail goes with the record it is about.
        builder.HasOne<PublishingRecord>()
            .WithMany()
            .HasForeignKey(attempt => new { attempt.WorkspaceId, attempt.PublishingRecordId })
            .HasPrincipalKey(record => new { record.WorkspaceId, record.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // The trail's order is unique per record by index rather than by agreement, so two workers writing an
        // attempt at once cannot both be the third one.
        builder.HasIndex(attempt => new { attempt.WorkspaceId, attempt.PublishingRecordId, attempt.AttemptNumber })
            .IsUnique()
            .HasDatabaseName("UX_PublishingDeliveryAttempts_Record_AttemptNumber");
    }
}
