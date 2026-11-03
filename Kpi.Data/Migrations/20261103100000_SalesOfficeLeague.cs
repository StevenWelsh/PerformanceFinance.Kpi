using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>
    /// The Sales Office filter made real (3 Nov 2026, Steve - Mobberley's first deals, 26 Oct, showed under Kettering and
    /// "All" while Mobberley said "no figures yet"). Sales Summary, Year on Year and Business Analysis's procedures
    /// already took @SalesOfficeId (the pages never passed it); Sales Performance gains the filter, so its league table
    /// ("Other" included) and timelapse take it here. NULL = every office, as before, so the old site runs unchanged on
    /// this database (a deployment slot swap runs both for a moment).
    /// </summary>
    public partial class SalesOfficeLeague : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spSalesLeagueTable
(
    @Year          int,
    @Measure       nvarchar(4) = 'TFI',
    @SalesOfficeId int         = NULL      -- NULL = every office; else only that office's deals (Fact.Lead.SalesOfficeId)
)
AS
BEGIN
    SET NOCOUNT ON;

    WITH Core AS        -- the year's reportable salespeople (Cfg.SalespersonScope replaces the hard-coded ids)
    (
        SELECT DISTINCT SalesId
        FROM   Cfg.SalespersonScope
        WHERE  InLeague = 1 AND @Year >= FromYear AND (ToYear IS NULL OR @Year <= ToYear)
    )
    SELECT
        @Year                                                     AS Yr,
        F.SalesId,
        F.Salesperson,
        SUM(CASE WHEN @Measure = 'AF' THEN F.AF ELSE F.TFI END)   AS MeasureValue,
        CAST(MAX(CASE WHEN C.SalesId IS NULL THEN 0 ELSE 1 END) AS bit) AS IsCore
    FROM   Fact.Lead F
           LEFT JOIN Core C ON C.SalesId = F.SalesId
    WHERE  F.IsUnwound = 0
      AND  YEAR(F.dtPayReceived) = @Year
      AND  (@SalesOfficeId IS NULL OR F.SalesOfficeId = @SalesOfficeId)
    GROUP BY F.SalesId, F.Salesperson
    ORDER BY IsCore DESC, MeasureValue DESC;
END

""");
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spSalesLeagueTimelapse
(
    @Year          int,
    @Measure       nvarchar(4) = 'TFI',
    @SalesOfficeId int         = NULL      -- NULL = every office; else only that office's deals (Fact.Lead.SalesOfficeId)
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @YearStart date = DATEFROMPARTS(@Year, 1, 1);

    WITH Core AS        -- the year's reportable salespeople (Cfg.SalespersonScope replaces the hard-coded ids)
    (
        SELECT DISTINCT SalesId
        FROM   Cfg.SalespersonScope
        WHERE  InLeague = 1 AND @Year >= FromYear AND (ToYear IS NULL OR @Year <= ToYear)
    )
    SELECT
        F.SalesId,
        F.Salesperson,
        DATEDIFF(WEEK, @YearStart, F.dtPayReceived) + 1                                           AS WeekNo,
        DATEADD(DAY, 6, DATEADD(WEEK, DATEDIFF(WEEK, @YearStart, F.dtPayReceived), @YearStart))    AS WeekEnd,
        SUM(CASE WHEN @Measure = 'AF' THEN F.AF ELSE F.TFI END)                                   AS WeekValue
    FROM   Fact.Lead F
           INNER JOIN Core C ON C.SalesId = F.SalesId          -- the same people as the league
    WHERE  F.IsUnwound = 0
      AND  YEAR(F.dtPayReceived) = @Year
      AND  (@SalesOfficeId IS NULL OR F.SalesOfficeId = @SalesOfficeId)
    GROUP BY F.SalesId, F.Salesperson,
             DATEDIFF(WEEK, @YearStart, F.dtPayReceived),
             DATEADD(DAY, 6, DATEADD(WEEK, DATEDIFF(WEEK, @YearStart, F.dtPayReceived), @YearStart))
    ORDER BY WeekNo, F.SalesId;
END

""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spSalesLeagueTable
(
    @Year    int,
    @Measure nvarchar(4) = 'TFI'
)
AS
BEGIN
    SET NOCOUNT ON;

    WITH Core AS        -- the year's reportable salespeople (Cfg.SalespersonScope replaces the hard-coded ids)
    (
        SELECT DISTINCT SalesId
        FROM   Cfg.SalespersonScope
        WHERE  InLeague = 1 AND @Year >= FromYear AND (ToYear IS NULL OR @Year <= ToYear)
    )
    SELECT
        @Year                                                     AS Yr,
        F.SalesId,
        F.Salesperson,
        SUM(CASE WHEN @Measure = 'AF' THEN F.AF ELSE F.TFI END)   AS MeasureValue,
        CAST(MAX(CASE WHEN C.SalesId IS NULL THEN 0 ELSE 1 END) AS bit) AS IsCore
    FROM   Fact.Lead F
           LEFT JOIN Core C ON C.SalesId = F.SalesId
    WHERE  F.IsUnwound = 0
      AND  YEAR(F.dtPayReceived) = @Year
    GROUP BY F.SalesId, F.Salesperson
    ORDER BY IsCore DESC, MeasureValue DESC;
END

""");
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spSalesLeagueTimelapse
(
    @Year    int,
    @Measure nvarchar(4) = 'TFI'
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @YearStart date = DATEFROMPARTS(@Year, 1, 1);

    WITH Core AS        -- the year's reportable salespeople (Cfg.SalespersonScope replaces the hard-coded ids)
    (
        SELECT DISTINCT SalesId
        FROM   Cfg.SalespersonScope
        WHERE  InLeague = 1 AND @Year >= FromYear AND (ToYear IS NULL OR @Year <= ToYear)
    )
    SELECT
        F.SalesId,
        F.Salesperson,
        DATEDIFF(WEEK, @YearStart, F.dtPayReceived) + 1                                           AS WeekNo,
        DATEADD(DAY, 6, DATEADD(WEEK, DATEDIFF(WEEK, @YearStart, F.dtPayReceived), @YearStart))    AS WeekEnd,
        SUM(CASE WHEN @Measure = 'AF' THEN F.AF ELSE F.TFI END)                                   AS WeekValue
    FROM   Fact.Lead F
           INNER JOIN Core C ON C.SalesId = F.SalesId          -- the same people as the league
    WHERE  F.IsUnwound = 0
      AND  YEAR(F.dtPayReceived) = @Year
    GROUP BY F.SalesId, F.Salesperson,
             DATEDIFF(WEEK, @YearStart, F.dtPayReceived),
             DATEADD(DAY, 6, DATEADD(WEEK, DATEDIFF(WEEK, @YearStart, F.dtPayReceived), @YearStart))
    ORDER BY WeekNo, F.SalesId;
END

""");
        }
    }
}
