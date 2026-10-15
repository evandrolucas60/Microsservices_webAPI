using System;
using Intranet.Services.IdentidadeAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intranet.Services.IdentidadeAPI.Migrations
{
    /// <summary>
    /// Gestor direto do colaborador (auto-relacionamento em Members): base do organograma por setor
    /// e das futuras aprovações. Ao remover o gestor, os liderados ficam sem gestor (SET NULL).
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261015090000_GestorDireto")]
    public partial class GestorDireto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GestorMemberId", table: "Members", type: "uuid", nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Members_GestorMemberId", table: "Members", column: "GestorMemberId");

            migrationBuilder.AddForeignKey(
                name: "FK_Members_Members_GestorMemberId",
                table: "Members",
                column: "GestorMemberId",
                principalTable: "Members",
                principalColumn: "MemberId",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_Members_Members_GestorMemberId", table: "Members");
            migrationBuilder.DropIndex(name: "IX_Members_GestorMemberId", table: "Members");
            migrationBuilder.DropColumn(name: "GestorMemberId", table: "Members");
        }
    }
}
