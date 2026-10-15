using System;
using Intranet.Services.EscalaAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intranet.Services.EscalaAPI.Migrations
{
    /// <summary>Escala ligada ao colaborador da T.I (dados preenchidos a partir do cadastro dele).</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261014110000_EscalaMember")]
    public partial class EscalaMember : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MemberId", table: "escalas", type: "uuid", nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "MemberId", table: "escalas");
        }
    }
}
