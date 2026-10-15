using System;
using Intranet.Services.DocumentoAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Intranet.Services.DocumentoAPI.Migrations
{
    /// <summary>
    /// Lojas do GC (nome único sem diferenciar maiúsculas/minúsculas) e os arquivos
    /// (fotos e documentos) das seções fixas de cada loja.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261014120000_Lojas")]
    public partial class Lojas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lojas",
                columns: table => new
                {
                    LojaId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Nome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NomeNormalizado = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CriadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AtualizadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CriadoPor = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lojas", x => x.LojaId);
                });

            migrationBuilder.CreateTable(
                name: "lojaArquivos",
                columns: table => new
                {
                    LojaArquivoId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LojaId = table.Column<int>(type: "integer", nullable: false),
                    Secao = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Tipo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    NomeArquivo = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Tamanho = table.Column<long>(type: "bigint", nullable: false),
                    Conteudo = table.Column<byte[]>(type: "bytea", nullable: false),
                    Descricao = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    EnviadoPor = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CriadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lojaArquivos", x => x.LojaArquivoId);
                    table.ForeignKey(
                        name: "FK_lojaArquivos_lojas_LojaId",
                        column: x => x.LojaId,
                        principalTable: "lojas",
                        principalColumn: "LojaId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lojas_NomeNormalizado",
                table: "lojas",
                column: "NomeNormalizado",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lojaArquivos_LojaId_Secao",
                table: "lojaArquivos",
                columns: new[] { "LojaId", "Secao" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "lojaArquivos");
            migrationBuilder.DropTable(name: "lojas");
        }
    }
}
