using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ViewOwl.Data.Migrations
{
    /// <inheritdoc/>
    public partial class UniqueNamePerUser : Migration
    {
        /// <inheritdoc/>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Device: unique (UserId, Name) — a user cannot have two devices with the same name.
            migrationBuilder.CreateIndex(
                name: "IX_Devices_UserId_Name",
                table: "Devices",
                columns: ["UserId", "Name"],
                unique: true);

            // Template: unique (UserId, Name) — a user cannot have two templates with the same name.
            migrationBuilder.CreateIndex(
                name: "IX_Templates_UserId_Name",
                table: "Templates",
                columns: ["UserId", "Name"],
                unique: true);
        }

        /// <inheritdoc/>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Devices_UserId_Name",
                table: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_Templates_UserId_Name",
                table: "Templates");
        }
    }
}
