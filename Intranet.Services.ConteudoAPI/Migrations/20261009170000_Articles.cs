using System;
using Intranet.Services.ConteudoAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Intranet.Services.ConteudoAPI.Migrations
{
    /// <summary>Seção Artigos: artigos/vídeos/informativos, comentários e curtidas.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261009170000_Articles")]
    public partial class Articles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "articles",
                columns: table => new
                {
                    ArticleId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Content = table.Column<string>(type: "text", nullable: true),
                    VideoUrl = table.Column<string>(type: "text", nullable: true),
                    CoverImageUrl = table.Column<string>(type: "text", nullable: true),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorUserId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NewsletterQueuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_articles", x => x.ArticleId);
                });

            migrationBuilder.CreateTable(
                name: "articleComments",
                columns: table => new
                {
                    ArticleCommentId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ArticleId = table.Column<int>(type: "integer", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_articleComments", x => x.ArticleCommentId);
                    table.ForeignKey(
                        name: "FK_articleComments_articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "articles",
                        principalColumn: "ArticleId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "articleLikes",
                columns: table => new
                {
                    ArticleLikeId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ArticleId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_articleLikes", x => x.ArticleLikeId);
                    table.ForeignKey(
                        name: "FK_articleLikes_articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "articles",
                        principalColumn: "ArticleId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_articles_CreatedAt",
                table: "articles",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_articles_Type",
                table: "articles",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_articleComments_ArticleId",
                table: "articleComments",
                column: "ArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_articleLikes_ArticleId_UserId",
                table: "articleLikes",
                columns: new[] { "ArticleId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "articleComments");
            migrationBuilder.DropTable(name: "articleLikes");
            migrationBuilder.DropTable(name: "articles");
        }
    }
}
