using System;
using Intranet.Services.ComunidadesAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Intranet.Services.ComunidadesAPI.Migrations
{
    /// <summary>
    /// Grupos privados: solicitações de acesso, papel "admin", capa e publicações
    /// (com imagem, curtidas e comentários). Imagens ficam no banco (bytea), em tabelas
    /// separadas para não pesar as listagens.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261013100000_GroupPrivacyPostsImages")]
    public partial class GroupPrivacyPostsImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Papéis: "owner" continua dono; qualquer valor desconhecido vira "member"
            migrationBuilder.Sql(
                "UPDATE \"groupMembers\" SET \"Role\" = 'member' WHERE \"Role\" NOT IN ('owner', 'admin', 'member');");

            migrationBuilder.AddColumn<DateTime>(
                name: "CoverUpdatedAt",
                table: "groups",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "groupCovers",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groupCovers", x => x.GroupId);
                    table.ForeignKey(
                        name: "FK_groupCovers_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "groups",
                        principalColumn: "GroupId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "groupJoinRequests",
                columns: table => new
                {
                    GroupJoinRequestId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecidedByMemberId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groupJoinRequests", x => x.GroupJoinRequestId);
                    table.ForeignKey(
                        name: "FK_groupJoinRequests_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "groups",
                        principalColumn: "GroupId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "groupPosts",
                columns: table => new
                {
                    GroupPostId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groupPosts", x => x.GroupPostId);
                    table.ForeignKey(
                        name: "FK_groupPosts_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "groups",
                        principalColumn: "GroupId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "groupPostImages",
                columns: table => new
                {
                    GroupPostId = table.Column<int>(type: "integer", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groupPostImages", x => x.GroupPostId);
                    table.ForeignKey(
                        name: "FK_groupPostImages_groupPosts_GroupPostId",
                        column: x => x.GroupPostId,
                        principalTable: "groupPosts",
                        principalColumn: "GroupPostId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "groupPostLikes",
                columns: table => new
                {
                    GroupPostLikeId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GroupPostId = table.Column<int>(type: "integer", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groupPostLikes", x => x.GroupPostLikeId);
                    table.ForeignKey(
                        name: "FK_groupPostLikes_groupPosts_GroupPostId",
                        column: x => x.GroupPostId,
                        principalTable: "groupPosts",
                        principalColumn: "GroupPostId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "groupPostComments",
                columns: table => new
                {
                    GroupPostCommentId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GroupPostId = table.Column<int>(type: "integer", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groupPostComments", x => x.GroupPostCommentId);
                    table.ForeignKey(
                        name: "FK_groupPostComments_groupPosts_GroupPostId",
                        column: x => x.GroupPostId,
                        principalTable: "groupPosts",
                        principalColumn: "GroupPostId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_groupJoinRequests_GroupId_MemberId",
                table: "groupJoinRequests",
                columns: new[] { "GroupId", "MemberId" },
                unique: true,
                filter: "\"Status\" = 'pending'");

            migrationBuilder.CreateIndex(
                name: "IX_groupJoinRequests_GroupId_Status_CreatedAt",
                table: "groupJoinRequests",
                columns: new[] { "GroupId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_groupPosts_GroupId_CreatedAt",
                table: "groupPosts",
                columns: new[] { "GroupId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_groupPostLikes_GroupPostId_MemberId",
                table: "groupPostLikes",
                columns: new[] { "GroupPostId", "MemberId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_groupPostComments_GroupPostId_CreatedAt",
                table: "groupPostComments",
                columns: new[] { "GroupPostId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "groupPostComments");
            migrationBuilder.DropTable(name: "groupPostLikes");
            migrationBuilder.DropTable(name: "groupPostImages");
            migrationBuilder.DropTable(name: "groupPosts");
            migrationBuilder.DropTable(name: "groupJoinRequests");
            migrationBuilder.DropTable(name: "groupCovers");

            migrationBuilder.DropColumn(name: "CoverUpdatedAt", table: "groups");

            // Admins voltam a ser membros comuns (o papel não existia antes)
            migrationBuilder.Sql("UPDATE \"groupMembers\" SET \"Role\" = 'member' WHERE \"Role\" = 'admin';");
        }
    }
}
