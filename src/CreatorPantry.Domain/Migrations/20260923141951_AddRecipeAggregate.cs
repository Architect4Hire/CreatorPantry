using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeAggregate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Recipes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Headnote = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    StorageNotes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    AttributionText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SourceUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    CuisineId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CourseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PrimaryTechniqueId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PrepTimeMinutes = table.Column<int>(type: "int", nullable: true),
                    CookTimeMinutes = table.Column<int>(type: "int", nullable: true),
                    RestTimeMinutes = table.Column<int>(type: "int", nullable: true),
                    TotalTimeMinutes = table.Column<int>(type: "int", nullable: true),
                    YieldText = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    YieldQuantity = table.Column<decimal>(type: "decimal(28,12)", nullable: true),
                    YieldUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    YieldUnitDimension = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recipes", x => x.Id);
                    table.UniqueConstraint("AK_Recipes_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_Recipes_Times_NonNegative", "(PrepTimeMinutes IS NULL OR PrepTimeMinutes >= 0) AND (CookTimeMinutes IS NULL OR CookTimeMinutes >= 0) AND (RestTimeMinutes IS NULL OR RestTimeMinutes >= 0) AND (TotalTimeMinutes IS NULL OR TotalTimeMinutes >= 0)");
                    table.CheckConstraint("CK_Recipes_Yield_Positive", "YieldQuantity IS NULL OR YieldQuantity > 0");
                    table.CheckConstraint("CK_Recipes_YieldUnit_Dimension", "(YieldUnitId IS NULL AND YieldUnitDimension IS NULL) OR (YieldUnitId IS NOT NULL AND YieldUnitDimension IS NOT NULL AND YieldUnitDimension <> 3)");
                    table.CheckConstraint("CK_Recipes_YieldUnit_RequiresQuantity", "YieldUnitId IS NULL OR YieldQuantity IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_Recipes_CookingTechniques_PrimaryTechniqueId",
                        column: x => x.PrimaryTechniqueId,
                        principalTable: "CookingTechniques",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Recipes_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Recipes_Cuisines_CuisineId",
                        column: x => x.CuisineId,
                        principalTable: "Cuisines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Recipes_MeasurementUnits_YieldUnitId_YieldUnitDimension",
                        columns: x => new { x.YieldUnitId, x.YieldUnitDimension },
                        principalTable: "MeasurementUnits",
                        principalColumns: new[] { "Id", "Dimension" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Recipes_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecipeAssetLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    MediaAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Caption = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeAssetLinks", x => x.Id);
                    table.CheckConstraint("CK_RecipeAssetLinks_Role_Specified", "Role <> 0");
                    table.CheckConstraint("CK_RecipeAssetLinks_SortOrder_NonNegative", "SortOrder >= 0");
                    table.ForeignKey(
                        name: "FK_RecipeAssetLinks_Recipes_WorkspaceId_RecipeId",
                        columns: x => new { x.WorkspaceId, x.RecipeId },
                        principalTable: "Recipes",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecipeEquipment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    DisplayText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    EquipmentTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsOptional = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeEquipment", x => x.Id);
                    table.CheckConstraint("CK_RecipeEquipment_SortOrder_NonNegative", "SortOrder >= 0");
                    table.ForeignKey(
                        name: "FK_RecipeEquipment_EquipmentTypes_EquipmentTypeId",
                        column: x => x.EquipmentTypeId,
                        principalTable: "EquipmentTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeEquipment_Recipes_WorkspaceId_RecipeId",
                        columns: x => new { x.WorkspaceId, x.RecipeId },
                        principalTable: "Recipes",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecipeIngredientGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeIngredientGroups", x => x.Id);
                    table.UniqueConstraint("AK_RecipeIngredientGroups_Workspace_Recipe_Id", x => new { x.WorkspaceId, x.RecipeId, x.Id });
                    table.CheckConstraint("CK_RecipeIngredientGroups_SortOrder_NonNegative", "SortOrder >= 0");
                    table.ForeignKey(
                        name: "FK_RecipeIngredientGroups_Recipes_WorkspaceId_RecipeId",
                        columns: x => new { x.WorkspaceId, x.RecipeId },
                        principalTable: "Recipes",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecipeInstructionGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeInstructionGroups", x => x.Id);
                    table.UniqueConstraint("AK_RecipeInstructionGroups_Workspace_Recipe_Id", x => new { x.WorkspaceId, x.RecipeId, x.Id });
                    table.CheckConstraint("CK_RecipeInstructionGroups_SortOrder_NonNegative", "SortOrder >= 0");
                    table.ForeignKey(
                        name: "FK_RecipeInstructionGroups_Recipes_WorkspaceId_RecipeId",
                        columns: x => new { x.WorkspaceId, x.RecipeId },
                        principalTable: "Recipes",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecipeIngredients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeIngredientGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    DisplayText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    IngredientNameText = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(28,12)", nullable: true),
                    QuantityUpper = table.Column<decimal>(type: "decimal(28,12)", nullable: true),
                    MeasurementUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MeasurementUnitDimension = table.Column<int>(type: "int", nullable: true),
                    IngredientId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MatchStatus = table.Column<int>(type: "int", nullable: false),
                    PreparationNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsOptional = table.Column<bool>(type: "bit", nullable: false),
                    ScalingBehavior = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeIngredients", x => x.Id);
                    table.CheckConstraint("CK_RecipeIngredients_Match_Status", "(IngredientId IS NOT NULL AND MatchStatus = 1) OR (IngredientId IS NULL AND MatchStatus <> 1)");
                    table.CheckConstraint("CK_RecipeIngredients_Quantity_Positive", "(Quantity IS NULL OR Quantity > 0) AND (QuantityUpper IS NULL OR QuantityUpper > 0)");
                    table.CheckConstraint("CK_RecipeIngredients_Quantity_Range", "QuantityUpper IS NULL OR (Quantity IS NOT NULL AND QuantityUpper > Quantity)");
                    table.CheckConstraint("CK_RecipeIngredients_SortOrder_NonNegative", "SortOrder >= 0");
                    table.CheckConstraint("CK_RecipeIngredients_Unit_Dimension", "(MeasurementUnitId IS NULL AND MeasurementUnitDimension IS NULL) OR (MeasurementUnitId IS NOT NULL AND MeasurementUnitDimension IS NOT NULL AND MeasurementUnitDimension <> 3)");
                    table.ForeignKey(
                        name: "FK_RecipeIngredients_Ingredients_IngredientId",
                        column: x => x.IngredientId,
                        principalTable: "Ingredients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeIngredients_MeasurementUnits_MeasurementUnitId_MeasurementUnitDimension",
                        columns: x => new { x.MeasurementUnitId, x.MeasurementUnitDimension },
                        principalTable: "MeasurementUnits",
                        principalColumns: new[] { "Id", "Dimension" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeIngredients_RecipeIngredientGroups_WorkspaceId_RecipeId_RecipeIngredientGroupId",
                        columns: x => new { x.WorkspaceId, x.RecipeId, x.RecipeIngredientGroupId },
                        principalTable: "RecipeIngredientGroups",
                        principalColumns: new[] { "WorkspaceId", "RecipeId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecipeInstructionSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeInstructionGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    TechniqueId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DurationMinutes = table.Column<int>(type: "int", nullable: true),
                    TemperatureValue = table.Column<decimal>(type: "decimal(28,12)", nullable: true),
                    TemperatureUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TemperatureUnitDimension = table.Column<int>(type: "int", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeInstructionSteps", x => x.Id);
                    table.CheckConstraint("CK_RecipeInstructionSteps_Duration_NonNegative", "DurationMinutes IS NULL OR DurationMinutes >= 0");
                    table.CheckConstraint("CK_RecipeInstructionSteps_SortOrder_NonNegative", "SortOrder >= 0");
                    table.CheckConstraint("CK_RecipeInstructionSteps_Temperature_Dimension", "(TemperatureValue IS NULL AND TemperatureUnitId IS NULL AND TemperatureUnitDimension IS NULL) OR (TemperatureValue IS NOT NULL AND TemperatureUnitId IS NOT NULL AND TemperatureUnitDimension = 3)");
                    table.ForeignKey(
                        name: "FK_RecipeInstructionSteps_CookingTechniques_TechniqueId",
                        column: x => x.TechniqueId,
                        principalTable: "CookingTechniques",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeInstructionSteps_MeasurementUnits_TemperatureUnitId_TemperatureUnitDimension",
                        columns: x => new { x.TemperatureUnitId, x.TemperatureUnitDimension },
                        principalTable: "MeasurementUnits",
                        principalColumns: new[] { "Id", "Dimension" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeInstructionSteps_RecipeInstructionGroups_WorkspaceId_RecipeId_RecipeInstructionGroupId",
                        columns: x => new { x.WorkspaceId, x.RecipeId, x.RecipeInstructionGroupId },
                        principalTable: "RecipeInstructionGroups",
                        principalColumns: new[] { "WorkspaceId", "RecipeId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeAssetLinks_Workspace_MediaAsset",
                table: "RecipeAssetLinks",
                columns: new[] { "WorkspaceId", "MediaAssetId" });

            migrationBuilder.CreateIndex(
                name: "UX_RecipeAssetLinks_Workspace_Recipe_Hero",
                table: "RecipeAssetLinks",
                columns: new[] { "WorkspaceId", "RecipeId" },
                unique: true,
                filter: "Role = 1");

            migrationBuilder.CreateIndex(
                name: "UX_RecipeAssetLinks_Workspace_Recipe_Order",
                table: "RecipeAssetLinks",
                columns: new[] { "WorkspaceId", "RecipeId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipeEquipment_EquipmentTypeId",
                table: "RecipeEquipment",
                column: "EquipmentTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeEquipment_Workspace_EquipmentType",
                table: "RecipeEquipment",
                columns: new[] { "WorkspaceId", "EquipmentTypeId" });

            migrationBuilder.CreateIndex(
                name: "UX_RecipeEquipment_Workspace_Recipe_Order",
                table: "RecipeEquipment",
                columns: new[] { "WorkspaceId", "RecipeId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_RecipeIngredientGroups_Workspace_Recipe_Order",
                table: "RecipeIngredientGroups",
                columns: new[] { "WorkspaceId", "RecipeId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipeIngredients_IngredientId",
                table: "RecipeIngredients",
                column: "IngredientId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeIngredients_MeasurementUnitId_MeasurementUnitDimension",
                table: "RecipeIngredients",
                columns: new[] { "MeasurementUnitId", "MeasurementUnitDimension" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeIngredients_Workspace_Ingredient",
                table: "RecipeIngredients",
                columns: new[] { "WorkspaceId", "IngredientId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeIngredients_WorkspaceId_RecipeId_RecipeIngredientGroupId",
                table: "RecipeIngredients",
                columns: new[] { "WorkspaceId", "RecipeId", "RecipeIngredientGroupId" });

            migrationBuilder.CreateIndex(
                name: "UX_RecipeIngredients_Workspace_Group_Order",
                table: "RecipeIngredients",
                columns: new[] { "WorkspaceId", "RecipeIngredientGroupId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_RecipeInstructionGroups_Workspace_Recipe_Order",
                table: "RecipeInstructionGroups",
                columns: new[] { "WorkspaceId", "RecipeId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipeInstructionSteps_TechniqueId",
                table: "RecipeInstructionSteps",
                column: "TechniqueId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeInstructionSteps_TemperatureUnitId_TemperatureUnitDimension",
                table: "RecipeInstructionSteps",
                columns: new[] { "TemperatureUnitId", "TemperatureUnitDimension" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeInstructionSteps_WorkspaceId_RecipeId_RecipeInstructionGroupId",
                table: "RecipeInstructionSteps",
                columns: new[] { "WorkspaceId", "RecipeId", "RecipeInstructionGroupId" });

            migrationBuilder.CreateIndex(
                name: "UX_RecipeInstructionSteps_Workspace_Group_Order",
                table: "RecipeInstructionSteps",
                columns: new[] { "WorkspaceId", "RecipeInstructionGroupId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_CourseId",
                table: "Recipes",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_CuisineId",
                table: "Recipes",
                column: "CuisineId");

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_PrimaryTechniqueId",
                table: "Recipes",
                column: "PrimaryTechniqueId");

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_Workspace_Status_UpdatedAt",
                table: "Recipes",
                columns: new[] { "WorkspaceId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_Workspace_Title",
                table: "Recipes",
                columns: new[] { "WorkspaceId", "Title" });

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_YieldUnitId_YieldUnitDimension",
                table: "Recipes",
                columns: new[] { "YieldUnitId", "YieldUnitDimension" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecipeAssetLinks");

            migrationBuilder.DropTable(
                name: "RecipeEquipment");

            migrationBuilder.DropTable(
                name: "RecipeIngredients");

            migrationBuilder.DropTable(
                name: "RecipeInstructionSteps");

            migrationBuilder.DropTable(
                name: "RecipeIngredientGroups");

            migrationBuilder.DropTable(
                name: "RecipeInstructionGroups");

            migrationBuilder.DropTable(
                name: "Recipes");
        }
    }
}
