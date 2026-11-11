using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Employee.Api.Migrations
{
    /// <summary>
    /// Férias da LG (ServicoDeFerias/ConsultarLista): a origem da ausência ("LG" = período importado e mantido pela
    /// sincronização; nulo = registro manual, que a sincronização nunca toca) e a data da última consulta das férias do
    /// colaborador (aviso "usando a última sincronização de ..." na geração quando a LG não responde).
    /// </summary>
    public partial class FeriasLg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Origem",
                table: "unavailabilities",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LgFeriasSincronizadoEm",
                table: "employees",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Origem",
                table: "unavailabilities");

            migrationBuilder.DropColumn(
                name: "LgFeriasSincronizadoEm",
                table: "employees");
        }
    }
}
