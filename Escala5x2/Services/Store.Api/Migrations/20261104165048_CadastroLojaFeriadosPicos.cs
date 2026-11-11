using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Store.Api.Migrations
{
    /// <summary>
    /// Cadastro da loja (endereço, código IBGE, horário de funcionamento por dia e padrão de feriados), feriados com
    /// abrangência Loja / ponto facultativo / funcionamento (e exceção por loja) e picos manuais.
    /// Dados existentes: as lojas com operação recebem o horário de funcionamento igual ao expediente da operação nos 7
    /// dias (o motor continua com o mesmo expediente); lojas sem operação ficam sem horário cadastrado.
    /// </summary>
    public partial class CadastroLojaFeriadosPicos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Bairro",
                table: "stores",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CadastroAtualizadoEm",
                table: "stores",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CadastroAtualizadoPor",
                table: "stores",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Cep",
                table: "stores",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CodigoIbge",
                table: "stores",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "FeriadoAbertura",
                table: "stores",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "FeriadoFechamento",
                table: "stores",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FeriadoFuncionamento",
                table: "stores",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Logradouro",
                table: "stores",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Numero",
                table: "stores",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "Abertura",
                table: "holidays",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CodigoIbge",
                table: "holidays",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "Fechamento",
                table: "holidays",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Funcionamento",
                table: "holidays",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PontoFacultativo",
                table: "holidays",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "StoreId",
                table: "holidays",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "holiday_store_hours",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HolidayId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    Funcionamento = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Abertura = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    Fechamento = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    AtualizadoPor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AtualizadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holiday_store_hours", x => x.Id);
                    table.ForeignKey(
                        name: "FK_holiday_store_hours_holidays_HolidayId",
                        column: x => x.HolidayId,
                        principalTable: "holidays",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_holiday_store_hours_stores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "manual_peaks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    SectorId = table.Column<Guid>(type: "uuid", nullable: true),
                    DiaSemana = table.Column<int>(type: "integer", nullable: true),
                    Data = table.Column<DateOnly>(type: "date", nullable: true),
                    Inicio = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    Fim = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    Quantidade = table.Column<int>(type: "integer", nullable: true),
                    Adicional = table.Column<int>(type: "integer", nullable: true),
                    Descricao = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    AtualizadoPor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AtualizadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manual_peaks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_manual_peaks_stores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "store_hours",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    DiaSemana = table.Column<int>(type: "integer", nullable: false),
                    Fechada = table.Column<bool>(type: "boolean", nullable: false),
                    Abertura = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    Fechamento = table.Column<TimeOnly>(type: "time without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_hours", x => x.Id);
                    table.ForeignKey(
                        name: "FK_store_hours_stores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_holiday_store_hours_HolidayId_StoreId",
                table: "holiday_store_hours",
                columns: new[] { "HolidayId", "StoreId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_holiday_store_hours_StoreId",
                table: "holiday_store_hours",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_manual_peaks_StoreId",
                table: "manual_peaks",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_store_hours_StoreId_DiaSemana",
                table: "store_hours",
                columns: new[] { "StoreId", "DiaSemana" },
                unique: true);

            // Horário de funcionamento a partir da operação da loja (mesmo expediente nos 7 dias; DayOfWeek 0 = domingo)
            migrationBuilder.Sql(
                """
                INSERT INTO store_hours ("Id", "StoreId", "DiaSemana", "Fechada", "Abertura", "Fechamento")
                SELECT md5(o."StoreId"::text || '-' || d.dia::text)::uuid, o."StoreId", d.dia, false, o."Abertura", o."Fechamento"
                FROM store_operations o
                CROSS JOIN generate_series(0, 6) AS d(dia)
                WHERE o."SectorId" IS NULL
                ON CONFLICT ("StoreId", "DiaSemana") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holiday_store_hours");

            migrationBuilder.DropTable(
                name: "manual_peaks");

            migrationBuilder.DropTable(
                name: "store_hours");

            migrationBuilder.DropColumn(
                name: "Bairro",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "CadastroAtualizadoEm",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "CadastroAtualizadoPor",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "Cep",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "CodigoIbge",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "FeriadoAbertura",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "FeriadoFechamento",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "FeriadoFuncionamento",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "Logradouro",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "Numero",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "Abertura",
                table: "holidays");

            migrationBuilder.DropColumn(
                name: "CodigoIbge",
                table: "holidays");

            migrationBuilder.DropColumn(
                name: "Fechamento",
                table: "holidays");

            migrationBuilder.DropColumn(
                name: "Funcionamento",
                table: "holidays");

            migrationBuilder.DropColumn(
                name: "PontoFacultativo",
                table: "holidays");

            migrationBuilder.DropColumn(
                name: "StoreId",
                table: "holidays");
        }
    }
}
