using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scheduling.Api.Migrations
{
    /// <summary>
    /// Opções de escala avaliadas pelo motor na última geração entregue (SolucoesMotorJson: total de soluções, as últimas
    /// guardadas com o detalhamento do objetivo por componente e o status final). Só coluna nova e nula: escalas
    /// existentes não mudam.
    /// </summary>
    public partial class SolucoesMotor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SolucoesMotorJson",
                table: "schedules",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SolucoesMotorJson",
                table: "schedules");
        }
    }
}
