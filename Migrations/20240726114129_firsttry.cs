using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GithubActionsOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class firsttry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Runners_Workflows_WorkflowId",
                table: "Runners");

            migrationBuilder.DropTable(
                name: "Workflows");

            migrationBuilder.DropIndex(
                name: "IX_Runners_WorkflowId",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "WorkflowId",
                table: "Runners");

            migrationBuilder.AddColumn<string>(
                name: "Arch",
                table: "Runners",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CloudServerId",
                table: "Runners",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "IsCustom",
                table: "Runners",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsOnline",
                table: "Runners",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "JobId",
                table: "Runners",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Owner",
                table: "Runners",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    JobId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GithubJobId = table.Column<long>(type: "bigint", nullable: false),
                    Repository = table.Column<string>(type: "text", nullable: true),
                    Owner = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    InProgressTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    QueueTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompleteTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    JobUrl = table.Column<string>(type: "text", nullable: true),
                    Orphan = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.JobId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Runners_JobId",
                table: "Runners",
                column: "JobId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Runners_Jobs_JobId",
                table: "Runners",
                column: "JobId",
                principalTable: "Jobs",
                principalColumn: "JobId",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Runners_Jobs_JobId",
                table: "Runners");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Runners_JobId",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "Arch",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "CloudServerId",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "IsCustom",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "IsOnline",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "JobId",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "Owner",
                table: "Runners");

            migrationBuilder.AddColumn<int>(
                name: "WorkflowId",
                table: "Runners",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Workflows",
                columns: table => new
                {
                    WorkflowId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GithubWorkflowId = table.Column<long>(type: "bigint", nullable: false),
                    Owner = table.Column<string>(type: "text", nullable: true),
                    Repository = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workflows", x => x.WorkflowId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Runners_WorkflowId",
                table: "Runners",
                column: "WorkflowId");

            migrationBuilder.AddForeignKey(
                name: "FK_Runners_Workflows_WorkflowId",
                table: "Runners",
                column: "WorkflowId",
                principalTable: "Workflows",
                principalColumn: "WorkflowId");
        }
    }
}
