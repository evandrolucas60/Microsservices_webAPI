using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Compliance.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "legal_rule_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    LegalBasis = table.Column<string>(type: "text", nullable: false),
                    Origin = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    WeeklyLimitMinutes = table.Column<int>(type: "integer", nullable: false),
                    DailyLimitMinutes = table.Column<int>(type: "integer", nullable: false),
                    DailyLimitWithAgreementMinutes = table.Column<int>(type: "integer", nullable: false),
                    MinRestDaysPerWeek = table.Column<int>(type: "integer", nullable: false),
                    MaxConsecutiveWorkDays = table.Column<int>(type: "integer", nullable: false),
                    MinInterShiftRestMinutes = table.Column<int>(type: "integer", nullable: false),
                    SundayWindowWeeks = table.Column<int>(type: "integer", nullable: false),
                    MaxOvertimeMinutesPerDay = table.Column<int>(type: "integer", nullable: false),
                    ApprovedBy = table.Column<string>(type: "text", nullable: false),
                    LegalOpinionRef = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legal_rule_versions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_legal_rule_versions_ValidFrom",
                table: "legal_rule_versions",
                column: "ValidFrom",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "legal_rule_versions");
        }
    }
}
