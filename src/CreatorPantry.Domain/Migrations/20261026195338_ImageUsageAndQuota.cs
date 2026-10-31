using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class ImageUsageAndQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_AccountAiUsageEntries_Operation_Attempt",
                table: "AccountAiUsageEntries");

            migrationBuilder.AddColumn<Guid>(
                name: "AiQuotaReservationId",
                table: "GeneratedImageOperations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BilledVariantCount",
                table: "GeneratedImageOperations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FlaggedVariantCount",
                table: "GeneratedImageOperations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UnusableVariantCount",
                table: "GeneratedImageOperations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<Guid>(
                name: "AiOperationId",
                table: "AccountAiUsageEntries",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "GeneratedImageOperationId",
                table: "AccountAiUsageEntries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_GeneratedImageOperations_BilledVariants_Coherent",
                table: "GeneratedImageOperations",
                sql: "BilledVariantCount >= 0 AND UnusableVariantCount >= 0 AND FlaggedVariantCount >= 0 AND UnusableVariantCount <= BilledVariantCount AND FlaggedVariantCount <= UnusableVariantCount");

            migrationBuilder.CreateIndex(
                name: "UX_AccountAiUsageEntries_ImageOperation_Attempt",
                table: "AccountAiUsageEntries",
                columns: new[] { "GeneratedImageOperationId", "AttemptNumber" },
                unique: true,
                filter: "GeneratedImageOperationId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_AccountAiUsageEntries_Operation_Attempt",
                table: "AccountAiUsageEntries",
                columns: new[] { "AiOperationId", "AttemptNumber" },
                unique: true,
                filter: "AiOperationId IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AccountAiUsageEntries_Names_One_Operation",
                table: "AccountAiUsageEntries",
                sql: "(AiOperationId IS NOT NULL AND GeneratedImageOperationId IS NULL) OR (AiOperationId IS NULL AND GeneratedImageOperationId IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_GeneratedImageOperations_BilledVariants_Coherent",
                table: "GeneratedImageOperations");

            migrationBuilder.DropIndex(
                name: "UX_AccountAiUsageEntries_ImageOperation_Attempt",
                table: "AccountAiUsageEntries");

            migrationBuilder.DropIndex(
                name: "UX_AccountAiUsageEntries_Operation_Attempt",
                table: "AccountAiUsageEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AccountAiUsageEntries_Names_One_Operation",
                table: "AccountAiUsageEntries");

            migrationBuilder.DropColumn(
                name: "AiQuotaReservationId",
                table: "GeneratedImageOperations");

            migrationBuilder.DropColumn(
                name: "BilledVariantCount",
                table: "GeneratedImageOperations");

            migrationBuilder.DropColumn(
                name: "FlaggedVariantCount",
                table: "GeneratedImageOperations");

            migrationBuilder.DropColumn(
                name: "UnusableVariantCount",
                table: "GeneratedImageOperations");

            migrationBuilder.DropColumn(
                name: "GeneratedImageOperationId",
                table: "AccountAiUsageEntries");

            migrationBuilder.AlterColumn<Guid>(
                name: "AiOperationId",
                table: "AccountAiUsageEntries",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_AccountAiUsageEntries_Operation_Attempt",
                table: "AccountAiUsageEntries",
                columns: new[] { "AiOperationId", "AttemptNumber" },
                unique: true);
        }
    }
}
