using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scheduling.Api.Migrations
{
    /// <summary>
    /// Escala PRINCIPAL do setor no período (Principal, PrincipalDesde, PrincipalPor) e o pedido de torná-la principal ao
    /// terminar a geração (PrincipalPedidoPor). Sem backfill: as escalas existentes ficam sem principal e a continuidade,
    /// a ocupação em outros setores e a visão do mês seguem a prioridade do status (Publicada &gt; Aprovada &gt; Gerada &gt;
    /// Rascunho, a mais recente) — exatamente como antes — até o gestor escolher uma principal.
    /// </summary>
    public partial class EscalaPrincipal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Principal",
                table: "schedules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PrincipalDesde",
                table: "schedules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrincipalPor",
                table: "schedules",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrincipalPedidoPor",
                table: "schedules",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_schedules_StoreId_SectorId_Principal",
                table: "schedules",
                columns: new[] { "StoreId", "SectorId", "Principal" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_schedules_StoreId_SectorId_Principal",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "Principal",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "PrincipalDesde",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "PrincipalPor",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "PrincipalPedidoPor",
                table: "schedules");
        }
    }
}
