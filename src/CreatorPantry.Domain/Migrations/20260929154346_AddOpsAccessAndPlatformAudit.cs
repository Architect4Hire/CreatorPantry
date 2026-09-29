using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddOpsAccessAndPlatformAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OpsApiClients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    KeyPrefix = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    KeyHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    KeySalt = table.Column<byte[]>(type: "varbinary(16)", maxLength: 16, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RotatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpsApiClients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformAuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorType = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ActorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SubjectId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    BeforeReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AfterReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformAuditLogs", x => x.Id);
                    table.CheckConstraint("CK_PlatformAuditLogs_ActorType", "[ActorType] <> 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountAiQuotaReservations_Posted_Account",
                table: "AccountAiQuotaReservations",
                columns: new[] { "PostedAt", "AccountId" },
                filter: "PostedAt IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_OpsApiClients_KeyPrefix",
                table: "OpsApiClients",
                column: "KeyPrefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_OpsApiClients_Name",
                table: "OpsApiClients",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditLogs_OccurredAt",
                table: "PlatformAuditLogs",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditLogs_Subject_OccurredAt",
                table: "PlatformAuditLogs",
                columns: new[] { "SubjectType", "SubjectId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OpsApiClients");

            migrationBuilder.DropTable(
                name: "PlatformAuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AccountAiQuotaReservations_Posted_Account",
                table: "AccountAiQuotaReservations");
        }
    }
}
