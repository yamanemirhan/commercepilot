using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commerce.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SiteKnowledge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector");
            migrationBuilder.CreateTable(
                name: "KnowledgeReleases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApplicationRevision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ModelIdentity = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChunkCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeReleases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Body = table.Column<string>(type: "character varying(1800)", maxLength: 1800, nullable: false),
                    SearchText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    LinksJson = table.Column<string>(type: "text", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Embedding = table.Column<float[]>(type: "real[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeChunks_KnowledgeReleases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "KnowledgeReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeIndexes",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ActiveReleaseId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeIndexes", x => x.Name);
                    table.ForeignKey(
                        name: "FK_KnowledgeIndexes_KnowledgeReleases_ActiveReleaseId",
                        column: x => x.ActiveReleaseId,
                        principalTable: "KnowledgeReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_ReleaseId_DocumentId_Ordinal",
                table: "KnowledgeChunks",
                columns: new[] { "ReleaseId", "DocumentId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeIndexes_ActiveReleaseId",
                table: "KnowledgeIndexes",
                column: "ActiveReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeReleases_ContentHash_ApplicationRevision_ModelIden~",
                table: "KnowledgeReleases",
                columns: new[] { "ContentHash", "ApplicationRevision", "ModelIdentity" },
                unique: true);
            migrationBuilder.Sql("""
                ALTER TABLE "KnowledgeChunks" ADD COLUMN "EmbeddingVector" vector(768)
                  GENERATED ALWAYS AS ("Embedding"::vector(768)) STORED;
                ALTER TABLE "KnowledgeChunks" ADD CONSTRAINT "CK_KnowledgeChunks_Embedding"
                  CHECK (array_length("Embedding", 1) = 768 AND vector_norm("EmbeddingVector") > 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnowledgeChunks");

            migrationBuilder.DropTable(
                name: "KnowledgeIndexes");

            migrationBuilder.DropTable(
                name: "KnowledgeReleases");
        }
    }
}
