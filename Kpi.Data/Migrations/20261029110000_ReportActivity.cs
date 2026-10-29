using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>
    /// Settings > Audit > Report activity (29 Oct 2026, Steve: "how people interact with the reports ... including when
    /// someone presses the timelapse button and for what year"): Auth.ReportEvent - filters chosen, views switched, buttons
    /// pressed, time on each report and how long its figures took to load - kept 3 months by Auth.spPruneHistory.
    /// </summary>
    public partial class ReportActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReportEvent",
                schema: "Auth",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2(0)", nullable: false),
                    Session = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Report = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Source = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Filters = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Ms = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportEvent", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportEvent_OccurredUtc",
                schema: "Auth",
                table: "ReportEvent",
                column: "OccurredUtc");

            // kept 3 months like the audit trail and report views: the nightly tidy-up removes older events too
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Auth.spPruneHistory
(
    @Months int = 3
)
AS
BEGIN
    SET NOCOUNT ON;
    IF @Months IS NULL OR @Months < 1 SET @Months = 1;
    DECLARE @Before datetime2(0) = DATEADD(MONTH, -@Months, SYSUTCDATETIME());

    DELETE FROM Auth.AuditEntry WHERE OccurredUtc < @Before;
    DECLARE @Audit int = @@ROWCOUNT;
    DELETE FROM Auth.ReportView WHERE OpenedUtc < @Before;
    DECLARE @Views int = @@ROWCOUNT;
    DELETE FROM Auth.ReportEvent WHERE OccurredUtc < @Before;

    SELECT @Audit AS AuditDeleted, @Views AS ViewsDeleted, @@ROWCOUNT AS EventsDeleted;
END
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Auth.spPruneHistory
(
    @Months int = 3
)
AS
BEGIN
    SET NOCOUNT ON;
    IF @Months IS NULL OR @Months < 1 SET @Months = 1;
    DECLARE @Before datetime2(0) = DATEADD(MONTH, -@Months, SYSUTCDATETIME());

    DELETE FROM Auth.AuditEntry WHERE OccurredUtc < @Before;
    DECLARE @Audit int = @@ROWCOUNT;
    DELETE FROM Auth.ReportView WHERE OpenedUtc < @Before;

    SELECT @Audit AS AuditDeleted, @@ROWCOUNT AS ViewsDeleted;
END
""");
            migrationBuilder.DropTable(
                name: "ReportEvent",
                schema: "Auth");
        }
    }
}
