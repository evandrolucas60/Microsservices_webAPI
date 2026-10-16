using System;
using Intranet.Services.GerenciamentoCategoriaAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Intranet.Services.GerenciamentoCategoriaAPI.Migrations
{
    /// <summary>
    /// Categorias do GC (nome único sem diferenciar maiúsculas/minúsculas, ordem da navegação) e
    /// os seus itens (fotos e documentos com título, descrição e seção opcional).
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261016120000_InitialCreate")]
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "categorias",
                columns: table => new
                {
                    CategoriaId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Nome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NomeNormalizado = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Descricao = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Ordem = table.Column<int>(type: "integer", nullable: false),
                    CriadoPor = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CriadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AtualizadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categorias", x => x.CategoriaId);
                });

            migrationBuilder.CreateTable(
                name: "categoriaItens",
                columns: table => new
                {
                    CategoriaItemId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CategoriaId = table.Column<int>(type: "integer", nullable: false),
                    Titulo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Descricao = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Secao = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Tipo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    NomeArquivo = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Tamanho = table.Column<long>(type: "bigint", nullable: false),
                    Conteudo = table.Column<byte[]>(type: "bytea", nullable: false),
                    EnviadoPor = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    EnviadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categoriaItens", x => x.CategoriaItemId);
                    table.ForeignKey(
                        name: "FK_categoriaItens_categorias_CategoriaId",
                        column: x => x.CategoriaId,
                        principalTable: "categorias",
                        principalColumn: "CategoriaId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_categorias_NomeNormalizado",
                table: "categorias",
                column: "NomeNormalizado",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_categorias_Ordem_Nome",
                table: "categorias",
                columns: new[] { "Ordem", "Nome" });

            migrationBuilder.CreateIndex(
                name: "IX_categoriaItens_CategoriaId_EnviadoEm",
                table: "categoriaItens",
                columns: new[] { "CategoriaId", "EnviadoEm" });

            migrationBuilder.CreateIndex(
                name: "IX_categoriaItens_EnviadoEm",
                table: "categoriaItens",
                column: "EnviadoEm");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "categoriaItens");
            migrationBuilder.DropTable(name: "categorias");
        }
    }
}
