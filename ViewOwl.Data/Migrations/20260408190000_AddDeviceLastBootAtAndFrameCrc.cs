using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ViewOwl.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceLastBootAtAndFrameCrc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastBootAt",
                table: "Devices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastFrameCrc",
                table: "Devices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FramesSentCount",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "LastBootAt",      table: "Devices");
            migrationBuilder.DropColumn(name: "LastFrameCrc",    table: "Devices");
            migrationBuilder.DropColumn(name: "FramesSentCount", table: "Devices");
        }
    }
}
