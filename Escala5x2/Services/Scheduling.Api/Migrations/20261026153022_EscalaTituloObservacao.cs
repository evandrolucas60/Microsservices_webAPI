using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scheduling.Api.Migrations
{
    /// <summary>
    /// Título (até 120) e observação (até 1000) da escala, editáveis por PATCH /schedules/{id}. Colunas novas e
    /// nulas: as escalas existentes não mudam.
    /// </summary>
    public partial class EscalaTituloObservacao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Observacao",
                table: "schedules",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Titulo",
                table: "schedules",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Observacao",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "Titulo",
                table: "schedules");
        }
    }
}
