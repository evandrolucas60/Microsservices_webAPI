using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Employee.Api.Migrations
{
    /// <summary>
    /// Edição/exclusão de ausências (UpdatedAt/UpdatedBy) e observação limitada a 500 caracteres. O novo tipo
    /// AtestadoMedico ("Afastamento médico") é texto na coluna Type (sem mudança de esquema).
    /// </summary>
    public partial class UnavailabilityEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE unavailabilities SET "Note" = left("Note", 500) WHERE length("Note") > 500;""");
            migrationBuilder.AlterColumn<string>(
                name: "Note",
                table: "unavailabilities",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "unavailabilities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedBy",
                table: "unavailabilities",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // O código anterior não conhece AtestadoMedico
            migrationBuilder.Sql("""UPDATE unavailabilities SET "Type" = 'Afastamento' WHERE "Type" = 'AtestadoMedico';""");
            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "unavailabilities");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                table: "unavailabilities");

            migrationBuilder.AlterColumn<string>(
                name: "Note",
                table: "unavailabilities",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);
        }
    }
}
