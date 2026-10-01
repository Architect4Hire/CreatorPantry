using System;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddBrandSourceChunks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_BrandSourceExtractions_Workspace_Id_Version_Status",
                table: "BrandSourceExtractions",
                columns: new[] { "WorkspaceId", "Id", "BrandSourceDocumentVersionId", "Status" });

            migrationBuilder.CreateTable(
                name: "BrandSourceChunkSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandSourceDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandSourceDocumentVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandSourceExtractionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceStatus = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ChunkerId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EmbeddingModel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EmbeddingDimension = table.Column<int>(type: "int", nullable: false),
                    ChunkCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EmbeddedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandSourceChunkSets", x => x.Id);
                    table.UniqueConstraint("AK_BrandSourceChunkSets_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_BrandSourceChunkSets_ChunkCount_NonNegative", "ChunkCount >= 0");
                    table.CheckConstraint("CK_BrandSourceChunkSets_ChunkerId_NotBlank", "trim(ChunkerId) <> ''");
                    table.CheckConstraint("CK_BrandSourceChunkSets_Current_HasChunks", "Status <> 2 OR ChunkCount >= 1");
                    table.CheckConstraint("CK_BrandSourceChunkSets_EmbeddedAt_Matches_Status", "(Status = 1 AND EmbeddedAt IS NULL) OR (Status <> 1 AND EmbeddedAt IS NOT NULL)");
                    table.CheckConstraint("CK_BrandSourceChunkSets_EmbeddingDimension_Fixed", "EmbeddingDimension = 1536");
                    table.CheckConstraint("CK_BrandSourceChunkSets_EmbeddingModel_NotBlank", "trim(EmbeddingModel) <> ''");
                    table.CheckConstraint("CK_BrandSourceChunkSets_SourceStatus_Succeeded", "SourceStatus = 1");
                    table.CheckConstraint("CK_BrandSourceChunkSets_Status_Specified", "Status <> 0");
                    table.ForeignKey(
                        name: "FK_BrandSourceChunkSets_BrandSourceExtractions_WorkspaceId_BrandSourceExtractionId_BrandSourceDocumentVersionId_SourceStatus",
                        columns: x => new { x.WorkspaceId, x.BrandSourceExtractionId, x.BrandSourceDocumentVersionId, x.SourceStatus },
                        principalTable: "BrandSourceExtractions",
                        principalColumns: new[] { "WorkspaceId", "Id", "BrandSourceDocumentVersionId", "Status" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BrandSourceChunkSets_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BrandSourceChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandSourceChunkSetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    StartByteOffset = table.Column<long>(type: "bigint", nullable: false),
                    ByteLength = table.Column<int>(type: "int", nullable: false),
                    ContentChecksum = table.Column<string>(type: "nvarchar(71)", maxLength: 71, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Embedding = table.Column<SqlVector<float>>(type: "vector(1536)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandSourceChunks", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_BrandSourceChunks_ByteLength_Bounded", "ByteLength >= 1 AND ByteLength <= 16000");
                    table.CheckConstraint("CK_BrandSourceChunks_ContentChecksum_Sha256", "ContentChecksum LIKE 'sha256:%'");
                    table.CheckConstraint("CK_BrandSourceChunks_Ordinal_Positive", "Ordinal >= 1");
                    table.CheckConstraint("CK_BrandSourceChunks_StartByteOffset_NonNegative", "StartByteOffset >= 0");
                    table.CheckConstraint("CK_BrandSourceChunks_Text_NotBlank", "trim(Text) <> ''");
                    table.ForeignKey(
                        name: "FK_BrandSourceChunks_BrandSourceChunkSets_WorkspaceId_BrandSourceChunkSetId",
                        columns: x => new { x.WorkspaceId, x.BrandSourceChunkSetId },
                        principalTable: "BrandSourceChunkSets",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_BrandSourceChunks_Workspace_Set_Ordinal",
                table: "BrandSourceChunks",
                columns: new[] { "WorkspaceId", "BrandSourceChunkSetId", "Ordinal" },
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_BrandSourceChunkSets_Workspace_Document_Status",
                table: "BrandSourceChunkSets",
                columns: new[] { "WorkspaceId", "BrandSourceDocumentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BrandSourceChunkSets_WorkspaceId_BrandSourceExtractionId_BrandSourceDocumentVersionId_SourceStatus",
                table: "BrandSourceChunkSets",
                columns: new[] { "WorkspaceId", "BrandSourceExtractionId", "BrandSourceDocumentVersionId", "SourceStatus" });

            migrationBuilder.CreateIndex(
                name: "UX_BrandSourceChunkSets_Workspace_Extraction_Model_Status",
                table: "BrandSourceChunkSets",
                columns: new[] { "WorkspaceId", "BrandSourceExtractionId", "EmbeddingModel", "Status" },
                unique: true,
                filter: "Status <> 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrandSourceChunks");

            migrationBuilder.DropTable(
                name: "BrandSourceChunkSets");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_BrandSourceExtractions_Workspace_Id_Version_Status",
                table: "BrandSourceExtractions");
        }
    }
}
