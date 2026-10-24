using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GithubActionsOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class AddPausedClouds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PausedClouds",
                columns: table => new
                {
                    Cloud = table.Column<string>(type: "text", nullable: false),
                    PausedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PausedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PausedClouds", x => x.Cloud);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PausedClouds");
        }
    }
}
