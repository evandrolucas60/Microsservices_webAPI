using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scheduling.Api.Migrations
{
    /// <summary>
    /// Diagnóstico de inviabilidade (DiagnosticoJson) e tempo do motor da última geração (TempoLimiteSegundos, usado no
    /// "tentar com mais tempo"). O novo status TimedOut (Sem solução no tempo) cabe na coluna de status (texto). Só colunas
    /// novas e nulas: escalas existentes não mudam.
    /// </summary>
    public partial class MotorStatusDiagnostico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DiagnosticoJson",
                table: "schedules",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TempoLimiteSegundos",
                table: "schedules",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiagnosticoJson",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "TempoLimiteSegundos",
                table: "schedules");
        }
    }
}
