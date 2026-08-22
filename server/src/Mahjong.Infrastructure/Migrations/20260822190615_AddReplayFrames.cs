using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReplayFrames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GameFrames",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AfterSeq = table.Column<int>(type: "int", nullable: false),
                    StateJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameFrames", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameFrames_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReplayTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplayTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReplayTokens_Rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "Rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameFrames_GameId_AfterSeq",
                table: "GameFrames",
                columns: new[] { "GameId", "AfterSeq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReplayTokens_RoomId",
                table: "ReplayTokens",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_ReplayTokens_TokenHash",
                table: "ReplayTokens",
                column: "TokenHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameFrames");

            migrationBuilder.DropTable(
                name: "ReplayTokens");
        }
    }
}
