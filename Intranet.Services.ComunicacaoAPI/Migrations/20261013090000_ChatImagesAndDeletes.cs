using System;
using Intranet.Services.ComunicacaoAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Intranet.Services.ComunicacaoAPI.Migrations
{
    /// <summary>
    /// Chat: imagens, mensagem apagada e "limpar conversa" por participante.
    /// Caixa de mensagens: exclusão pelo remetente/destinatário.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261013090000_ChatImagesAndDeletes")]
    public partial class ChatImagesAndDeletes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MemberAClearedAt", table: "conversations", type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<DateTime>(
                name: "MemberBClearedAt", table: "conversations", type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt", table: "chatMessages", type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<DateTime>(
                name: "SenderDeletedAt", table: "messages", type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt", table: "messageRecipients", type: "timestamp with time zone", nullable: true);

            migrationBuilder.CreateTable(
                name: "chatAttachments",
                columns: table => new
                {
                    ChatAttachmentId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChatMessageId = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chatAttachments", x => x.ChatAttachmentId);
                    table.ForeignKey(
                        name: "FK_chatAttachments_chatMessages_ChatMessageId",
                        column: x => x.ChatMessageId,
                        principalTable: "chatMessages",
                        principalColumn: "ChatMessageId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chatAttachments_ChatMessageId",
                table: "chatAttachments",
                column: "ChatMessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "chatAttachments");
            migrationBuilder.DropColumn(name: "MemberAClearedAt", table: "conversations");
            migrationBuilder.DropColumn(name: "MemberBClearedAt", table: "conversations");
            migrationBuilder.DropColumn(name: "DeletedAt", table: "chatMessages");
            migrationBuilder.DropColumn(name: "SenderDeletedAt", table: "messages");
            migrationBuilder.DropColumn(name: "DeletedAt", table: "messageRecipients");
        }
    }
}
