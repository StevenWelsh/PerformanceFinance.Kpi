using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>
    /// Settings > Audit, fuller (19 Oct 2026, Steve): Auth.ReportView - who opened which report - and Auth.spPruneHistory,
    /// which the site runs nightly to keep the audit trail and report views to 3 months. The site's role may not change
    /// or delete audit lines itself (DENY); the procedure, owned like the tables, deletes only lines older than the limit.
    /// </summary>
    public partial class ReportViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReportView",
                schema: "Auth",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OpenedUtc = table.Column<DateTime>(type: "datetime2(0)", nullable: false),
                    Report = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Source = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportView", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportView_OpenedUtc",
                schema: "Auth",
                table: "ReportView",
                column: "OpenedUtc");

            // the nightly tidy-up: ownership chaining (dbo owns the procedure and both tables) lets kpi_site run it although
            // it is DENIED deleting audit lines itself - and it deletes nothing newer than @Months (at least 1)
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
            migrationBuilder.Sql("GRANT EXECUTE ON Auth.spPruneHistory TO kpi_site;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS Auth.spPruneHistory;");
            migrationBuilder.DropTable(
                name: "ReportView",
                schema: "Auth");
        }
    }
}
