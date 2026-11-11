using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Employee.Api.Migrations
{
    /// <summary>
    /// Setor padrão pelo OrgaoResponsavel da LG: órgão atual (código, descrição e setor ligado no Store.Api) e a marca de
    /// setores alterados manualmente (quem/quando). Colaboradores existentes COM setor ficam marcados como manuais
    /// (preserva o comportamento anterior: a LG não mexia em quem já tinha setor); sem autor/data, a primeira sincronização
    /// desfaz a marca quando o setor já é exatamente o do órgão da LG (ver LgRegras.AplicarSetorLg).
    /// </summary>
    public partial class SetoresLg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LgOrgaoCodigo",
                table: "employees",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LgOrgaoDescricao",
                table: "employees",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LgSetorId",
                table: "employees",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SetoresAlteradosEm",
                table: "employees",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SetoresAlteradosPor",
                table: "employees",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SetoresManuais",
                table: "employees",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE employees e SET "SetoresManuais" = TRUE
                WHERE EXISTS (SELECT 1 FROM employee_sectors s WHERE s."EmployeeId" = e."Id");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LgOrgaoCodigo",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "LgOrgaoDescricao",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "LgSetorId",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "SetoresAlteradosEm",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "SetoresAlteradosPor",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "SetoresManuais",
                table: "employees");
        }
    }
}
