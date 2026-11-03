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

            // 存量表已有数据，直接加 NOT NULL 列会失败（23502），
            // 因此先加可空列 → 回填 → 再置为 NOT NULL。
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "SearchVector",
                table: "kb_chunks",
                type: "tsvector",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE kb_chunks SET \"SearchVector\" = to_tsvector('simple', coalesce(\"Content\", '')) WHERE \"SearchVector\" IS NULL;");

            migrationBuilder.AlterColumn<NpgsqlTsVector>(
                name: "SearchVector",
                table: "kb_chunks",
                type: "tsvector",
                nullable: false,
                oldClrType: typeof(NpgsqlTsVector),
                oldType: "tsvector",
                oldNullable: true);

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
