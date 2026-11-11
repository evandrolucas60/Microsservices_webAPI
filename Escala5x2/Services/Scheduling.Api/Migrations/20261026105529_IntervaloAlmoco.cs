using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scheduling.Api.Migrations
{
    /// <summary>
    /// Intervalo de refeição por célula: início (BreakStart), duração própria (BreakMinutes, nulo = a do turno) e quem
    /// posicionou (BreakOrigem). Linhas existentes ficam com BreakStart nulo (= não posicionado) e BreakOrigem = Motor;
    /// PaidMinutes não muda (sem duração própria, o intervalo efetivo continua o do turno).
    /// </summary>
    public partial class IntervaloAlmoco : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BreakMinutes",
                table: "schedule_assignments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BreakOrigem",
                table: "schedule_assignments",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Motor");

            migrationBuilder.AddColumn<TimeOnly>(
                name: "BreakStart",
                table: "schedule_assignments",
                type: "time without time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BreakMinutes",
                table: "schedule_assignments");

            migrationBuilder.DropColumn(
                name: "BreakOrigem",
                table: "schedule_assignments");

            migrationBuilder.DropColumn(
                name: "BreakStart",
                table: "schedule_assignments");
        }
    }
}
