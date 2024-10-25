using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GithubActionsOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class addstuckreplacementflag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "StuckJobReplacement",
                table: "Runners",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StuckJobReplacement",
                table: "Runners");
        }
    }
}
