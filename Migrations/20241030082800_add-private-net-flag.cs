using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GithubActionsOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class addprivatenetflag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UsePrivateNetwork",
                table: "Runners",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UsePrivateNetwork",
                table: "Runners");
        }
    }
}
