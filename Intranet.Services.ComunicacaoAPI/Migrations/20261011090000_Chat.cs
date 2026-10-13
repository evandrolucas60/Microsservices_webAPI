using System;
using Intranet.Services.ComunicacaoAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Intranet.Services.ComunicacaoAPI.Migrations
{
    /// <summary>Chat: conversas privadas entre dois colaboradores e suas mensagens.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261011090000_Chat")]
    public partial class Chat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conversations",
                columns: table => new
                {
                    ConversationId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MemberAId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberBId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.ConversationId);
                });

            migrationBuilder.CreateTable(
                name: "chatMessages",
                columns: table => new
                {
                    ChatMessageId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConversationId = table.Column<int>(type: "integer", nullable: false),
                    SenderMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chatMessages", x => x.ChatMessageId);
                    table.ForeignKey(
                        name: "FK_chatMessages_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "ConversationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_MemberAId_MemberBId",
                table: "conversations",
                columns: new[] { "MemberAId", "MemberBId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_conversations_MemberBId_LastMessageAt",
                table: "conversations",
                columns: new[] { "MemberBId", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_chatMessages_ConversationId_ChatMessageId",
                table: "chatMessages",
                columns: new[] { "ConversationId", "ChatMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_chatMessages_ConversationId_ReadAt",
                table: "chatMessages",
                columns: new[] { "ConversationId", "ReadAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "chatMessages");
            migrationBuilder.DropTable(name: "conversations");
        }
    }
}
