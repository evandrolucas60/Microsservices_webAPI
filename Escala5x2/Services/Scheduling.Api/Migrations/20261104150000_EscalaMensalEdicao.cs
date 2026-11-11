using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scheduling.Api.Migrations
{
    /// <summary>
    /// Escala por MÊS e edição de escala aprovada/publicada:
    /// <list type="bullet">
    /// <item>EndDate gravado (antes calculado como StartDate + Weeks × 7 − 1): as escalas novas vão do dia 1º ao último dia do
    /// mês (28, 29, 30 ou 31 dias). Backfill das existentes (por semanas) com a conta antiga — elas continuam abrindo com o
    /// mesmo período.</item>
    /// <item>schedule_edit_acks: edição salva mesmo com inconformidades novas ("Salvar assim mesmo"): quem, quando, status,
    /// percentual antes/depois e as inconformidades aceitas (jsonb).</item>
    /// </list>
    /// </summary>
    public partial class EscalaMensalEdicao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "EndDate",
                table: "schedules",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            // Escalas existentes (por semanas): fim = início + semanas × 7 − 1
            migrationBuilder.Sql("UPDATE schedules SET \"EndDate\" = \"StartDate\" + (GREATEST(\"Weeks\", 1) * 7 - 1);");

            migrationBuilder.CreateTable(
                name: "schedule_edit_acks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    By = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CellCount = table.Column<int>(type: "integer", nullable: false),
                    CellsRevision = table.Column<int>(type: "integer", nullable: false),
                    PercentualAntes = table.Column<double>(type: "double precision", nullable: true),
                    PercentualDepois = table.Column<double>(type: "double precision", nullable: true),
                    ViolacoesJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedule_edit_acks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_schedule_edit_acks_schedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_schedule_edit_acks_ScheduleId_At",
                table: "schedule_edit_acks",
                columns: new[] { "ScheduleId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "schedule_edit_acks");

            migrationBuilder.DropColumn(
                name: "EndDate",
                table: "schedules");
        }
    }
}
