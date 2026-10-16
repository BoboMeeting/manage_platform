using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ManagerPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoResources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "subtitle_tracks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VideoResourceId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Language = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CueCount = table.Column<int>(type: "integer", nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subtitle_tracks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "video_resources",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    OriginalFileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    StorageKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    HlsMasterKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DeepMeaningKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ProcessStatus = table.Column<int>(type: "integer", nullable: false),
                    ShelfStatus = table.Column<int>(type: "integer", nullable: false),
                    FailReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    FailedStage = table.Column<int>(type: "integer", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_video_resources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "video_status_events",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VideoResourceId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Stage = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_video_status_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_subtitle_tracks_default_per_video",
                table: "subtitle_tracks",
                columns: new[] { "VideoResourceId", "IsDefault" },
                unique: true,
                filter: "\"IsDefault\" IS TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_subtitle_tracks_VideoResourceId",
                table: "subtitle_tracks",
                column: "VideoResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_video_resources_CreatedAt",
                table: "video_resources",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_video_resources_ProcessStatus",
                table: "video_resources",
                column: "ProcessStatus");

            migrationBuilder.CreateIndex(
                name: "IX_video_resources_ShelfStatus",
                table: "video_resources",
                column: "ShelfStatus");

            migrationBuilder.CreateIndex(
                name: "IX_video_status_events_VideoResourceId",
                table: "video_status_events",
                column: "VideoResourceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "subtitle_tracks");

            migrationBuilder.DropTable(
                name: "video_resources");

            migrationBuilder.DropTable(
                name: "video_status_events");
        }
    }
}
