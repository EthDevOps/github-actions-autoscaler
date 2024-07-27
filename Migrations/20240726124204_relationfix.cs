using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GithubActionsOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class relationfix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Runners_Jobs_JobId",
                table: "Runners");

            migrationBuilder.AlterColumn<int>(
                name: "JobId",
                table: "Runners",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

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
                name: "FK_Runners_Jobs_JobId",
                table: "Runners",
                column: "JobId",
                principalTable: "Jobs",
                principalColumn: "JobId");

            migrationBuilder.AddForeignKey(
                name: "FK_Runners_Jobs_JobId1",
                table: "Runners",
                column: "JobId1",
                principalTable: "Jobs",
                principalColumn: "JobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Runners_Jobs_JobId",
                table: "Runners");

            migrationBuilder.DropForeignKey(
                name: "FK_Runners_Jobs_JobId1",
                table: "Runners");

            migrationBuilder.DropIndex(
                name: "IX_Runners_JobId1",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "JobId1",
                table: "Runners");

            migrationBuilder.AlterColumn<int>(
                name: "JobId",
                table: "Runners",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Runners_Jobs_JobId",
                table: "Runners",
                column: "JobId",
                principalTable: "Jobs",
                principalColumn: "JobId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
