using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>Year on Year: the current month and year compared to the SAME DATE last year, as Klipfolio did.</summary>
    public partial class YearOnYearToDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spYearOnYear
(
    @Measure       nvarchar(4)   = 'AF',
    @Interval      nvarchar(10)  = 'Month',
    @DivisionId    int           = -1,
    @SalespersonId int           = -1,
    @Arrangement   nvarchar(100) = NULL,
    @SalesOfficeId int           = NULL
)
AS
BEGIN
    SET NOCOUNT ON;

    -- TotalToDate (19 Oct 2026, Steve: Klipfolio compared the CURRENT month and year to the SAME DATE last year): each
    -- year's figure for the period up to the report date's day of the year (to 19 Oct: Jan-Sep in full, 1-19 Oct, not
    -- Nov-Dec). The page compares the current year with the prior year's TotalToDate.
    DECLARE @AsOf date = Rpt.fnReportDate();
    DECLARE @M int = MONTH(@AsOf), @D int = DAY(@AsOf);

    SELECT
        YEAR(F.dtPayReceived)                                                   AS [Year],
        CASE WHEN @Interval = 'Quarter' THEN DATEPART(QUARTER, F.dtPayReceived)
             ELSE MONTH(F.dtPayReceived) END                                    AS Period,
        SUM(CASE WHEN @Measure = 'TFI' THEN F.TFI ELSE F.AF END)                AS Total,
        SUM(CASE WHEN F.IsOB = 1 THEN CASE WHEN @Measure = 'TFI' THEN F.TFI ELSE F.AF END ELSE 0 END) AS TotalOB,
        SUM(CASE WHEN F.IsOB = 0 THEN CASE WHEN @Measure = 'TFI' THEN F.TFI ELSE F.AF END ELSE 0 END) AS TotalBrokered,
        SUM(CASE WHEN MONTH(F.dtPayReceived) < @M OR (MONTH(F.dtPayReceived) = @M AND DAY(F.dtPayReceived) <= @D)
                 THEN CASE WHEN @Measure = 'TFI' THEN F.TFI ELSE F.AF END ELSE 0 END)   AS TotalToDate
    FROM   Fact.Lead F
    WHERE  F.IsUnwound = 0
      AND  YEAR(F.dtPayReceived) > YEAR(@AsOf) - 10
      AND  (@DivisionId    = -1    OR F.DivisionId    = @DivisionId)
      AND  (@SalespersonId = -1    OR F.SalesId       = @SalespersonId)
      AND  (@Arrangement   IS NULL OR F.Arrangement   = @Arrangement)
      AND  (@SalesOfficeId IS NULL OR F.SalesOfficeId = @SalesOfficeId)
    GROUP BY
        YEAR(F.dtPayReceived),
        CASE WHEN @Interval = 'Quarter' THEN DATEPART(QUARTER, F.dtPayReceived) ELSE MONTH(F.dtPayReceived) END
    ORDER BY [Year], Period;
END
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spYearOnYear
(
    @Measure       nvarchar(4)   = 'AF',
    @Interval      nvarchar(10)  = 'Month',
    @DivisionId    int           = -1,
    @SalespersonId int           = -1,
    @Arrangement   nvarchar(100) = NULL,
    @SalesOfficeId int           = NULL
)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        YEAR(F.dtPayReceived)                                                   AS [Year],
        CASE WHEN @Interval = 'Quarter' THEN DATEPART(QUARTER, F.dtPayReceived)
             ELSE MONTH(F.dtPayReceived) END                                    AS Period,
        SUM(CASE WHEN @Measure = 'TFI' THEN F.TFI ELSE F.AF END)                AS Total,
        SUM(CASE WHEN F.IsOB = 1 THEN CASE WHEN @Measure = 'TFI' THEN F.TFI ELSE F.AF END ELSE 0 END) AS TotalOB,
        SUM(CASE WHEN F.IsOB = 0 THEN CASE WHEN @Measure = 'TFI' THEN F.TFI ELSE F.AF END ELSE 0 END) AS TotalBrokered
    FROM   Fact.Lead F
    WHERE  F.IsUnwound = 0
      AND  YEAR(F.dtPayReceived) > YEAR(Rpt.fnReportDate()) - 10
      AND  (@DivisionId    = -1    OR F.DivisionId    = @DivisionId)
      AND  (@SalespersonId = -1    OR F.SalesId       = @SalespersonId)
      AND  (@Arrangement   IS NULL OR F.Arrangement   = @Arrangement)
      AND  (@SalesOfficeId IS NULL OR F.SalesOfficeId = @SalesOfficeId)
    GROUP BY
        YEAR(F.dtPayReceived),
        CASE WHEN @Interval = 'Quarter' THEN DATEPART(QUARTER, F.dtPayReceived) ELSE MONTH(F.dtPayReceived) END
    ORDER BY [Year], Period;
END
""");
        }
    }
}
