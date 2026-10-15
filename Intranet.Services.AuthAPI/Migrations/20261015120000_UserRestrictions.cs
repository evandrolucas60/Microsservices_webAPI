using System;
using Intranet.Services.AuthAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intranet.Services.AuthAPI.Migrations
{
    /// <summary>Painel de gestão: bloqueio (motivo) e serviços restritos por usuário.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261015120000_UserRestrictions")]
    public partial class UserRestrictions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserRestrictions",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Servicos = table.Column<string[]>(type: "text[]", nullable: false),
                    MotivoRestricao = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    MotivoBloqueio = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    BloqueadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BloqueadoPorUserId = table.Column<string>(type: "text", nullable: true),
                    AtualizadoPorUserId = table.Column<string>(type: "text", nullable: true),
                    AtualizadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRestrictions", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_UserRestrictions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "UserRestrictions");
        }
    }
}
