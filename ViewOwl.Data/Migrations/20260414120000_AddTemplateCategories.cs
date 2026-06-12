using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ViewOwl.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop residual table from any previous failed migration attempt.
            migrationBuilder.Sql("DROP TABLE IF EXISTS TemplateCategories;");
            migrationBuilder.Sql("DROP INDEX  IF EXISTS IX_TemplateCategories_Slug;");

            migrationBuilder.CreateTable(
                name: "TemplateCategories",
                columns: table => new
                {
                    Id        = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Label     = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Slug      = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IsSystem  = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateCategories", x => x.Id);
                });

            // Slug must be globally unique — used as data-vow-category attribute value in templates.
            migrationBuilder.CreateIndex(
                name: "IX_TemplateCategories_Slug",
                table: "TemplateCategories",
                column: "Slug",
                unique: true);

            // Seed the eight standard categories that match VOW_CATEGORIES in the frontend.
            // Raw SQL avoids EF InsertData boxing issues with bool/DateTime on different runtimes.
            // IsSystem=1 on "other" — it is the fallback and cannot be deleted.
            migrationBuilder.Sql(@"
INSERT INTO TemplateCategories (Label, Slug, IsSystem, CreatedAt) VALUES
  ('Weather', 'weather', 0, '2026-04-14T12:00:00.0000000'),
  ('Clock',   'clock',   0, '2026-04-14T12:00:00.0000000'),
  ('Rates',   'rates',   0, '2026-04-14T12:00:00.0000000'),
  ('Menu',    'menu',    0, '2026-04-14T12:00:00.0000000'),
  ('System',  'system',  0, '2026-04-14T12:00:00.0000000'),
  ('Sci-Fi',  'scifi',   0, '2026-04-14T12:00:00.0000000'),
  ('Ambient', 'ambient', 0, '2026-04-14T12:00:00.0000000'),
  ('Other',   'other',   1, '2026-04-14T12:00:00.0000000');
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "TemplateCategories");
        }
    }
}
