using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scheduling.Api.Migrations
{
    /// <summary>
    /// Cancelamento da geração (POST /api/schedules/{id}/cancelar-geracao): id da geração em andamento (GeracaoId, vai ao
    /// Worker e volta nos resultados), quando ela foi pedida (GeracaoIniciadaEm, tempo decorrido na tela), o estado anterior
    /// da escala numa geração na própria escala (GeracaoAnteriorJson, para devolvê-la como estava) e quem/quando cancelou.
    /// O novo status "Cancelled" é texto (coluna Status varchar(20)): não precisa de conversão. Sem backfill: escalas
    /// existentes ficam com tudo nulo (gerações pedidas antes desta versão não citam id e continuam aceitas).
    /// </summary>
    public partial class CancelarGeracao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GeracaoId",
                table: "schedules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GeracaoIniciadaEm",
                table: "schedules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GeracaoAnteriorJson",
                table: "schedules",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GeracaoCanceladaEm",
                table: "schedules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GeracaoCanceladaPor",
                table: "schedules",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GeracaoId",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "GeracaoIniciadaEm",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "GeracaoAnteriorJson",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "GeracaoCanceladaEm",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "GeracaoCanceladaPor",
                table: "schedules");
        }
    }
}
