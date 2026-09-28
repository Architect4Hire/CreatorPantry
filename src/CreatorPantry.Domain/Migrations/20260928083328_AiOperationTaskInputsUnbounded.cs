using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AiOperationTaskInputsUnbounded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "TaskInputsJson",
                table: "AiOperations",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(4000)",
                oldMaxLength: 4000,
                oldNullable: true);
        }

        /// <remarks>
        /// One-way in practice. SQL Server errors on truncation rather than truncating, so this reverts
        /// cleanly only while every stored TaskInputsJson is still under 4000 characters -- which stops being
        /// true as soon as one AIREC-002 request carries a long brief alongside a selected concept. Rolling
        /// back past this point means deciding what to do with those rows first.
        /// </remarks>
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "TaskInputsJson",
                table: "AiOperations",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);
        }
    }
}
