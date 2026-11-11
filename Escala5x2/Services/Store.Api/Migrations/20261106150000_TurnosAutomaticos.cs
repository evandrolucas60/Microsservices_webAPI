using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Store.Api.Migrations
{
    /// <summary>
    /// Turnos automáticos na operação da loja/setor ("se a loja já tem horário de funcionamento, por que definir turno?"):
    /// os turnos de abertura, fechamento e intermediários passam a ser DERIVADOS do horário de funcionamento de cada dia
    /// (feriado com horário especial, exceção do setor) e das regras de conformidade vigentes (jornada = carga semanal ÷ dias
    /// de trabalho do regime, limitada pelo limite diário/de domingo; intervalo pelas regras de intervalo). Padrão LIGADO:
    /// TODAS as operações existentes passam a usar os turnos automáticos. A duração da jornada, o intervalo e a janela de
    /// entradas flexíveis já gravados ficam guardados (não são apagados) e voltam a valer só se o gestor ligar
    /// "Personalizar turnos" na operação (TurnosAutomaticos = false).
    /// </summary>
    public partial class TurnosAutomaticos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TurnosAutomaticos",
                table: "store_operations",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TurnosAutomaticos",
                table: "store_operations");
        }
    }
}
