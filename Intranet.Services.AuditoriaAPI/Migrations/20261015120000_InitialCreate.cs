using System;
using Intranet.Services.AuditoriaAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Intranet.Services.AuditoriaAPI.Migrations
{
    /// <summary>Tabelas de auditoria: erros (5xx) e acessos (login).</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261015120000_InitialCreate")]
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "acessos",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Sucesso = table.Column<bool>(type: "boolean", nullable: false),
                    Motivo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_acessos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "erros",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Servico = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Metodo = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Caminho = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Mensagem = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Excecao = table.Column<string>(type: "text", nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_erros", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_acessos_Em",
                table: "acessos",
                column: "Em");

            migrationBuilder.CreateIndex(
                name: "IX_acessos_UserId_Em",
                table: "acessos",
                columns: new[] { "UserId", "Em" });

            migrationBuilder.CreateIndex(
                name: "IX_erros_Em",
                table: "erros",
                column: "Em");

            migrationBuilder.CreateIndex(
                name: "IX_erros_Servico_Em",
                table: "erros",
                columns: new[] { "Servico", "Em" });

            migrationBuilder.CreateIndex(
                name: "IX_erros_UserId_Em",
                table: "erros",
                columns: new[] { "UserId", "Em" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "acessos");
            migrationBuilder.DropTable(name: "erros");
        }
    }
}
