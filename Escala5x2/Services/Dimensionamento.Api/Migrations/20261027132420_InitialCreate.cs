using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dimensionamento.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "perfis_demanda",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    SectorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Nome = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Origem = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Observacao = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PeriodoInicio = table.Column<DateOnly>(type: "date", nullable: true),
                    PeriodoFim = table.Column<DateOnly>(type: "date", nullable: true),
                    Dias = table.Column<int>(type: "integer", nullable: true),
                    TotalDocumentos = table.Column<double>(type: "double precision", nullable: false),
                    CurvaJson = table.Column<string>(type: "jsonb", nullable: false),
                    FatoresDiaJson = table.Column<string>(type: "jsonb", nullable: true),
                    CheckoutsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CriadoPor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CriadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AtualizadoPor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AtualizadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_perfis_demanda", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cenarios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    SectorId = table.Column<Guid>(type: "uuid", nullable: true),
                    PerfilDemandaId = table.Column<Guid>(type: "uuid", nullable: true),
                    Nome = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Descricao = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ParametrosJson = table.Column<string>(type: "jsonb", nullable: false),
                    ResumoJson = table.Column<string>(type: "jsonb", nullable: true),
                    ResultadoJson = table.Column<string>(type: "jsonb", nullable: true),
                    CalculadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CriadoPor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CriadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AtualizadoPor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AtualizadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cenarios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cenarios_perfis_demanda_PerfilDemandaId",
                        column: x => x.PerfilDemandaId,
                        principalTable: "perfis_demanda",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cenarios_PerfilDemandaId",
                table: "cenarios",
                column: "PerfilDemandaId");

            migrationBuilder.CreateIndex(
                name: "IX_cenarios_StoreId_SectorId",
                table: "cenarios",
                columns: new[] { "StoreId", "SectorId" });

            migrationBuilder.CreateIndex(
                name: "IX_perfis_demanda_StoreId_SectorId",
                table: "perfis_demanda",
                columns: new[] { "StoreId", "SectorId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cenarios");

            migrationBuilder.DropTable(
                name: "perfis_demanda");
        }
    }
}
