using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Commerce.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ChatConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActiveTurnId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActiveUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversations_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChatTurns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Input = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MessagesJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatTurns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatTurns_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChatCards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TurnId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToolCallId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatCards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatCards_ChatTurns_TurnId",
                        column: x => x.TurnId,
                        principalTable: "ChatTurns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChatEvents",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TurnId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DataJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatEvents", x => x.Sequence);
                    table.ForeignKey(
                        name: "FK_ChatEvents_ChatTurns_TurnId",
                        column: x => x.TurnId,
                        principalTable: "ChatTurns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatCards_ConversationId_ObservedAt",
                table: "ChatCards",
                columns: new[] { "ConversationId", "ObservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatCards_TurnId",
                table: "ChatCards",
                column: "TurnId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatEvents_ConversationId_Sequence",
                table: "ChatEvents",
                columns: new[] { "ConversationId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatEvents_TurnId",
                table: "ChatEvents",
                column: "TurnId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatTurns_ConversationId_CreatedAt",
                table: "ChatTurns",
                columns: new[] { "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_UserId_CreatedAt",
                table: "Conversations",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatCards");

            migrationBuilder.DropTable(
                name: "ChatEvents");

            migrationBuilder.DropTable(
                name: "ChatTurns");

            migrationBuilder.DropTable(
                name: "Conversations");
        }
    }
}
