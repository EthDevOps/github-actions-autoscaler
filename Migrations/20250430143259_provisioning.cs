using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GithubActionsOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class provisioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProvisionId",
                table: "Runners",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProvisionPayload",
                table: "Runners",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProvisionId",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "ProvisionPayload",
                table: "Runners");
        }
    }
}
