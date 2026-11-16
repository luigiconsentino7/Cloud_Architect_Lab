using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lab.Modules.Shared.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Ia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NarrativeJson",
                table: "eva_evaluations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "conv_conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conv_conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "conv_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    PromptVersion = table.Column<string>(type: "TEXT", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conv_messages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conv_conversations_ChallengeId",
                table: "conv_conversations",
                column: "ChallengeId");

            migrationBuilder.CreateIndex(
                name: "IX_conv_conversations_Kind_UpdatedAt",
                table: "conv_conversations",
                columns: new[] { "Kind", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_conv_messages_ConversationId_Number",
                table: "conv_messages",
                columns: new[] { "ConversationId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conv_conversations");

            migrationBuilder.DropTable(
                name: "conv_messages");

            migrationBuilder.DropColumn(
                name: "NarrativeJson",
                table: "eva_evaluations");
        }
    }
}
