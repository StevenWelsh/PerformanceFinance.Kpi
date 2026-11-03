using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>
    /// Settings > Scheduled tasks (3 Nov 2026, Steve - SHS-Web's scheduler; it replaced Data sync and Shire sync):
    /// Cfg.ScheduledTask, each task's schedule and latest state (and the lease that lets one copy of the site run it at a
    /// time), and Cfg.ScheduledTaskRun, every run. The site reads and writes both.
    /// </summary>
    public partial class ScheduledTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScheduledTask",
                schema: "Cfg",
                columns: table => new
                {
                    Key = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    DaysMask = table.Column<int>(type: "int", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    RepeatEveryMinutes = table.Column<int>(type: "int", nullable: true),
                    RepeatUntil = table.Column<TimeOnly>(type: "time", nullable: true),
                    RunMissed = table.Column<bool>(type: "bit", nullable: false),
                    RetryAfterMinutes = table.Column<int>(type: "int", nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    NextDueUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastStartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastFinishedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastOutcome = table.Column<int>(type: "int", nullable: true),
                    LastMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LastDurationMs = table.Column<int>(type: "int", nullable: true),
                    LastSucceededUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledTask", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledTaskRun",
                schema: "Cfg",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaskKey = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    Trigger = table.Column<int>(type: "int", nullable: false),
                    TriggeredBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Instance = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledTaskRun", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTaskRun_TaskKey_StartedUtc",
                schema: "Cfg",
                table: "ScheduledTaskRun",
                columns: new[] { "TaskKey", "StartedUtc" });
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE, DELETE ON Cfg.ScheduledTask TO kpi_site;");
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE, DELETE ON Cfg.ScheduledTaskRun TO kpi_site;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduledTask",
                schema: "Cfg");

            migrationBuilder.DropTable(
                name: "ScheduledTaskRun",
                schema: "Cfg");
        }
    }
}
