using Intranet.Services.ComunicacaoAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intranet.Services.ComunicacaoAPI.Migrations
{
    /// <summary>Chat: tipo da mensagem (texto/chamar atenção) e do anexo (imagem/áudio/documento).</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261014100000_ChatKinds")]
    public partial class ChatKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind", table: "chatMessages", type: "character varying(10)", maxLength: 10,
                nullable: false, defaultValue: "text");
            migrationBuilder.AddColumn<string>(
                name: "Kind", table: "chatAttachments", type: "character varying(10)", maxLength: 10,
                nullable: false, defaultValue: "image");

            // Limite de "chamar atenção" (última por remetente/conversa)
            migrationBuilder.CreateIndex(
                name: "IX_chatMessages_ConversationId_SenderMemberId_Kind",
                table: "chatMessages",
                columns: new[] { "ConversationId", "SenderMemberId", "Kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_chatMessages_ConversationId_SenderMemberId_Kind", table: "chatMessages");
            migrationBuilder.DropColumn(name: "Kind", table: "chatMessages");
            migrationBuilder.DropColumn(name: "Kind", table: "chatAttachments");
        }
    }
}
