using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ManagerPlatform.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "ai_roles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    PromptTemplate = table.Column<string>(type: "text", nullable: false),
                    TtsConfig = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    AvatarUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ai_sessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConferenceId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AiRoleId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AgentInstance = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CustomPrompt = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "conferences",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RoomId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StartedByUserId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WaitingExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PendingCloseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conferences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "meeting_rooms",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    HostUserId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    HostNickname = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RoomName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxParticipants = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Locked = table.Column<bool>(type: "boolean", nullable: false),
                    InviteCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meeting_rooms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "participants",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConferenceId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UserId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Nickname = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    JoinTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaveTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsAi = table.Column<bool>(type: "boolean", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    AiSessionId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_participants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Account = table.Column<string>(type: "citext", maxLength: 128, nullable: false),
                    AccountKind = table.Column<int>(type: "integer", nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Nickname = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AvatarUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_roles_Name",
                table: "ai_roles",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ai_sessions_AiRoleId",
                table: "ai_sessions",
                column: "AiRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_sessions_ConferenceId",
                table: "ai_sessions",
                column: "ConferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_sessions_Status",
                table: "ai_sessions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "ix_conferences_active_per_room",
                table: "conferences",
                column: "RoomId",
                unique: true,
                filter: "\"Status\" IN (0, 1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_conferences_StartedByUserId",
                table: "conferences",
                column: "StartedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_conferences_Status",
                table: "conferences",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_meeting_rooms_HostUserId",
                table: "meeting_rooms",
                column: "HostUserId");

            migrationBuilder.CreateIndex(
                name: "IX_meeting_rooms_InviteCode",
                table: "meeting_rooms",
                column: "InviteCode",
                unique: true,
                filter: "\"InviteCode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_meeting_rooms_RoomName",
                table: "meeting_rooms",
                column: "RoomName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_meeting_rooms_StartTime",
                table: "meeting_rooms",
                column: "StartTime");

            migrationBuilder.CreateIndex(
                name: "IX_meeting_rooms_Status",
                table: "meeting_rooms",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_participants_AiSessionId",
                table: "participants",
                column: "AiSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_participants_ConferenceId",
                table: "participants",
                column: "ConferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_participants_ConferenceId_IsAi_LeaveTime",
                table: "participants",
                columns: new[] { "ConferenceId", "IsAi", "LeaveTime" });

            migrationBuilder.CreateIndex(
                name: "IX_participants_UserId",
                table: "participants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_users_Account",
                table: "users",
                column: "Account",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_Role",
                table: "users",
                column: "Role");

            migrationBuilder.CreateIndex(
                name: "IX_users_Status",
                table: "users",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_roles");

            migrationBuilder.DropTable(
                name: "ai_sessions");

            migrationBuilder.DropTable(
                name: "conferences");

            migrationBuilder.DropTable(
                name: "meeting_rooms");

            migrationBuilder.DropTable(
                name: "participants");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
