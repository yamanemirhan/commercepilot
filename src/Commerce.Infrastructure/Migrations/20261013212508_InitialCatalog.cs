using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Commerce.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Stock = table.Column<int>(type: "integer", nullable: false),
                    Artwork = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Color = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Badge = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                    table.CheckConstraint("CK_Products_Price", "\"Price\" >= 0");
                    table.CheckConstraint("CK_Products_Stock", "\"Stock\" >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Products_Category_Price",
                table: "Products",
                columns: new[] { "Category", "Price" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_Slug",
                table: "Products",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Products");
        }
    }
}
