using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Store.Api.Migrations
{
    /// <summary>
    /// Lojas pelos centros de custo da LG: ativo (lojas existentes ficam ativas), código do centro de custo (único quando
    /// preenchido) e data da última sincronização. Nenhum dado existente muda: a sincronização de lojas adota as lojas
    /// pelo código normalizado.
    /// </summary>
    public partial class LojasLg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Active",
                table: "stores",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "LgCentroDeCustoCodigo",
                table: "stores",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LgSincronizadoEm",
                table: "stores",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_stores_LgCentroDeCustoCodigo",
                table: "stores",
                column: "LgCentroDeCustoCodigo",
                unique: true,
                filter: "\"LgCentroDeCustoCodigo\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_stores_LgCentroDeCustoCodigo",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "Active",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "LgCentroDeCustoCodigo",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "LgSincronizadoEm",
                table: "stores");
        }
    }
}
