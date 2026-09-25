using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddAiOperationLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AiOperations_Status_RequestedAt",
                table: "AiOperations");

            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "AiOperations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AvailableAt",
                table: "AiOperations",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                table: "AiOperations",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeasedBy",
                table: "AiOperations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiOperations_Status_AvailableAt",
                table: "AiOperations",
                columns: new[] { "Status", "AvailableAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiOperations_Status_LeaseExpiresAt",
                table: "AiOperations",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AiOperations_Attempts_NotNegative",
                table: "AiOperations",
                sql: "Attempts >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AiOperations_Lease_Complete",
                table: "AiOperations",
                sql: "(LeasedBy IS NULL AND LeaseExpiresAt IS NULL) OR (LeasedBy IS NOT NULL AND LeaseExpiresAt IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AiOperations_Lease_Requires_Running",
                table: "AiOperations",
                sql: "LeasedBy IS NULL OR Status = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AiOperations_Status_AvailableAt",
                table: "AiOperations");

            migrationBuilder.DropIndex(
                name: "IX_AiOperations_Status_LeaseExpiresAt",
                table: "AiOperations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AiOperations_Attempts_NotNegative",
                table: "AiOperations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AiOperations_Lease_Complete",
                table: "AiOperations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AiOperations_Lease_Requires_Running",
                table: "AiOperations");

            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "AiOperations");

            migrationBuilder.DropColumn(
                name: "AvailableAt",
                table: "AiOperations");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "AiOperations");

            migrationBuilder.DropColumn(
                name: "LeasedBy",
                table: "AiOperations");

            migrationBuilder.CreateIndex(
                name: "IX_AiOperations_Status_RequestedAt",
                table: "AiOperations",
                columns: new[] { "Status", "RequestedAt" });
        }
    }
}
