using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GithubActionsOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class jobadditions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestedProfile",
                table: "Jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestedSize",
                table: "Jobs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestedProfile",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "RequestedSize",
                table: "Jobs");
        }
    }
}
