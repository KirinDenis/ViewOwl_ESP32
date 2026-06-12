using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ViewOwl.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPipelineEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PipelineEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Ts = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: true),
                    TemplateId = table.Column<int>(type: "INTEGER", nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: true),
                    Payload = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PipelineEvents_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PipelineEvents_Templates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "Templates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            // Index: recent events for a specific device
            migrationBuilder.CreateIndex(
                name: "IX_PipelineEvents_DeviceId_Ts",
                table: "PipelineEvents",
                columns: new[] { "DeviceId", "Ts" });

            // Index: recent events for a specific template
            migrationBuilder.CreateIndex(
                name: "IX_PipelineEvents_TemplateId_Ts",
                table: "PipelineEvents",
                columns: new[] { "TemplateId", "Ts" });

            // Index: all events of a given type, newest first
            migrationBuilder.CreateIndex(
                name: "IX_PipelineEvents_EventType_Ts",
                table: "PipelineEvents",
                columns: new[] { "EventType", "Ts" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PipelineEvents");
        }
    }
}
