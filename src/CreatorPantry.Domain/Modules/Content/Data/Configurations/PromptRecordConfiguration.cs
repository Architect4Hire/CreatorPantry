using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class PromptRecordConfiguration : IEntityTypeConfiguration<PromptRecord>
{
    public void Configure(EntityTypeBuilder<PromptRecord> builder)
    {
        builder.ToTable("PromptRecords", table =>
        {
            // A prompt record with no prompt records nothing.
            table.HasCheckConstraint("CK_PromptRecords_Text_NotBlank", "trim(Text) <> ''");
            table.HasCheckConstraint("CK_PromptRecords_ChannelKey_NotBlank", "trim(ChannelKey) <> ''");

            // A proposal id belongs to a generated prompt and nothing else, so provenance cannot disagree with
            // source. The same constraint ContentRevisions carries, for the same reason.
            table.HasCheckConstraint(
                "CK_PromptRecords_AiProposal_Source",
                $"(AiProposalId IS NOT NULL AND Source <> {(int)PromptRecordSource.Manual}) OR "
                    + $"(AiProposalId IS NULL AND Source = {(int)PromptRecordSource.Manual})");

            // A generated prompt names the template that wrote it, with the checksum: a version string alone is a
            // claim. A prompt the creator typed has no template to name.
            table.HasCheckConstraint(
                "CK_PromptRecords_Template_Generated",
                $"(Source = {(int)PromptRecordSource.Manual} "
                    + "AND PromptTemplateId IS NULL AND PromptTemplateVersion IS NULL AND PromptTemplateBodyChecksum IS NULL) OR "
                    + $"(Source <> {(int)PromptRecordSource.Manual} "
                    + "AND PromptTemplateId IS NOT NULL AND PromptTemplateVersion IS NOT NULL AND PromptTemplateBodyChecksum IS NOT NULL)");

            // A model's draft exists exactly when a model wrote one.
            table.HasCheckConstraint(
                "CK_PromptRecords_GeneratedText_Source",
                $"(GeneratedText IS NULL AND Source = {(int)PromptRecordSource.Manual}) OR "
                    + $"(GeneratedText IS NOT NULL AND Source <> {(int)PromptRecordSource.Manual})");

            // A version pin needs its recipe. The composite foreign key below then constrains it to a version of
            // that same recipe, so the pair cannot name a version of something else.
            table.HasCheckConstraint(
                "CK_PromptRecords_RecipeVersion_RequiresRecipe",
                "RecipeVersionId IS NULL OR RecipeId IS NOT NULL");

            // Stored as integers, and an enum column accepts any integer: a write that went round the write seam
            // could otherwise store a source or a kind that does not exist.
            table.HasCheckConstraint(
                "CK_PromptRecords_Source_Range",
                $"Source >= 0 AND Source <= {(int)PromptRecordSource.ReferenceImageAnalysis}");
            table.HasCheckConstraint(
                "CK_PromptRecords_ImageKind_Range",
                $"ImageKind >= 0 AND ImageKind <= {(int)PromptImageKind.Other}");
        });

        builder.HasKey(record => record.Id);

        // Set by the application, never the store.
        builder.Property(record => record.Id).ValueGeneratedNever();

        builder.Property(record => record.ChannelKey).IsRequired().HasMaxLength(ContentPolicy.ChannelKeyMaxLength);
        builder.Property(record => record.ImageKind).IsRequired();
        builder.Property(record => record.Text).IsRequired().HasMaxLength(ContentPolicy.PromptTextMaxLength);
        builder.Property(record => record.GeneratedText).HasMaxLength(ContentPolicy.PromptTextMaxLength);
        builder.Property(record => record.Label).HasMaxLength(ContentPolicy.PromptLabelMaxLength);
        builder.Property(record => record.Source).IsRequired();
        builder.Property(record => record.PromptTemplateId).HasMaxLength(ContentPolicy.TemplateIdMaxLength);
        builder.Property(record => record.PromptTemplateVersion).HasMaxLength(ContentPolicy.TemplateVersionMaxLength);
        builder.Property(record => record.PromptTemplateBodyChecksum).HasMaxLength(ContentPolicy.ChecksumMaxLength);
        builder.Property(record => record.CreatedByMembershipId).IsRequired();
        builder.Property(record => record.CreatedAt).IsRequired();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(record => record.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace-paired, so one workspace's prompt cannot be pinned to another's recipe — unrepresentable
        // rather than merely refused. Restrict: a recipe with prompts in the library is not simply deleted, as
        // with its content proposals.
        builder.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(record => new { record.WorkspaceId, record.RecipeId })
            .HasPrincipalKey(recipe => new { recipe.WorkspaceId, recipe.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // A version of *that recipe*, in this workspace.
        builder.HasOne<RecipeVersion>()
            .WithMany()
            .HasForeignKey(record => new { record.WorkspaceId, record.RecipeId, record.RecipeVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.RecipeId, version.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The proposal that produced the draft, in this workspace. Restrict: the provenance outlives nothing.
        builder.HasOne<AiProposal>()
            .WithMany()
            .HasForeignKey(record => new { record.WorkspaceId, record.AiProposalId })
            .HasPrincipalKey(proposal => new { proposal.WorkspaceId, proposal.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The library's default read: newest first, with the id as the keyset tie-break PRM-002's cursor needs.
        builder.HasIndex(record => new { record.WorkspaceId, record.CreatedAt, record.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("IX_PromptRecords_Workspace_Created");

        // The same read filtered to one channel.
        builder.HasIndex(record => new { record.WorkspaceId, record.ChannelKey, record.CreatedAt, record.Id })
            .IsDescending(false, false, true, true)
            .HasDatabaseName("IX_PromptRecords_Workspace_Channel_Created");

        // "Which prompts came from this recipe" needs no index of its own: the recipe-version foreign key above
        // already creates (WorkspaceId, RecipeId, RecipeVersionId), whose leading columns answer exactly that.
        // An explicit (WorkspaceId, RecipeId) index was written here first and removed on reviewing the generated
        // migration — it would have cost every insert a second write to serve a read already covered.

        // The pin 12.3 left waiting for 12.6's table. Workspace-paired and restricted, like every other pin
        // here: a prompt record can only name an image of its own workspace, and the workspace's own cascade
        // is what removes both rows — a second cascade path into PromptRecords is what SQL Server refuses.
        //
        // Restrict also states the retention rule the other way round: a staged image that a prompt record
        // names cannot be deleted out from under it, so 12.8's sweep has to reckon with the prompt library
        // rather than orphan a row that is the evidence of what produced a published image.
        builder.HasOne<GeneratedImage>()
            .WithMany()
            .HasForeignKey(record => new { record.WorkspaceId, record.GeneratedImageId })
            .HasPrincipalKey(image => new { image.WorkspaceId, image.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // One prompt record per committed generated image. This is what makes 12.3a's "no duplicate prompt records
        // on DAM retry" a property of the schema rather than something a worker has to remember: a retry loses the
        // index. Filtered, because a prompt saved before any image was committed has no id to be unique on.
        builder.HasIndex(record => new { record.WorkspaceId, record.GeneratedImageId })
            .IsUnique()
            .HasFilter("GeneratedImageId IS NOT NULL")
            .HasDatabaseName("UX_PromptRecords_Workspace_GeneratedImage");
    }
}
