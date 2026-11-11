using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Store.Api.Migrations
{
    /// <summary>
    /// Operação da loja/setor (store_operations): expediente, mínimos rígidos (cobertura, abertura, fechamento), entradas
    /// flexíveis, duração da jornada e intervalo dos turnos gerados. Uma linha por loja (SectorId nulo) e no máximo uma
    /// exceção por setor. Semeia a loja DEMO (se existir e ainda não tiver operação): 07:00–22:00, mínimos 2/2/2,
    /// incremento de 15 min, janela flexível padrão (07:15–13:25), jornada de 500 min com 60 min de intervalo.
    /// Lojas sem operação seguem o modelo anterior (só demanda por faixa).
    /// </summary>
    public partial class OperacaoLoja : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "store_operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    SectorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Abertura = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    Fechamento = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    CoberturaMinima = table.Column<int>(type: "integer", nullable: false),
                    MinimoAbertura = table.Column<int>(type: "integer", nullable: false),
                    MinimoFechamento = table.Column<int>(type: "integer", nullable: false),
                    IncrementoFlexivelMinutos = table.Column<int>(type: "integer", nullable: false),
                    EntradaFlexivelMaisCedo = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    EntradaFlexivelMaisTarde = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    DuracaoJornadaMinutos = table.Column<int>(type: "integer", nullable: false),
                    IntervaloMinutos = table.Column<int>(type: "integer", nullable: true),
                    AtualizadoPor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AtualizadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_operations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_store_operations_stores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_store_operations_SectorId",
                table: "store_operations",
                column: "SectorId",
                unique: true,
                filter: "\"SectorId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_store_operations_StoreId_loja",
                table: "store_operations",
                column: "StoreId",
                unique: true,
                filter: "\"SectorId\" IS NULL");

            migrationBuilder.Sql("""
                INSERT INTO store_operations ("Id", "StoreId", "SectorId", "Abertura", "Fechamento", "CoberturaMinima", "MinimoAbertura",
                    "MinimoFechamento", "IncrementoFlexivelMinutos", "EntradaFlexivelMaisCedo", "EntradaFlexivelMaisTarde",
                    "DuracaoJornadaMinutos", "IntervaloMinutos", "AtualizadoPor", "AtualizadoEm")
                SELECT '5d1f0a7e-6c33-4c55-9a43-0f2b7c1e0a01', s."Id", NULL, '07:00', '22:00', 2, 2, 2, 15, NULL, NULL, 500, 60, 'seed (migração OperacaoLoja)', now()
                FROM stores s
                WHERE s."Id" = '8f0c5a3e-1d2b-4c6a-9e7f-0a1b2c3d4e01'
                  AND NOT EXISTS (SELECT 1 FROM store_operations o WHERE o."StoreId" = s."Id" AND o."SectorId" IS NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "store_operations");
        }
    }
}
