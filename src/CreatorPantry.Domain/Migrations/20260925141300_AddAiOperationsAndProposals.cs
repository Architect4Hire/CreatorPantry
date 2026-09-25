using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddAiOperationsAndProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskType = table.Column<int>(type: "int", nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecipeVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    RequestedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailureCategory = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiOperations", x => x.Id);
                    table.UniqueConstraint("AK_AiOperations_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_AiOperations_Completed_Terminal", "(CompletedAt IS NULL AND Status NOT IN (3, 4, 5, 6, 7)) OR (CompletedAt IS NOT NULL AND Status IN (3, 4, 5, 6, 7))");
                    table.CheckConstraint("CK_AiOperations_Failure_Status", "(FailureCategory IS NULL AND Status <> 6) OR (FailureCategory IS NOT NULL AND Status = 6)");
                    table.CheckConstraint("CK_AiOperations_FailureCategory_Declared", "FailureCategory IS NULL OR FailureCategory <> 0");
                    table.CheckConstraint("CK_AiOperations_Running_HasStarted", "Status <> 1 OR StartedAt IS NOT NULL");
                    table.CheckConstraint("CK_AiOperations_Scope_Declared", "Scope <> 0");
                    table.CheckConstraint("CK_AiOperations_TaskType_Declared", "TaskType <> 0");
                    table.CheckConstraint("CK_AiOperations_Version_Requires_Recipe", "RecipeVersionId IS NULL OR RecipeId IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_AiOperations_RecipeVersions_WorkspaceId_RecipeVersionId",
                        columns: x => new { x.WorkspaceId, x.RecipeVersionId },
                        principalTable: "RecipeVersions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AiOperations_Recipes_WorkspaceId_RecipeId",
                        columns: x => new { x.WorkspaceId, x.RecipeId },
                        principalTable: "Recipes",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AiOperations_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiExecutionMetadata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AiOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    ProviderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModelName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModelDeployment = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PromptTemplateId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PromptTemplateVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LatencyMilliseconds = table.Column<int>(type: "int", nullable: false),
                    InputTokens = table.Column<int>(type: "int", nullable: true),
                    OutputTokens = table.Column<int>(type: "int", nullable: true),
                    EstimatedCost = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    SafetyBlocked = table.Column<bool>(type: "bit", nullable: false),
                    FailureCategory = table.Column<int>(type: "int", nullable: true),
                    FailureSummary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiExecutionMetadata", x => x.Id);
                    table.CheckConstraint("CK_AiExecutionMetadata_Attempt_Positive", "AttemptNumber >= 1");
                    table.CheckConstraint("CK_AiExecutionMetadata_Completed_After_Started", "CompletedAt >= StartedAt");
                    table.CheckConstraint("CK_AiExecutionMetadata_Cost_NotNegative", "EstimatedCost IS NULL OR EstimatedCost >= 0");
                    table.CheckConstraint("CK_AiExecutionMetadata_FailureCategory_Declared", "FailureCategory IS NULL OR FailureCategory <> 0");
                    table.CheckConstraint("CK_AiExecutionMetadata_Latency_NotNegative", "LatencyMilliseconds >= 0");
                    table.CheckConstraint("CK_AiExecutionMetadata_Summary_Requires_Failure", "FailureSummary IS NULL OR FailureCategory IS NOT NULL");
                    table.CheckConstraint("CK_AiExecutionMetadata_Tokens_NotNegative", "(InputTokens IS NULL OR InputTokens >= 0) AND (OutputTokens IS NULL OR OutputTokens >= 0)");
                    table.ForeignKey(
                        name: "FK_AiExecutionMetadata_AiOperations_WorkspaceId_AiOperationId",
                        columns: x => new { x.WorkspaceId, x.AiOperationId },
                        principalTable: "AiOperations",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AiOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRecipeVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OutputSchemaVersion = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PromptTemplateId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PromptTemplateVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PromptTemplateBodyChecksum = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ProviderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModelName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModelDeployment = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiProposals", x => x.Id);
                    table.UniqueConstraint("AK_AiProposals_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.ForeignKey(
                        name: "FK_AiProposals_AiOperations_WorkspaceId_AiOperationId",
                        columns: x => new { x.WorkspaceId, x.AiOperationId },
                        principalTable: "AiOperations",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiProposals_RecipeVersions_WorkspaceId_SourceRecipeVersionId",
                        columns: x => new { x.WorkspaceId, x.SourceRecipeVersionId },
                        principalTable: "RecipeVersions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AiProposalFeedback",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AiProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WasHelpful = table.Column<bool>(type: "bit", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiProposalFeedback", x => x.Id);
                    table.CheckConstraint("CK_AiProposalFeedback_SaysSomething", "WasHelpful IS NOT NULL OR Comment IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_AiProposalFeedback_AiProposals_WorkspaceId_AiProposalId",
                        columns: x => new { x.WorkspaceId, x.AiProposalId },
                        principalTable: "AiProposals",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiStructuredChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AiProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChangeKind = table.Column<int>(type: "int", nullable: false),
                    TargetKind = table.Column<int>(type: "int", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FieldName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BeforeValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    AfterValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ProposedPosition = table.Column<int>(type: "int", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Disposition = table.Column<int>(type: "int", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecidedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiStructuredChanges", x => x.Id);
                    table.UniqueConstraint("AK_AiStructuredChanges_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_AiStructuredChanges_ChangeKind_Declared", "ChangeKind <> 0");
                    table.CheckConstraint("CK_AiStructuredChanges_Decision_Complete", "(Disposition = 0 AND DecidedAt IS NULL AND DecidedByMembershipId IS NULL) OR (Disposition <> 0 AND DecidedAt IS NOT NULL AND DecidedByMembershipId IS NOT NULL)");
                    table.CheckConstraint("CK_AiStructuredChanges_FieldName_Set", "(FieldName IS NOT NULL AND ChangeKind = 1) OR (FieldName IS NULL AND ChangeKind <> 1)");
                    table.CheckConstraint("CK_AiStructuredChanges_HasAValue", "BeforeValue IS NOT NULL OR AfterValue IS NOT NULL OR ProposedPosition IS NOT NULL");
                    table.CheckConstraint("CK_AiStructuredChanges_Position_Kind", "ProposedPosition IS NULL OR ChangeKind IN (2, 4)");
                    table.CheckConstraint("CK_AiStructuredChanges_Position_NotNegative", "ProposedPosition IS NULL OR ProposedPosition >= 0");
                    table.CheckConstraint("CK_AiStructuredChanges_TargetKind_Declared", "TargetKind <> 0");
                    table.ForeignKey(
                        name: "FK_AiStructuredChanges_AiProposals_WorkspaceId_AiProposalId",
                        columns: x => new { x.WorkspaceId, x.AiProposalId },
                        principalTable: "AiProposals",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiWarnings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AiProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    AiStructuredChangeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiWarnings", x => x.Id);
                    table.CheckConstraint("CK_AiWarnings_Kind_Declared", "Kind <> 0");
                    table.ForeignKey(
                        name: "FK_AiWarnings_AiProposals_WorkspaceId_AiProposalId",
                        columns: x => new { x.WorkspaceId, x.AiProposalId },
                        principalTable: "AiProposals",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiWarnings_AiStructuredChanges_WorkspaceId_AiStructuredChangeId",
                        columns: x => new { x.WorkspaceId, x.AiStructuredChangeId },
                        principalTable: "AiStructuredChanges",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiExecutionMetadata_Workspace_Correlation",
                table: "AiExecutionMetadata",
                columns: new[] { "WorkspaceId", "CorrelationId" });

            migrationBuilder.CreateIndex(
                name: "UX_AiExecutionMetadata_Workspace_Operation_Attempt",
                table: "AiExecutionMetadata",
                columns: new[] { "WorkspaceId", "AiOperationId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiOperations_Status_RequestedAt",
                table: "AiOperations",
                columns: new[] { "Status", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiOperations_Workspace_Recipe_StatusChangedAt",
                table: "AiOperations",
                columns: new[] { "WorkspaceId", "RecipeId", "StatusChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiOperations_WorkspaceId_RecipeVersionId",
                table: "AiOperations",
                columns: new[] { "WorkspaceId", "RecipeVersionId" });

            migrationBuilder.CreateIndex(
                name: "UX_AiOperations_Workspace_IdempotencyKey",
                table: "AiOperations",
                columns: new[] { "WorkspaceId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiProposalFeedback_Workspace_Proposal_CreatedAt",
                table: "AiProposalFeedback",
                columns: new[] { "WorkspaceId", "AiProposalId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiProposals_Workspace_SourceVersion",
                table: "AiProposals",
                columns: new[] { "WorkspaceId", "SourceRecipeVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_AiProposals_Workspace_Template",
                table: "AiProposals",
                columns: new[] { "WorkspaceId", "PromptTemplateId", "PromptTemplateVersion" });

            migrationBuilder.CreateIndex(
                name: "UX_AiProposals_Workspace_Operation",
                table: "AiProposals",
                columns: new[] { "WorkspaceId", "AiOperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiStructuredChanges_Workspace_Proposal_Disposition",
                table: "AiStructuredChanges",
                columns: new[] { "WorkspaceId", "AiProposalId", "Disposition" });

            migrationBuilder.CreateIndex(
                name: "IX_AiStructuredChanges_Workspace_Proposal_SortOrder",
                table: "AiStructuredChanges",
                columns: new[] { "WorkspaceId", "AiProposalId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_AiWarnings_Workspace_Proposal_SortOrder",
                table: "AiWarnings",
                columns: new[] { "WorkspaceId", "AiProposalId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_AiWarnings_WorkspaceId_AiStructuredChangeId",
                table: "AiWarnings",
                columns: new[] { "WorkspaceId", "AiStructuredChangeId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiExecutionMetadata");

            migrationBuilder.DropTable(
                name: "AiProposalFeedback");

            migrationBuilder.DropTable(
                name: "AiWarnings");

            migrationBuilder.DropTable(
                name: "AiStructuredChanges");

            migrationBuilder.DropTable(
                name: "AiProposals");

            migrationBuilder.DropTable(
                name: "AiOperations");
        }
    }
}
