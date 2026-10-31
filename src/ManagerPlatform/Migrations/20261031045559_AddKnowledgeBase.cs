using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace ManagerPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeBase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "knowledge_bases",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    EmbeddingModel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EmbeddingDim = table.Column<int>(type: "integer", nullable: false),
                    ChunkSize = table.Column<int>(type: "integer", nullable: false, defaultValue: 500),
                    ChunkOverlap = table.Column<int>(type: "integer", nullable: false, defaultValue: 80),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_bases", x => x.Id);
                    table.CheckConstraint("ck_knowledge_bases_overlap", "\"ChunkOverlap\" >= 0 AND \"ChunkOverlap\" < \"ChunkSize\"");
                });

            migrationBuilder.CreateTable(
                name: "retrieval_logs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    KnowledgeBaseId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Query = table.Column<string>(type: "text", nullable: false),
                    TopK = table.Column<int>(type: "integer", nullable: false),
                    HitCount = table.Column<int>(type: "integer", nullable: false),
                    TopScore = table.Column<float>(type: "real", nullable: true),
                    ElapsedMs = table.Column<int>(type: "integer", nullable: false),
                    Caller = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retrieval_logs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "kb_documents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    KnowledgeBaseId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ChunkCount = table.Column<int>(type: "integer", nullable: false),
                    IndexedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kb_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_kb_documents_knowledge_bases_KnowledgeBaseId",
                        column: x => x.KnowledgeBaseId,
                        principalTable: "knowledge_bases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "kb_chunks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    KnowledgeBaseId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DocumentId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Seq = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    CharCount = table.Column<int>(type: "integer", nullable: false),
                    HeadingPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PageNo = table.Column<int>(type: "integer", nullable: true),
                    Embedding = table.Column<Vector>(type: "vector(1024)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kb_chunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_kb_chunks_kb_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "kb_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "kb_ingest_events",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DocumentId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kb_ingest_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_kb_ingest_events_kb_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "kb_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_kb_chunks_DocumentId_Seq",
                table: "kb_chunks",
                columns: new[] { "DocumentId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_kb_chunks_embedding",
                table: "kb_chunks",
                column: "Embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_kb_chunks_KnowledgeBaseId",
                table: "kb_chunks",
                column: "KnowledgeBaseId");

            migrationBuilder.CreateIndex(
                name: "ix_kb_documents_checksum",
                table: "kb_documents",
                columns: new[] { "KnowledgeBaseId", "Checksum" },
                unique: true,
                filter: "\"Checksum\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_kb_documents_KnowledgeBaseId_Status",
                table: "kb_documents",
                columns: new[] { "KnowledgeBaseId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_kb_ingest_events_DocumentId_CreatedAt",
                table: "kb_ingest_events",
                columns: new[] { "DocumentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_bases_Name",
                table: "knowledge_bases",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_logs_CreatedAt",
                table: "retrieval_logs",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kb_chunks");

            migrationBuilder.DropTable(
                name: "kb_ingest_events");

            migrationBuilder.DropTable(
                name: "retrieval_logs");

            migrationBuilder.DropTable(
                name: "kb_documents");

            migrationBuilder.DropTable(
                name: "knowledge_bases");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
