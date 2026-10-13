using System;
using Intranet.Services.IdentidadeAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intranet.Services.IdentidadeAPI.Migrations
{
    /// <summary>
    /// Data de nascimento do membro (aniversariantes do mês), ramal no trabalho (página de Ramais)
    /// e data de admissão opcional (perfis criados automaticamente ainda não a possuem).
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261009150000_AniversariantesRamais")]
    public partial class AniversariantesRamais : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "BirthDate",
                table: "Members",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Ramal",
                table: "Works",
                type: "text",
                nullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "AdmissionDate",
                table: "Works",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BirthDate",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "Ramal",
                table: "Works");

            migrationBuilder.AlterColumn<DateTime>(
                name: "AdmissionDate",
                table: "Works",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc),
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
