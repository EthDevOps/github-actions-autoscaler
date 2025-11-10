using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GithubActionsOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class PersistentQueues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RunnerLifecycles_Runners_RunnerId",
                table: "RunnerLifecycles");

            migrationBuilder.AlterColumn<int>(
                name: "RunnerId",
                table: "RunnerLifecycles",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "CancelledRunnersCounters",
                columns: table => new
                {
                    CancelledRunnersCounterId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Owner = table.Column<string>(type: "text", nullable: true),
                    Repository = table.Column<string>(type: "text", nullable: true),
                    Size = table.Column<string>(type: "text", nullable: true),
                    Profile = table.Column<string>(type: "text", nullable: true),
                    Arch = table.Column<string>(type: "text", nullable: true),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CancelledRunnersCounters", x => x.CancelledRunnersCounterId);
                });

            migrationBuilder.CreateTable(
                name: "CreateTaskQueues",
                columns: table => new
                {
                    CreateTaskQueueId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    TargetType = table.Column<int>(type: "integer", nullable: false),
                    RepoName = table.Column<string>(type: "text", nullable: true),
                    RunnerDbId = table.Column<int>(type: "integer", nullable: false),
                    IsStuckReplacement = table.Column<bool>(type: "boolean", nullable: false),
                    StuckJobId = table.Column<int>(type: "integer", nullable: true),
                    QueuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreateTaskQueues", x => x.CreateTaskQueueId);
                });

            migrationBuilder.CreateTable(
                name: "CreatedRunnersTrackings",
                columns: table => new
                {
                    Hostname = table.Column<string>(type: "text", nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    TargetType = table.Column<int>(type: "integer", nullable: false),
                    RepoName = table.Column<string>(type: "text", nullable: true),
                    RunnerDbId = table.Column<int>(type: "integer", nullable: false),
                    IsStuckReplacement = table.Column<bool>(type: "boolean", nullable: false),
                    StuckJobId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreatedRunnersTrackings", x => x.Hostname);
                });

            migrationBuilder.CreateTable(
                name: "DeleteTaskQueues",
                columns: table => new
                {
                    DeleteTaskQueueId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    ServerId = table.Column<long>(type: "bigint", nullable: false),
                    RunnerDbId = table.Column<int>(type: "integer", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeleteTaskQueues", x => x.DeleteTaskQueueId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CancelledRunnersCounters_Owner_Repository_Size_Profile_Arch",
                table: "CancelledRunnersCounters",
                columns: new[] { "Owner", "Repository", "Size", "Profile", "Arch" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_RunnerLifecycles_Runners_RunnerId",
                table: "RunnerLifecycles",
                column: "RunnerId",
                principalTable: "Runners",
                principalColumn: "RunnerId",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RunnerLifecycles_Runners_RunnerId",
                table: "RunnerLifecycles");

            migrationBuilder.DropTable(
                name: "CancelledRunnersCounters");

            migrationBuilder.DropTable(
                name: "CreateTaskQueues");

            migrationBuilder.DropTable(
                name: "CreatedRunnersTrackings");

            migrationBuilder.DropTable(
                name: "DeleteTaskQueues");

            migrationBuilder.AlterColumn<int>(
                name: "RunnerId",
                table: "RunnerLifecycles",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddForeignKey(
                name: "FK_RunnerLifecycles_Runners_RunnerId",
                table: "RunnerLifecycles",
                column: "RunnerId",
                principalTable: "Runners",
                principalColumn: "RunnerId");
        }
    }
}
