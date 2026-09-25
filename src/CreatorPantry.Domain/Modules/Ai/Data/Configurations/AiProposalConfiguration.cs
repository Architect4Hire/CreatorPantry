using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Ai.Data.Configurations;

internal sealed class AiProposalConfiguration : IEntityTypeConfiguration<AiProposal>
{
    public void Configure(EntityTypeBuilder<AiProposal> builder)
    {
        builder.ToTable("AiProposals");

        builder.HasKey(proposal => proposal.Id);

        // The target of the children's composite foreign keys.
        builder.HasAlternateKey(proposal => new { proposal.WorkspaceId, proposal.Id })
            .HasName("AK_AiProposals_Workspace_Id");

        builder.Property(proposal => proposal.OutputSchemaVersion)
            .IsRequired()
            .HasMaxLength(AiPolicy.SchemaVersionMaxLength);

        builder.Property(proposal => proposal.PromptTemplateId)
            .IsRequired()
            .HasMaxLength(AiPolicy.TemplateIdMaxLength);

        builder.Property(proposal => proposal.PromptTemplateVersion)
            .IsRequired()
            .HasMaxLength(AiPolicy.TemplateVersionMaxLength);

        builder.Property(proposal => proposal.PromptTemplateBodyChecksum)
            .IsRequired()
            .HasMaxLength(AiPolicy.ChecksumMaxLength);

        builder.Property(proposal => proposal.ProviderName)
            .IsRequired()
            .HasMaxLength(AiPolicy.ProviderIdentifierMaxLength);

        builder.Property(proposal => proposal.ModelName)
            .IsRequired()
            .HasMaxLength(AiPolicy.ProviderIdentifierMaxLength);

        builder.Property(proposal => proposal.ModelDeployment)
            .HasMaxLength(AiPolicy.ProviderIdentifierMaxLength);

        builder.Property(proposal => proposal.CreatedAt).IsRequired();

        // No foreign key to Workspaces, for the same reason the interior children have none: the operation
        // already cascades from Workspaces, so a second edge would give SQL Server two cascade paths to this
        // table and it refuses the DDL outright — "may cause cycles or multiple cascade paths". SQLite does
        // not enforce that rule, so this was only visible once the migration ran against a real database.
        //
        // The consequence is worth stating rather than discovering: a proposal is an aggregate root for
        // reading and for routing — it has its own id and its own alternate key — but for deletion it is
        // interior to its operation. Nothing can remove an operation and leave its proposal behind. Erasing a
        // workspace still reaches this table, through the operation.
        //
        // Cascade from the operation, which is this aggregate's owner inside the module.
        builder.HasOne<AiOperation>()
            .WithMany()
            .HasForeignKey(proposal => new { proposal.WorkspaceId, proposal.AiOperationId })
            .HasPrincipalKey(operation => new { operation.WorkspaceId, operation.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, matching RecipeVersion and AiOperation: deleting a recipe that has AI history is refused,
        // so removing one stays a deliberate operation that says what becomes of the record.
        builder.HasOne<RecipeVersion>()
            .WithMany()
            .HasForeignKey(proposal => new { proposal.WorkspaceId, proposal.SourceRecipeVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // At most one proposal per operation. A unique index rather than sharing the operation's primary key,
        // so the proposal keeps an id of its own for the routes that address it.
        builder.HasIndex(proposal => new { proposal.WorkspaceId, proposal.AiOperationId })
            .IsUnique()
            .HasDatabaseName("UX_AiProposals_Workspace_Operation");

        // Every proposal computed against one source version — what a staleness sweep reads after a recipe
        // changes, to find the proposals that can no longer be applied.
        builder.HasIndex(proposal => new { proposal.WorkspaceId, proposal.SourceRecipeVersionId })
            .HasDatabaseName("IX_AiProposals_Workspace_SourceVersion");

        // Finding every proposal produced by one template version, which is what makes a prompt change
        // reviewable after the fact rather than only forward.
        builder.HasIndex(proposal => new { proposal.WorkspaceId, proposal.PromptTemplateId, proposal.PromptTemplateVersion })
            .HasDatabaseName("IX_AiProposals_Workspace_Template");
    }
}
