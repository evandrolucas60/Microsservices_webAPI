using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Employee.Api.Migrations
{
    /// <summary>
    /// Integração LG (SOAP ConsultarListaHistoricosVigenteNaDataPorDemanda): empresa, situação (código/descrição e início),
    /// admissão, centro de custo, data da sincronização, registro completo (jsonb) e data da inativação automática.
    /// Colunas nulas = cadastro só da Escala 5x2 (comportamento de antes). Índice (empresa, matrícula): a mesma pessoa
    /// em outra loja (mudança de centro de custo).
    /// </summary>
    public partial class IntegracaoLg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LgCentroDeCusto",
                table: "employees",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LgDataAdmissao",
                table: "employees",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LgDataInicioSituacao",
                table: "employees",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LgEmpresaCodigo",
                table: "employees",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LgInativadoEm",
                table: "employees",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LgRegistroJson",
                table: "employees",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LgSincronizadoEm",
                table: "employees",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LgSituacaoCodigo",
                table: "employees",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LgSituacaoDescricao",
                table: "employees",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_employees_LgEmpresaCodigo_Registration",
                table: "employees",
                columns: new[] { "LgEmpresaCodigo", "Registration" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_employees_LgEmpresaCodigo_Registration",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "LgCentroDeCusto",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "LgDataAdmissao",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "LgDataInicioSituacao",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "LgEmpresaCodigo",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "LgInativadoEm",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "LgRegistroJson",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "LgSincronizadoEm",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "LgSituacaoCodigo",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "LgSituacaoDescricao",
                table: "employees");
        }
    }
}
