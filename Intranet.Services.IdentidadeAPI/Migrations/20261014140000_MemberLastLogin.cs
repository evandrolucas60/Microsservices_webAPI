using System;
using Intranet.Services.IdentidadeAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intranet.Services.IdentidadeAPI.Migrations
{
    /// <summary>Último login do colaborador (painel "Most Recent Signin" da home).</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261014140000_MemberLastLogin")]
    public partial class MemberLastLogin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastLoginAt", table: "Members", type: "timestamp with time zone", nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Members_LastLoginAt", table: "Members", column: "LastLoginAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Members_LastLoginAt", table: "Members");
            migrationBuilder.DropColumn(name: "LastLoginAt", table: "Members");
        }
    }
}
