using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    PlanKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Source = table.Column<int>(type: "int", nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SupersedesFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountPlans", x => x.Id);
                    table.UniqueConstraint("AK_AccountPlans_Account_EffectiveFrom", x => new { x.AccountId, x.EffectiveFrom });
                    table.CheckConstraint("CK_AccountPlans_After_Superseded", "SupersedesFrom IS NULL OR SupersedesFrom < EffectiveFrom");
                    table.CheckConstraint("CK_AccountPlans_PlanKey_NotBlank", "PlanKey IS NULL OR trim(PlanKey) <> ''");
                    table.CheckConstraint("CK_AccountPlans_Source_Declared", "Source <> 0");
                    table.ForeignKey(
                        name: "FK_AccountPlans_Superseded",
                        columns: x => new { x.AccountId, x.SupersedesFrom },
                        principalTable: "AccountPlans",
                        principalColumns: new[] { "AccountId", "EffectiveFrom" });
                });

            migrationBuilder.CreateIndex(
                name: "UX_AccountPlans_Account_Root",
                table: "AccountPlans",
                column: "AccountId",
                unique: true,
                filter: "SupersedesFrom IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_AccountPlans_Account_Successor",
                table: "AccountPlans",
                columns: new[] { "AccountId", "SupersedesFrom" },
                unique: true,
                filter: "SupersedesFrom IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountPlans");
        }
    }
}
