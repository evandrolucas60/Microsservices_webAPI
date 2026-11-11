using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Store.Api.Migrations
{
    /// <summary>
    /// Setores a partir do OrgaoResponsavel da LG: origem (setores existentes ficam "Manual"), código e descrição do órgão
    /// e data da última sincronização. Índice único (loja, código do órgão) quando preenchido: no máximo um setor por
    /// órgão em cada loja e proteção contra duas sincronizações simultâneas. Ele cobre a chave estrangeira StoreId, por
    /// isso substitui o índice IX_sectors_StoreId. Nenhum setor existente muda de nome: a sincronização adota pelo nome.
    /// </summary>
    public partial class SetoresLg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sectors_StoreId",
                table: "sectors");

            migrationBuilder.AddColumn<int>(
                name: "LgOrgaoCodigo",
                table: "sectors",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LgOrgaoDescricao",
                table: "sectors",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LgSincronizadoEm",
                table: "sectors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origem",
                table: "sectors",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.CreateIndex(
                name: "IX_sectors_StoreId_LgOrgaoCodigo",
                table: "sectors",
                columns: new[] { "StoreId", "LgOrgaoCodigo" },
                unique: true,
                filter: "\"LgOrgaoCodigo\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sectors_StoreId_LgOrgaoCodigo",
                table: "sectors");

            migrationBuilder.DropColumn(
                name: "LgOrgaoCodigo",
                table: "sectors");

            migrationBuilder.DropColumn(
                name: "LgOrgaoDescricao",
                table: "sectors");

            migrationBuilder.DropColumn(
                name: "LgSincronizadoEm",
                table: "sectors");

            migrationBuilder.DropColumn(
                name: "Origem",
                table: "sectors");

            migrationBuilder.CreateIndex(
                name: "IX_sectors_StoreId",
                table: "sectors",
                column: "StoreId");
        }
    }
}
