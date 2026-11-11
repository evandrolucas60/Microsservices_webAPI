using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Store.Api.Migrations
{
    /// <summary>
    /// Rodízio de abertura/fechamento na operação da loja/setor: quando faltar gente com perfil Abertura/Fechamento, o
    /// motor escala colaboradores de outro perfil nesses turnos (com penalidade, distribuído entre o setor). Padrão
    /// LIGADO: as operações existentes passam a usar o rodízio.
    /// </summary>
    public partial class RodizioAberturaFechamento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RodizioAberturaFechamento",
                table: "store_operations",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RodizioAberturaFechamento",
                table: "store_operations");
        }
    }
}
