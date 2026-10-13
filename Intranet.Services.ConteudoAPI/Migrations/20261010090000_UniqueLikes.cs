using Intranet.Services.ConteudoAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intranet.Services.ConteudoAPI.Migrations
{
    /// <summary>
    /// Uma curtida por colaborador em cada post/comentário. Remove as duplicadas
    /// existentes (mantém a mais antiga) antes de criar os índices únicos.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261010090000_UniqueLikes")]
    public partial class UniqueLikes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DELETE FROM ""postLikes"" a
USING ""postLikes"" b
WHERE a.""PostId"" = b.""PostId"" AND a.""MemberId"" = b.""MemberId"" AND a.""PostLikeId"" > b.""PostLikeId"";

DELETE FROM ""commentLikes"" a
USING ""commentLikes"" b
WHERE a.""CommentId"" = b.""CommentId"" AND a.""MemberId"" = b.""MemberId"" AND a.""CommentLikeId"" > b.""CommentLikeId"";
");

            migrationBuilder.DropIndex(name: "IX_postLikes_PostId", table: "postLikes");
            migrationBuilder.DropIndex(name: "IX_commentLikes_CommentId", table: "commentLikes");

            migrationBuilder.CreateIndex(
                name: "IX_postLikes_PostId_MemberId",
                table: "postLikes",
                columns: new[] { "PostId", "MemberId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commentLikes_CommentId_MemberId",
                table: "commentLikes",
                columns: new[] { "CommentId", "MemberId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_postLikes_PostId_MemberId", table: "postLikes");
            migrationBuilder.DropIndex(name: "IX_commentLikes_CommentId_MemberId", table: "commentLikes");

            migrationBuilder.CreateIndex(name: "IX_postLikes_PostId", table: "postLikes", column: "PostId");
            migrationBuilder.CreateIndex(name: "IX_commentLikes_CommentId", table: "commentLikes", column: "CommentId");
        }
    }
}
