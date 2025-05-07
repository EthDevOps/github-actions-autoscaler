using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GithubActionsOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class fixjobrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Runners_Jobs_JobId1",
                table: "Runners");

            migrationBuilder.DropIndex(
                name: "IX_Runners_JobId1",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "JobId1",
                table: "Runners");

            migrationBuilder.AddColumn<int>(
                name: "RunnerId",
                table: "Jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_RunnerId",
                table: "Jobs",
                column: "RunnerId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_Runners_RunnerId",
                table: "Jobs",
                column: "RunnerId",
                principalTable: "Runners",
                principalColumn: "RunnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_Runners_RunnerId",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_RunnerId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "RunnerId",
                table: "Jobs");

            migrationBuilder.AddColumn<int>(
                name: "JobId1",
                table: "Runners",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Runners_JobId1",
                table: "Runners",
                column: "JobId1",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Runners_Jobs_JobId1",
                table: "Runners",
                column: "JobId1",
                principalTable: "Jobs",
                principalColumn: "JobId");
        }
    }
}
