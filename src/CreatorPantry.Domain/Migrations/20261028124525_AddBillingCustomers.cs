using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingCustomers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BillingCustomers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ExternalCustomerId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    IsLiveMode = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingCustomers", x => x.Id);
                    table.CheckConstraint("CK_BillingCustomers_AccountId_NotBlank", "trim(AccountId) <> ''");
                    table.CheckConstraint("CK_BillingCustomers_ExternalCustomerId_NotBlank", "trim(ExternalCustomerId) <> ''");
                });

            migrationBuilder.CreateIndex(
                name: "UX_BillingCustomers_Account_Mode",
                table: "BillingCustomers",
                columns: new[] { "AccountId", "IsLiveMode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BillingCustomers_ExternalCustomerId",
                table: "BillingCustomers",
                column: "ExternalCustomerId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingCustomers");
        }
    }
}
