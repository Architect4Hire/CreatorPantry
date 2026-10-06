using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Configurations;

internal sealed class TestAttachmentLinkConfiguration : IEntityTypeConfiguration<TestAttachmentLink>
{
    public void Configure(EntityTypeBuilder<TestAttachmentLink> builder)
    {
        builder.ToTable("TestAttachmentLinks", table =>
        {
            table.HasCheckConstraint(
                "CK_TestAttachmentLinks_SortOrder_NonNegative",
                "SortOrder >= 0");

            // No role column and so no hero rule. A test's photographs are evidence, and none of them is the
            // lead image of anything — that concept belongs to the recipe, on RecipeAssetLink.
        });

        builder.HasKey(link => link.Id);

        // Never store-generated; see TestObservationConfiguration. Nothing writes an attachment yet, so this
        // costs nothing today and stops the same false conflict the moment something does.
        builder.Property(link => link.Id).ValueGeneratedNever();

        builder.Property(link => link.MediaAssetId).IsRequired();
        builder.Property(link => link.SortOrder).IsRequired();
        builder.Property(link => link.Caption).HasMaxLength(RecipePolicy.CaptionMaxLength);

        // Cascade from the run. No Workspaces edge, for the reason TestObservationConfiguration gives.
        builder.HasOne<RecipeTestRun>()
            .WithMany(run => run.Attachments)
            .HasForeignKey(link => new { link.WorkspaceId, link.RecipeTestRunId })
            .HasPrincipalKey(run => new { run.WorkspaceId, run.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, for the reason TestIssueConfiguration gives about observations: cascading would delete a
        // photograph's link to the run because the issue it illustrated was removed, and SetNull is
        // unavailable while WorkspaceId is part of this key. Detaching the evidence is the seam's job to do
        // deliberately.
        builder.HasOne<TestIssue>()
            .WithMany()
            .HasForeignKey(link => new { link.WorkspaceId, link.TestIssueId })
            .HasPrincipalKey(issue => new { issue.WorkspaceId, issue.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The constraint this comment used to promise, now that 12.9 has landed the aggregate. Composite
        // so one workspace's test cannot attach another's photograph, and restricted because deleting a
        // link never deletes the asset and Workspace already cascades here through RecipeTestRun.
        builder.HasOne<MediaAsset>()
            .WithMany()
            .HasForeignKey(link => new { link.WorkspaceId, link.MediaAssetId })
            .HasPrincipalKey(asset => new { asset.WorkspaceId, asset.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(link => new { link.WorkspaceId, link.RecipeTestRunId, link.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_TestAttachmentLinks_Workspace_Run_Order");

        // "Where is this image used", which the media library needs before it will let anything be deleted.
        // Recipes answer that question from IX_RecipeAssetLinks_Workspace_MediaAsset; a test's photographs are
        // a second place an asset can be in use, and an answer that read only the first would be wrong.
        builder.HasIndex(link => new { link.WorkspaceId, link.MediaAssetId })
            .HasDatabaseName("IX_TestAttachmentLinks_Workspace_MediaAsset");
    }
}
