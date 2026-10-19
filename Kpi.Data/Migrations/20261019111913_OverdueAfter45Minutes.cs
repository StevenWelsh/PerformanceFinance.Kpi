using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>The figures count as overdue after 45 minutes in office hours (was 30): the office now refreshes every 30.</summary>
    public partial class OverdueAfter45Minutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spDataStatus
AS
BEGIN
    SET NOCOUNT ON;
    -- FiguresStale after 45 minutes, not 30 (19 Oct 2026, Steve): the office refreshes every 30 minutes now, so 30 tripped
    -- briefly on nearly every cycle; 45 = one refresh plus the site's collection and some slack.

    DECLARE @NowUtc datetime2(0) = SYSUTCDATETIME();
    DECLARE @NowUk  datetime2(0) = CAST(@NowUtc AT TIME ZONE 'UTC' AT TIME ZONE 'GMT Standard Time' AS datetime2(0));
    -- office hours: Monday to Saturday (1 Jan 1900 was a Monday, so day 6 of each week is Sunday), 07:30-20:00
    DECLARE @InOfficeHours bit =
        CASE WHEN DATEDIFF(DAY, '19000101', @NowUk) % 7 <> 6
              AND CAST(@NowUk AS time) BETWEEN '07:30' AND '20:00' THEN 1 ELSE 0 END;

    SELECT
        S.ReportDate,
        S.LastFrequentLoadUtc,
        S.LastNightlyLoadUtc,
        CAST(S.LastFrequentLoadUtc AT TIME ZONE 'UTC' AT TIME ZONE 'GMT Standard Time' AS datetime2(0)) AS LastUpdatedUk,
        CAST(CASE WHEN @InOfficeHours = 1
                   AND (S.LastFrequentLoadUtc IS NULL OR S.LastFrequentLoadUtc < DATEADD(MINUTE, -45, @NowUtc))
                  THEN 1 ELSE 0 END AS bit) AS FiguresStale,
        CAST(CASE WHEN S.LastNightlyLoadUtc IS NULL OR S.LastNightlyLoadUtc < DATEADD(HOUR, -26, @NowUtc)
                  THEN 1 ELSE 0 END AS bit) AS NightlyStale
    FROM   Ref.Setting S
    WHERE  S.SettingId = 1;
END
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spDataStatus
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @NowUtc datetime2(0) = SYSUTCDATETIME();
    DECLARE @NowUk  datetime2(0) = CAST(@NowUtc AT TIME ZONE 'UTC' AT TIME ZONE 'GMT Standard Time' AS datetime2(0));
    -- office hours: Monday to Saturday (1 Jan 1900 was a Monday, so day 6 of each week is Sunday), 07:30-20:00
    DECLARE @InOfficeHours bit =
        CASE WHEN DATEDIFF(DAY, '19000101', @NowUk) % 7 <> 6
              AND CAST(@NowUk AS time) BETWEEN '07:30' AND '20:00' THEN 1 ELSE 0 END;

    SELECT
        S.ReportDate,
        S.LastFrequentLoadUtc,
        S.LastNightlyLoadUtc,
        CAST(S.LastFrequentLoadUtc AT TIME ZONE 'UTC' AT TIME ZONE 'GMT Standard Time' AS datetime2(0)) AS LastUpdatedUk,
        CAST(CASE WHEN @InOfficeHours = 1
                   AND (S.LastFrequentLoadUtc IS NULL OR S.LastFrequentLoadUtc < DATEADD(MINUTE, -30, @NowUtc))
                  THEN 1 ELSE 0 END AS bit) AS FiguresStale,
        CAST(CASE WHEN S.LastNightlyLoadUtc IS NULL OR S.LastNightlyLoadUtc < DATEADD(HOUR, -26, @NowUtc)
                  THEN 1 ELSE 0 END AS bit) AS NightlyStale
    FROM   Ref.Setting S
    WHERE  S.SettingId = 1;
END
""");
        }
    }
}
