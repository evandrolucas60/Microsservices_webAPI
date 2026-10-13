using System;
using Intranet.Services.ComunicacaoAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Intranet.Services.ComunicacaoAPI.Migrations
{
    /// <summary>Mensagens, destinatários e anexos.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261010110000_InitialCreate")]
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    MessageId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SenderMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderUserId = table.Column<string>(type: "text", nullable: false),
                    MessageBody = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "messageMedia",
                columns: table => new
                {
                    MessageMediaId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MessageId = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messageMedia", x => x.MessageMediaId);
                    table.ForeignKey(
                        name: "FK_messageMedia_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "messages",
                        principalColumn: "MessageId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "messageRecipients",
                columns: table => new
                {
                    MessageRecipientId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MessageId = table.Column<int>(type: "integer", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messageRecipients", x => x.MessageRecipientId);
                    table.ForeignKey(
                        name: "FK_messageRecipients_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "messages",
                        principalColumn: "MessageId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_messages_SenderMemberId_CreatedAt",
                table: "messages",
                columns: new[] { "SenderMemberId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_messageMedia_MessageId",
                table: "messageMedia",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_messageRecipients_MemberId_ReadAt",
                table: "messageRecipients",
                columns: new[] { "MemberId", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "IX_messageRecipients_MessageId_MemberId",
                table: "messageRecipients",
                columns: new[] { "MessageId", "MemberId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "messageMedia");
            migrationBuilder.DropTable(name: "messageRecipients");
            migrationBuilder.DropTable(name: "messages");
        }
    }
}
