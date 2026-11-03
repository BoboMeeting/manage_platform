using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace ManagerPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddHybridSearchColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "kb_chunks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "SearchVector",
                table: "kb_chunks",
                type: "tsvector",
                nullable: false);

            migrationBuilder.CreateIndex(
                name: "ix_kb_chunks_search_vector",
                table: "kb_chunks",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_kb_chunks_search_vector",
                table: "kb_chunks");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "kb_chunks");

            migrationBuilder.DropColumn(
                name: "SearchVector",
                table: "kb_chunks");
        }
    }
}
