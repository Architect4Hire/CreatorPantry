using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddNutritionMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NutritionMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IngredientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    MatchMethod = table.Column<int>(type: "int", nullable: true),
                    Confidence = table.Column<int>(type: "int", nullable: true),
                    ReferenceSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ExternalRecordId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ReferenceDescription = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    SourceVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReviewSourceVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UnresolvedReason = table.Column<int>(type: "int", nullable: true),
                    PortionMeasurementUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PortionAmount = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: true),
                    PortionGrams = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: true),
                    PortionLabel = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    PortionOrigin = table.Column<int>(type: "int", nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConfirmedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NutritionMappings", x => x.Id);
                    table.UniqueConstraint("AK_NutritionMappings_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_NutritionMappings_Confidence_Automatic", "(Confidence IS NULL AND (MatchMethod IS NULL OR MatchMethod <> 1)) OR (Confidence IS NOT NULL AND MatchMethod IS NOT NULL AND MatchMethod = 1)");
                    table.CheckConstraint("CK_NutritionMappings_Confirmation", "(Status IN (20, 30) AND ConfirmedAt IS NOT NULL AND ConfirmedByMembershipId IS NOT NULL) OR (Status NOT IN (20, 30) AND ConfirmedAt IS NULL AND ConfirmedByMembershipId IS NULL)");
                    table.CheckConstraint("CK_NutritionMappings_Portion_NeedsReference", "Status <> 40 OR PortionMeasurementUnitId IS NULL");
                    table.CheckConstraint("CK_NutritionMappings_Portion_Whole", "(PortionMeasurementUnitId IS NULL AND PortionAmount IS NULL AND PortionGrams IS NULL AND PortionOrigin IS NULL AND PortionLabel IS NULL) OR (PortionMeasurementUnitId IS NOT NULL AND PortionAmount IS NOT NULL AND PortionAmount > 0 AND PortionGrams IS NOT NULL AND PortionGrams > 0 AND PortionOrigin IS NOT NULL)");
                    table.CheckConstraint("CK_NutritionMappings_Reference_Whole", "(Status = 40 AND ReferenceSourceId IS NULL AND SourceCode IS NULL AND ExternalRecordId IS NULL AND ReferenceDescription IS NULL AND SourceVersion IS NULL AND MatchMethod IS NULL) OR (Status <> 40 AND ReferenceSourceId IS NOT NULL AND SourceCode IS NOT NULL AND ExternalRecordId IS NOT NULL AND ReferenceDescription IS NOT NULL AND SourceVersion IS NOT NULL AND MatchMethod IS NOT NULL)");
                    table.CheckConstraint("CK_NutritionMappings_ReviewVersion_NeedsReview", "(Status = 30 AND ReviewSourceVersion IS NOT NULL) OR (Status <> 30 AND ReviewSourceVersion IS NULL)");
                    table.CheckConstraint("CK_NutritionMappings_Status_Range", "Status IN (10, 20, 30, 40)");
                    table.CheckConstraint("CK_NutritionMappings_UnresolvedReason", "(Status = 40 AND UnresolvedReason IS NOT NULL) OR (Status <> 40 AND UnresolvedReason IS NULL)");
                    table.ForeignKey(
                        name: "FK_NutritionMappings_Ingredients_IngredientId",
                        column: x => x.IngredientId,
                        principalTable: "Ingredients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NutritionMappings_MeasurementUnits_PortionMeasurementUnitId",
                        column: x => x.PortionMeasurementUnitId,
                        principalTable: "MeasurementUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NutritionMappings_ReferenceSources_ReferenceSourceId",
                        column: x => x.ReferenceSourceId,
                        principalTable: "ReferenceSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NutritionMappings_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NutritionMappingRejections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NutritionMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExternalRecordId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RejectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RejectedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NutritionMappingRejections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NutritionMappingRejections_NutritionMappings_WorkspaceId_NutritionMappingId",
                        columns: x => new { x.WorkspaceId, x.NutritionMappingId },
                        principalTable: "NutritionMappings",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_NutritionMappingRejections_Mapping_Reference",
                table: "NutritionMappingRejections",
                columns: new[] { "WorkspaceId", "NutritionMappingId", "SourceCode", "ExternalRecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NutritionMappings_IngredientId",
                table: "NutritionMappings",
                column: "IngredientId");

            migrationBuilder.CreateIndex(
                name: "IX_NutritionMappings_PortionMeasurementUnitId",
                table: "NutritionMappings",
                column: "PortionMeasurementUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_NutritionMappings_ReferenceSourceId",
                table: "NutritionMappings",
                column: "ReferenceSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_NutritionMappings_Workspace_Status",
                table: "NutritionMappings",
                columns: new[] { "WorkspaceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_NutritionMappings_Workspace_Ingredient",
                table: "NutritionMappings",
                columns: new[] { "WorkspaceId", "IngredientId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NutritionMappingRejections");

            migrationBuilder.DropTable(
                name: "NutritionMappings");
        }
    }
}
