using System;
using Intranet.Services.ConteudoAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Intranet.Services.ConteudoAPI.Migrations
{
    /// <summary>Denúncias de conteúdo (posts, artigos e comentários) para o painel de gestão.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261015130000_Denuncias")]
    public partial class Denuncias : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "denuncias",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Tipo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AlvoId = table.Column<int>(type: "integer", nullable: false),
                    AlvoAutorMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlvoResumo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Motivo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Detalhes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DenuncianteMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CriadaEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvidaPorUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ResolvidaEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Resolucao = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_denuncias", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_denuncias_Status_CriadaEm", table: "denuncias", columns: new[] { "Status", "CriadaEm" });

            migrationBuilder.CreateIndex(
                name: "IX_denuncias_Tipo_AlvoId", table: "denuncias", columns: new[] { "Tipo", "AlvoId" });

            // Uma denúncia pendente por colaborador por conteúdo
            migrationBuilder.CreateIndex(
                name: "IX_denuncias_DenuncianteMemberId_Tipo_AlvoId",
                table: "denuncias",
                columns: new[] { "DenuncianteMemberId", "Tipo", "AlvoId" },
                unique: true,
                filter: "\"Status\" = 'pendente'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "denuncias");
        }
    }
}
