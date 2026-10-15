using System;
using Intranet.Services.ComunicacaoAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intranet.Services.ComunicacaoAPI.Migrations
{
    /// <summary>Mensagens: respostas encadeadas à mensagem original e "registrar ciência".</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261015100000_MessageThreads")]
    public partial class MessageThreads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(name: "ThreadId", table: "messages", type: "integer", nullable: true);
            migrationBuilder.AddColumn<int>(name: "ParentMessageId", table: "messages", type: "integer", nullable: true);
            migrationBuilder.AddColumn<DateTime>(
                name: "AcknowledgedAt", table: "messageRecipients", type: "timestamp with time zone", nullable: true);

            migrationBuilder.CreateIndex(name: "IX_messages_ThreadId", table: "messages", column: "ThreadId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_messages_ThreadId", table: "messages");
            migrationBuilder.DropColumn(name: "ThreadId", table: "messages");
            migrationBuilder.DropColumn(name: "ParentMessageId", table: "messages");
            migrationBuilder.DropColumn(name: "AcknowledgedAt", table: "messageRecipients");
        }
    }
}
