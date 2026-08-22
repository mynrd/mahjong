using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mahjong.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Rooms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nchar(6)", fixedLength: true, maxLength: 6, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    PasswordHash = table.Column<byte[]>(type: "varbinary(64)", maxLength: 64, nullable: false),
                    PasswordSalt = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    PasswordIterations = table.Column<int>(type: "int", nullable: false),
                    RulesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    HostPlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HandsPlayed = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rooms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Games",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HandNumber = table.Column<int>(type: "int", nullable: false),
                    ManoSeat = table.Column<int>(type: "int", nullable: false),
                    JokerTile = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    Seed = table.Column<int>(type: "int", nullable: false),
                    StateJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Games", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Games_Rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "Rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Players",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Seat = table.Column<int>(type: "int", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    IsBot = table.Column<bool>(type: "bit", nullable: false),
                    IsConnected = table.Column<bool>(type: "bit", nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Balance = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Players", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Players_Rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "Rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GameActions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Seq = table.Column<int>(type: "int", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Seat = table.Column<int>(type: "int", nullable: false),
                    ActionType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameActions_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HandResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WinnerPlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WinnerSeat = table.Column<int>(type: "int", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    BreakdownJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TotalUnits = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HandResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HandResults_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Settlements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    HandResultId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Seat = table.Column<int>(type: "int", nullable: false),
                    Delta = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Settlements_HandResults_HandResultId",
                        column: x => x.HandResultId,
                        principalTable: "HandResults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameActions_GameId_Seq",
                table: "GameActions",
                columns: new[] { "GameId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Games_RoomId_HandNumber",
                table: "Games",
                columns: new[] { "RoomId", "HandNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HandResults_GameId",
                table: "HandResults",
                column: "GameId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Players_RoomId_Seat",
                table: "Players",
                columns: new[] { "RoomId", "Seat" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Players_TokenHash",
                table: "Players",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_Rooms_Code",
                table: "Rooms",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Settlements_GameId",
                table: "Settlements",
                column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_Settlements_HandResultId",
                table: "Settlements",
                column: "HandResultId");

            migrationBuilder.CreateIndex(
                name: "IX_Settlements_PlayerId",
                table: "Settlements",
                column: "PlayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameActions");

            migrationBuilder.DropTable(
                name: "Players");

            migrationBuilder.DropTable(
                name: "Settlements");

            migrationBuilder.DropTable(
                name: "HandResults");

            migrationBuilder.DropTable(
                name: "Games");

            migrationBuilder.DropTable(
                name: "Rooms");
        }
    }
}
