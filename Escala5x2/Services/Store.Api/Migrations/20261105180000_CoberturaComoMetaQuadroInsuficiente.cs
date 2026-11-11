using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Store.Api.Migrations
{
    /// <summary>
    /// "Gerar mesmo quando o quadro não cobre o mínimo", na operação da loja/setor: no setor em que a cobertura mínima é
    /// impossível com o quadro (ex.: 3 pessoas para 07:00–22:00 com os intervalos), o motor gera com a cobertura como meta e
    /// avisa as faixas descobertas. Padrão LIGADO: as operações existentes passam a usar.
    /// </summary>
    public partial class CoberturaComoMetaQuadroInsuficiente : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CoberturaComoMetaQuadroInsuficiente",
                table: "store_operations",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoberturaComoMetaQuadroInsuficiente",
                table: "store_operations");
        }
    }
}
