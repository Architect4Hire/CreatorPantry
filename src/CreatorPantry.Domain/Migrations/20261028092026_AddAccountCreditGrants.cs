using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountCreditGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountCreditGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    CreditsGranted = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    CreditsRemaining = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    CreditsClawedBack = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    ExternalReferenceId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    GrantedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClawedBackAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountCreditGrants", x => x.Id);
                    table.CheckConstraint("CK_AccountCreditGrants_ClawBack_Recorded", "(ClawedBackAt IS NULL AND CAST(CreditsClawedBack AS FLOAT) = 0) OR (ClawedBackAt IS NOT NULL AND CAST(CreditsRemaining AS FLOAT) = 0 AND ClawedBackAt >= GrantedAt)");
                    table.CheckConstraint("CK_AccountCreditGrants_ClawedBack_Within_Granted", "CAST(CreditsClawedBack AS FLOAT) >= 0 AND CAST(CreditsClawedBack AS FLOAT) <= CAST(CreditsGranted AS FLOAT)");
                    table.CheckConstraint("CK_AccountCreditGrants_Granted_Positive", "CAST(CreditsGranted AS FLOAT) > 0");
                    table.CheckConstraint("CK_AccountCreditGrants_Purchase_Referenced", "Source <> 1 OR ExternalReferenceId IS NOT NULL");
                    table.CheckConstraint("CK_AccountCreditGrants_Reference_NotBlank", "ExternalReferenceId IS NULL OR trim(ExternalReferenceId) <> ''");
                    table.CheckConstraint("CK_AccountCreditGrants_Remaining_Within_Granted", "CAST(CreditsRemaining AS FLOAT) >= 0 AND CAST(CreditsRemaining AS FLOAT) <= CAST(CreditsGranted AS FLOAT)");
                    table.CheckConstraint("CK_AccountCreditGrants_Source_Declared", "Source <> 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountCreditGrants_Account_GrantedAt",
                table: "AccountCreditGrants",
                columns: new[] { "AccountId", "GrantedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_AccountCreditGrants_Source_Reference",
                table: "AccountCreditGrants",
                columns: new[] { "Source", "ExternalReferenceId" },
                unique: true,
                filter: "ExternalReferenceId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountCreditGrants");
        }
    }
}
