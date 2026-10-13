using System;
using Intranet.Services.ConteudoAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intranet.Services.ConteudoAPI.Migrations
{
    /// <summary>Compartilhamento de posts (repost na própria timeline).</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261014090000_PostShares")]
    public partial class PostShares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SharedFromPostId", table: "posts", type: "integer", nullable: true);
            migrationBuilder.AddColumn<Guid>(
                name: "SharedFromMemberId", table: "posts", type: "uuid", nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_posts_SharedFromPostId", table: "posts", column: "SharedFromPostId");

            migrationBuilder.AddForeignKey(
                name: "FK_posts_posts_SharedFromPostId",
                table: "posts",
                column: "SharedFromPostId",
                principalTable: "posts",
                principalColumn: "PostId",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_posts_posts_SharedFromPostId", table: "posts");
            migrationBuilder.DropIndex(name: "IX_posts_SharedFromPostId", table: "posts");
            migrationBuilder.DropColumn(name: "SharedFromPostId", table: "posts");
            migrationBuilder.DropColumn(name: "SharedFromMemberId", table: "posts");
        }
    }
}
