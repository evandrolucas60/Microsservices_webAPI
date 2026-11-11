using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Store.Api.Migrations
{
    /// <summary>
    /// Folgas fixas e intercaladas em setor com 2 colaboradores, na operação da loja/setor: o motor dá a cada um os mesmos
    /// dias de folga toda semana, sem coincidir com as do outro. Padrão LIGADO: as operações existentes passam a usar.
    /// </summary>
    public partial class FolgasFixasSetorDuasPessoas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FolgasFixasSetorDuasPessoas",
                table: "store_operations",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FolgasFixasSetorDuasPessoas",
                table: "store_operations");
        }
    }
}
