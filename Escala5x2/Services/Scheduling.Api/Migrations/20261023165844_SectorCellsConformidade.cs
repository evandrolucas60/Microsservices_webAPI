using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scheduling.Api.Migrations
{
    /// <inheritdoc />
    public partial class SectorCellsConformidade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AvaliadaEm",
                table: "schedules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CellsRevision",
                table: "schedules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ConformidadeJson",
                table: "schedules",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ConformidadePercentual",
                table: "schedules",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GenerationMode",
                table: "schedules",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SectorId",
                table: "schedules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SectorName",
                table: "schedules",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AlterColumn<TimeOnly>(
                name: "Start",
                table: "schedule_assignments",
                type: "time without time zone",
                nullable: true,
                oldClrType: typeof(TimeOnly),
                oldType: "time without time zone");

            migrationBuilder.AlterColumn<Guid>(
                name: "ShiftId",
                table: "schedule_assignments",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<TimeOnly>(
                name: "End",
                table: "schedule_assignments",
                type: "time without time zone",
                nullable: true,
                oldClrType: typeof(TimeOnly),
                oldType: "time without time zone");

            migrationBuilder.AddColumn<bool>(
                name: "Folga",
                table: "schedule_assignments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Origem",
                table: "schedule_assignments",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Motor");

            migrationBuilder.CreateTable(
                name: "schedule_suggestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CellsRevision = table.Column<int>(type: "integer", nullable: false),
                    PercentualAtual = table.Column<double>(type: "double precision", nullable: true),
                    PercentualPrevisto = table.Column<double>(type: "double precision", nullable: true),
                    AlteracoesJson = table.Column<string>(type: "jsonb", nullable: true),
                    Mensagem = table.Column<string>(type: "text", nullable: true),
                    RequestedBy = table.Column<string>(type: "text", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AppliedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedule_suggestions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_schedules_StoreId_SectorId",
                table: "schedules",
                columns: new[] { "StoreId", "SectorId" });

            migrationBuilder.CreateIndex(
                name: "IX_schedule_suggestions_ScheduleId",
                table: "schedule_suggestions",
                column: "ScheduleId");

            // Escalas antigas (geradas por loja) ficam como "Todos os setores", somente leitura; as células existentes são do motor
            migrationBuilder.Sql("UPDATE schedules SET \"SectorName\" = 'Todos os setores' WHERE \"SectorId\" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "schedule_suggestions");

            migrationBuilder.DropIndex(
                name: "IX_schedules_StoreId_SectorId",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "AvaliadaEm",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "CellsRevision",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "ConformidadeJson",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "ConformidadePercentual",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "GenerationMode",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "SectorId",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "SectorName",
                table: "schedules");

            // Folgas marcadas não existiam (linhas sem turno)
            migrationBuilder.Sql("DELETE FROM schedule_assignments WHERE \"Folga\" OR \"ShiftId\" IS NULL;");

            migrationBuilder.DropColumn(
                name: "Folga",
                table: "schedule_assignments");

            migrationBuilder.DropColumn(
                name: "Origem",
                table: "schedule_assignments");

            migrationBuilder.AlterColumn<TimeOnly>(
                name: "Start",
                table: "schedule_assignments",
                type: "time without time zone",
                nullable: false,
                defaultValue: new TimeOnly(0, 0, 0),
                oldClrType: typeof(TimeOnly),
                oldType: "time without time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ShiftId",
                table: "schedule_assignments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<TimeOnly>(
                name: "End",
                table: "schedule_assignments",
                type: "time without time zone",
                nullable: false,
                defaultValue: new TimeOnly(0, 0, 0),
                oldClrType: typeof(TimeOnly),
                oldType: "time without time zone",
                oldNullable: true);
        }
    }
}
