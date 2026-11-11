using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Employee.Api.Migrations
{
    /// <summary>
    /// Perfil de jornada, disponibilidade semanal (jsonb), preferências (jsonb) e custo/hora do colaborador.
    /// Padrão dos colaboradores existentes (decisão): Abertura e Fechamento ALTERNADOS por loja e setor principal (menor
    /// SectorId), na ordem do nome — o mesmo desenho dos turnos fixos de manhã/tarde usados até aqui; ninguém vira
    /// Flexível sem decisão do gestor. Novos cadastros: Flexivel (padrão da coluna). Disponibilidade/preferências/custo
    /// ficam vazios (disponível sempre, sem preferência, sem custo).
    /// </summary>
    public partial class PerfilJornada : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CustoHora",
                table: "employees",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisponibilidadeJson",
                table: "employees",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "JornadaAtualizadaEm",
                table: "employees",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JornadaAtualizadaPor",
                table: "employees",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PerfilJornada",
                table: "employees",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Flexivel");

            migrationBuilder.AddColumn<string>(
                name: "PreferenciasJson",
                table: "employees",
                type: "jsonb",
                nullable: true);
            migrationBuilder.Sql("""
                UPDATE employees e SET "PerfilJornada" = CASE WHEN r.rn % 2 = 1 THEN 'Abertura' ELSE 'Fechamento' END
                FROM (
                    SELECT x."Id", row_number() OVER (
                        PARTITION BY x."StoreId", (SELECT min(es."SectorId"::text) FROM employee_sectors es WHERE es."EmployeeId" = x."Id")
                        ORDER BY x."Name", x."Registration") AS rn
                    FROM employees x) r
                WHERE r."Id" = e."Id";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustoHora",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "DisponibilidadeJson",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "JornadaAtualizadaEm",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "JornadaAtualizadaPor",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "PerfilJornada",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "PreferenciasJson",
                table: "employees");
        }
    }
}
