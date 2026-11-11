using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Store.Api.Migrations
{
    /// <summary>
    /// Setores ativos/inativos: o catálogo de órgãos responsáveis da LG (ConsultarListaPorDemanda) inativa o setor da LG
    /// cujo órgão ficou inativo, perdeu a habilitação para a empresa ou saiu do catálogo (nunca apaga) e o reativa se o
    /// órgão voltar. Todos os setores existentes ficam ativos. Setor manual nunca é inativado pela LG.
    /// </summary>
    public partial class SetoresAtivos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Active",
                table: "sectors",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Active",
                table: "sectors");
        }
    }
}
