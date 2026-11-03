using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>
    /// The Sales Office filter on Margin Analysis and Minimum IRR (3 Nov 2026, Steve - all of Mobberley's first deals are
    /// own book, so both reports already count them). Minimum IRR: one condition on its deals. Margin Analysis: the office
    /// scopes the sell side as the division does, and the buy side - RCF / IWFA lines by their deal's office, blocks by the
    /// office's share of own-book AF (an apportionment, as the division's is). NULL = every office, as before; the date-range
    /// procedure takes it LAST so the old site's positional calls work unchanged (a slot swap runs both for a moment).
    /// </summary>
    public partial class SalesOfficeMarginIrr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spMarginAnalysisByDateRange
(
    @StartDate    date,
    @EndDate      date,
    @DivisionId   int,
    @BuyRate      decimal(18,4) OUTPUT,
    @SellRate     decimal(18,4) OUTPUT,
    @Margin       decimal(18,4) OUTPUT,
    @TotalFunding decimal(18,2) OUTPUT,
    @TotalAF      decimal(18,2) OUTPUT,
    @SalesOfficeId int          = NULL     -- NULL = every office (last, so positional callers are unchanged)
)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- read every table as at ONE moment, even if a publish commits part-way through this procedure
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
    BEGIN TRANSACTION;

    DECLARE @AF decimal(18,2), @AFAll decimal(18,2), @SellTermRate decimal(38,6), @SellTerm decimal(38,6);
    DECLARE @BuyAmount decimal(38,6), @BuyTermRate decimal(38,6), @BuyTerm decimal(38,6), @Ratio decimal(18,4);

    -- 1. sell side: own-book leads paid out in the range (unwound ones included, as today); the division and the
    --    office scope the figures, @AFAll stays every own-book lead (the blocks' share below)
    SELECT
        @AF           = SUM(CASE WHEN (@DivisionId = -1 OR DivisionId = @DivisionId) AND (@SalesOfficeId IS NULL OR SalesOfficeId = @SalesOfficeId) THEN AF ELSE 0 END),
        @SellTermRate = SUM(CASE WHEN (@DivisionId = -1 OR DivisionId = @DivisionId) AND (@SalesOfficeId IS NULL OR SalesOfficeId = @SalesOfficeId) THEN AF * Term * (IRR / 100) ELSE 0 END),
        @SellTerm     = SUM(CASE WHEN (@DivisionId = -1 OR DivisionId = @DivisionId) AND (@SalesOfficeId IS NULL OR SalesOfficeId = @SalesOfficeId) THEN AF * Term ELSE 0 END),
        @AFAll        = SUM(AF)
    FROM   Fact.Lead
    WHERE  IsOB = 1 AND dtPayReceived BETWEEN @StartDate AND @EndDate;

    SET @Ratio = CASE WHEN ISNULL(@AFAll, 0) = 0 THEN 0 ELSE @AF / @AFAll END;

    -- 2. buy side: blocks shared out by the scope's share of own-book AF; RCF and IWFA by their lead's division and
    --    office (every RCF / IWFA line's lead is in Fact.Lead, so the offices add up to the whole)
    SELECT
        @BuyAmount   = SUM(X.Amount),
        @BuyTermRate = SUM(X.Amount * X.Term * X.Rate),
        @BuyTerm     = SUM(X.Amount * X.Term)
    FROM
    (
        SELECT CASE WHEN FL.Source = 'Block' THEN FL.Amount * @Ratio ELSE FL.Amount END AS Amount, FL.Term, FL.Rate
        FROM   Fact.FundingLine FL
        WHERE  FL.EventDate BETWEEN @StartDate AND @EndDate
          AND  FL.IsBulkMove = 0
          AND  (FL.Source = 'Block' OR @DivisionId = -1 OR FL.DivisionId = @DivisionId)
          AND  (FL.Source = 'Block' OR @SalesOfficeId IS NULL
                OR EXISTS (SELECT 1 FROM Fact.Lead L WHERE L.LeadId = FL.LeadId AND L.SalesOfficeId = @SalesOfficeId))
    ) X;

    SET @SellRate     = CASE WHEN ISNULL(@SellTerm, 0) = 0 THEN 0 ELSE @SellTermRate / @SellTerm END;
    SET @BuyRate      = CASE WHEN ISNULL(@BuyTerm, 0)  = 0 THEN 0 ELSE @BuyTermRate  / @BuyTerm  END;
    SET @Margin       = @SellRate - @BuyRate;
    SET @TotalFunding = ISNULL(@BuyAmount, 0);
    SET @TotalAF      = @AF;

    COMMIT TRANSACTION;
END

""");
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spMarginAnalysisByYear (@DivisionId int, @SalesOfficeId int = NULL)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- read every table as at ONE moment, even if a publish commits part-way through this procedure
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
    BEGIN TRANSACTION;
    DECLARE @Out TABLE (YearInt int, BuyRate decimal(18,4), SellRate decimal(18,4), Margin decimal(18,4),
                        TotalFunding decimal(18,4), TotalAF decimal(18,4));
    DECLARE @Year int = 2021, @Start date, @End date;
    DECLARE @Buy decimal(18,4), @Sell decimal(18,4), @Margin decimal(18,4), @Funding decimal(18,2), @AF decimal(18,2);

    WHILE @Year <= YEAR(GETDATE())
    BEGIN
        SET @Start = DATEFROMPARTS(@Year, 1, 1);
        SET @End   = DATEFROMPARTS(@Year, 12, 31);
        EXEC Rpt.spMarginAnalysisByDateRange @Start, @End, @DivisionId, @Buy OUTPUT, @Sell OUTPUT, @Margin OUTPUT, @Funding OUTPUT, @AF OUTPUT,
             @SalesOfficeId = @SalesOfficeId;
        INSERT @Out VALUES (@Year, @Buy, @Sell, @Margin, @Funding, @AF);
        SET @Year += 1;
    END

    SELECT * FROM @Out ORDER BY YearInt;
    COMMIT TRANSACTION;
END

""");
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spMarginAnalysisByMonth (@DivisionId int, @RollingMonth int, @SalesOfficeId int = NULL)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- read every table as at ONE moment, even if a publish commits part-way through this procedure
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
    BEGIN TRANSACTION;
    DECLARE @First date = CASE WHEN @RollingMonth = 1
                               THEN DATEFROMPARTS(YEAR(DATEADD(MONTH, -11, GETDATE())), MONTH(DATEADD(MONTH, -11, GETDATE())), 1)
                               ELSE DATEFROMPARTS(YEAR(GETDATE()), 1, 1) END;
    DECLARE @Out TABLE (YearInt int, MonthInt int, MonthString nvarchar(20), BuyRate decimal(18,4), SellRate decimal(18,4),
                        Margin decimal(18,4), TotalFunding decimal(18,4), TotalAF decimal(18,4), IsFuture bit,
                        OrderInt int, StartDate date, EndDate date);
    DECLARE @i int = 1, @Start date, @End date;
    DECLARE @Buy decimal(18,4), @Sell decimal(18,4), @Margin decimal(18,4), @Funding decimal(18,2), @AF decimal(18,2);

    WHILE @i <= 13          -- 12 months, then the overall row
    BEGIN
        SET @Start = CASE WHEN @i < 13 THEN DATEADD(MONTH, @i - 1, @First) ELSE @First END;
        SET @End   = CASE WHEN @i < 13 THEN EOMONTH(@Start) ELSE EOMONTH(DATEADD(MONTH, 11, @First)) END;

        EXEC Rpt.spMarginAnalysisByDateRange @Start, @End, @DivisionId, @Buy OUTPUT, @Sell OUTPUT, @Margin OUTPUT, @Funding OUTPUT, @AF OUTPUT,
             @SalesOfficeId = @SalesOfficeId;

        INSERT @Out VALUES (YEAR(@Start), CASE WHEN @i < 13 THEN MONTH(@Start) ELSE 13 END,
                            CASE WHEN @i < 13 THEN FORMAT(@Start, 'MMM yyyy') ELSE 'Overall' END,
                            @Buy, @Sell, @Margin, @Funding, ISNULL(@AF, 0),
                            CASE WHEN @i < 13 AND @Start > CAST(GETDATE() AS date) THEN 1 ELSE 0 END,
                            @i, @Start, @End);
        SET @i += 1;
    END

    SELECT * FROM @Out ORDER BY OrderInt;
    COMMIT TRANSACTION;
END

""");
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spMarginAnalysisByDays (@Days int, @DivisionId int, @SalesOfficeId int = NULL)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- read every table as at ONE moment, even if a publish commits part-way through this procedure
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
    BEGIN TRANSACTION;
    DECLARE @Start date = DATEADD(DAY, -@Days, GETDATE()), @End date = GETDATE();
    DECLARE @Buy decimal(18,4), @Sell decimal(18,4), @Margin decimal(18,4), @Funding decimal(18,2), @AF decimal(18,2);

    EXEC Rpt.spMarginAnalysisByDateRange @Start, @End, @DivisionId, @Buy OUTPUT, @Sell OUTPUT, @Margin OUTPUT, @Funding OUTPUT, @AF OUTPUT,
             @SalesOfficeId = @SalesOfficeId;

    SELECT CONVERT(nvarchar(10), @Start, 120) + ' - ' + CONVERT(nvarchar(10), @End, 120) AS DateRange,
           @Buy AS BuyRate, @Sell AS SellRate, @Margin AS Margin, @Funding AS TotalFunding, @AF AS TotalAF;
    COMMIT TRANSACTION;
END

""");
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spMinIRRAnalysis
(
    @AnalysisPeriod nvarchar(10) = 'M_This',
    @DivisionId     int          = -1,
    @SalesOfficeId  int          = NULL     -- NULL = every office
)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- read every table as at ONE moment, even if a publish commits part-way through this procedure
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
    BEGIN TRANSACTION;

    DECLARE @Grain char(1) = CASE WHEN @AnalysisPeriod LIKE '%M%' THEN 'M' WHEN @AnalysisPeriod LIKE '%Q%' THEN 'Q' ELSE 'Y' END;
    DECLARE @This  bit     = CASE WHEN @AnalysisPeriod LIKE '%This%' THEN 1 ELSE 0 END;
    DECLARE @Today date    = CAST(GETDATE() AS date);
    DECLARE @From  date, @To date;

    IF @Grain = 'M'
    BEGIN
        DECLARE @M date = CASE WHEN @This = 1 THEN @Today ELSE DATEADD(MONTH, -1, @Today) END;
        SET @From = DATEFROMPARTS(YEAR(@M), MONTH(@M), 1);
        SET @To   = EOMONTH(@M);
    END
    ELSE IF @Grain = 'Q'
    BEGIN
        DECLARE @QId int = (SELECT QuarterId FROM Dim.Period WHERE @Today BETWEEN PeriodStartDate AND PeriodEndDate)
                         - CASE WHEN @This = 1 THEN 0 ELSE 1 END;
        SELECT @From = MIN(PeriodStartDate), @To = MAX(PeriodEndDate) FROM Dim.Period WHERE QuarterId = @QId;
    END
    ELSE
    BEGIN
        DECLARE @Y int = YEAR(@Today) - CASE WHEN @This = 1 THEN 0 ELSE 1 END;
        SET @From = DATEFROMPARTS(@Y, 1, 1);
        SET @To   = DATEFROMPARTS(@Y, 12, 31);
    END

    SELECT
        CASE @Grain WHEN 'Y' THEN P.YearName WHEN 'Q' THEN P.QuarterName
             ELSE LEFT(DATENAME(MONTH, F.dtPayReceived), 3) + ' ' + DATENAME(YEAR, F.dtPayReceived) END AS GroupByPeriod,
        F.LeadId, F.IRRInclFees, F.MinIRR, F.SalesId, F.Salesperson, F.Division,
        ISNULL(F.AF, 0)                                                    AS AF,
        ISNULL(F.IRRInclFees, 0) * ISNULL(F.AF, 0) * ISNULL(F.Term, 0)     AS Term_AF_IRR,
        ISNULL(F.AF, 0) * ISNULL(F.Term, 0)                                AS Term_AF
    INTO   #LeadData
    FROM   Fact.Lead F
           INNER JOIN Dim.Period P ON P.PeriodId = F.PeriodId
    WHERE  F.IsOB = 1
      AND  YEAR(F.dtPayReceived) > 2021
      AND  (@DivisionId = -1 OR F.DivisionId = @DivisionId)
      AND  (@SalesOfficeId IS NULL OR F.SalesOfficeId = @SalesOfficeId)
      AND  F.dtPayReceived BETWEEN @From AND @To;

    -- per salesperson (active salespeople only, as today)
    SELECT
        ROW_NUMBER() OVER (ORDER BY L.Salesperson) AS OrderInt,
        L.GroupByPeriod, L.Division AS SalesDivision, L.Salesperson,
        COUNT(*)                                                                     AS TransactionsTotal,
        SUM(L.AF)                                                                    AS AFTotal,
        CASE WHEN SUM(L.Term_AF) = 0 THEN 0 ELSE SUM(L.Term_AF_IRR) / SUM(L.Term_AF) END AS IRRAverage,
        SUM(CASE WHEN L.IRRInclFees < L.MinIRR THEN 1 ELSE 0 END)                    AS TransactionsUnderMinIRR,
        SUM(CASE WHEN L.IRRInclFees < L.MinIRR THEN L.AF ELSE 0 END)                 AS AFUnderMinIRR
    INTO   #Results
    FROM   #LeadData L
           INNER JOIN Dim.Salesperson S ON S.SalesId = L.SalesId AND S.IsActive = 1
    GROUP BY L.GroupByPeriod, L.Division, L.Salesperson;

    -- the total row: every deal, and the people rows' below-minimum counts (as today)
    INSERT #Results
    SELECT 1000, GroupByPeriod, 'Total', 'Total', COUNT(*), SUM(AF),
           CASE WHEN SUM(Term_AF) = 0 THEN 0 ELSE SUM(Term_AF_IRR) / SUM(Term_AF) END,
           (SELECT SUM(TransactionsUnderMinIRR) FROM #Results),
           (SELECT SUM(AFUnderMinIRR) FROM #Results)
    FROM   #LeadData
    GROUP BY GroupByPeriod;

    SELECT OrderInt, GroupByPeriod, SalesDivision, Salesperson, TransactionsTotal,
           CAST(AFTotal AS decimal(18,0))                    AS AFTotal,
           CAST(IRRAverage AS decimal(18,4))                 AS IRRAverage,
           TransactionsUnderMinIRR,
           CAST(AFUnderMinIRR AS decimal(18,0))              AS AFUnderMinIRR,
           CAST(CASE WHEN TransactionsTotal > 0 THEN TransactionsUnderMinIRR * 1.0 / TransactionsTotal END AS decimal(18,4)) AS PercentTransactionsUnderMinIRR,
           CAST(CASE WHEN AFTotal > 0 THEN AFUnderMinIRR / AFTotal END AS decimal(18,4))                                     AS PercentAFUnderMinIRR
    FROM   #Results
    ORDER BY OrderInt;

    COMMIT TRANSACTION;
END

""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spMarginAnalysisByDateRange
(
    @StartDate    date,
    @EndDate      date,
    @DivisionId   int,
    @BuyRate      decimal(18,4) OUTPUT,
    @SellRate     decimal(18,4) OUTPUT,
    @Margin       decimal(18,4) OUTPUT,
    @TotalFunding decimal(18,2) OUTPUT,
    @TotalAF      decimal(18,2) OUTPUT
)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- read every table as at ONE moment, even if a publish commits part-way through this procedure
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
    BEGIN TRANSACTION;

    DECLARE @AF decimal(18,2), @AFAll decimal(18,2), @SellTermRate decimal(38,6), @SellTerm decimal(38,6);
    DECLARE @BuyAmount decimal(38,6), @BuyTermRate decimal(38,6), @BuyTerm decimal(38,6), @Ratio decimal(18,4);

    -- 1. sell side: own-book leads paid out in the range (unwound ones included, as today)
    SELECT
        @AF           = SUM(CASE WHEN @DivisionId = -1 OR DivisionId = @DivisionId THEN AF ELSE 0 END),
        @SellTermRate = SUM(CASE WHEN @DivisionId = -1 OR DivisionId = @DivisionId THEN AF * Term * (IRR / 100) ELSE 0 END),
        @SellTerm     = SUM(CASE WHEN @DivisionId = -1 OR DivisionId = @DivisionId THEN AF * Term ELSE 0 END),
        @AFAll        = SUM(AF)
    FROM   Fact.Lead
    WHERE  IsOB = 1 AND dtPayReceived BETWEEN @StartDate AND @EndDate;

    SET @Ratio = CASE WHEN ISNULL(@AFAll, 0) = 0 THEN 0 ELSE @AF / @AFAll END;

    -- 2. buy side: blocks shared out by the division's share; RCF and IWFA by their lead's division
    SELECT
        @BuyAmount   = SUM(X.Amount),
        @BuyTermRate = SUM(X.Amount * X.Term * X.Rate),
        @BuyTerm     = SUM(X.Amount * X.Term)
    FROM
    (
        SELECT CASE WHEN FL.Source = 'Block' THEN FL.Amount * @Ratio ELSE FL.Amount END AS Amount, FL.Term, FL.Rate
        FROM   Fact.FundingLine FL
        WHERE  FL.EventDate BETWEEN @StartDate AND @EndDate
          AND  FL.IsBulkMove = 0
          AND  (FL.Source = 'Block' OR @DivisionId = -1 OR FL.DivisionId = @DivisionId)
    ) X;

    SET @SellRate     = CASE WHEN ISNULL(@SellTerm, 0) = 0 THEN 0 ELSE @SellTermRate / @SellTerm END;
    SET @BuyRate      = CASE WHEN ISNULL(@BuyTerm, 0)  = 0 THEN 0 ELSE @BuyTermRate  / @BuyTerm  END;
    SET @Margin       = @SellRate - @BuyRate;
    SET @TotalFunding = ISNULL(@BuyAmount, 0);
    SET @TotalAF      = @AF;

    COMMIT TRANSACTION;
END

""");
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spMarginAnalysisByYear (@DivisionId int)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- read every table as at ONE moment, even if a publish commits part-way through this procedure
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
    BEGIN TRANSACTION;
    DECLARE @Out TABLE (YearInt int, BuyRate decimal(18,4), SellRate decimal(18,4), Margin decimal(18,4),
                        TotalFunding decimal(18,4), TotalAF decimal(18,4));
    DECLARE @Year int = 2021, @Start date, @End date;
    DECLARE @Buy decimal(18,4), @Sell decimal(18,4), @Margin decimal(18,4), @Funding decimal(18,2), @AF decimal(18,2);

    WHILE @Year <= YEAR(GETDATE())
    BEGIN
        SET @Start = DATEFROMPARTS(@Year, 1, 1);
        SET @End   = DATEFROMPARTS(@Year, 12, 31);
        EXEC Rpt.spMarginAnalysisByDateRange @Start, @End, @DivisionId, @Buy OUTPUT, @Sell OUTPUT, @Margin OUTPUT, @Funding OUTPUT, @AF OUTPUT;
        INSERT @Out VALUES (@Year, @Buy, @Sell, @Margin, @Funding, @AF);
        SET @Year += 1;
    END

    SELECT * FROM @Out ORDER BY YearInt;
    COMMIT TRANSACTION;
END

""");
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spMarginAnalysisByMonth (@DivisionId int, @RollingMonth int)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- read every table as at ONE moment, even if a publish commits part-way through this procedure
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
    BEGIN TRANSACTION;
    DECLARE @First date = CASE WHEN @RollingMonth = 1
                               THEN DATEFROMPARTS(YEAR(DATEADD(MONTH, -11, GETDATE())), MONTH(DATEADD(MONTH, -11, GETDATE())), 1)
                               ELSE DATEFROMPARTS(YEAR(GETDATE()), 1, 1) END;
    DECLARE @Out TABLE (YearInt int, MonthInt int, MonthString nvarchar(20), BuyRate decimal(18,4), SellRate decimal(18,4),
                        Margin decimal(18,4), TotalFunding decimal(18,4), TotalAF decimal(18,4), IsFuture bit,
                        OrderInt int, StartDate date, EndDate date);
    DECLARE @i int = 1, @Start date, @End date;
    DECLARE @Buy decimal(18,4), @Sell decimal(18,4), @Margin decimal(18,4), @Funding decimal(18,2), @AF decimal(18,2);

    WHILE @i <= 13          -- 12 months, then the overall row
    BEGIN
        SET @Start = CASE WHEN @i < 13 THEN DATEADD(MONTH, @i - 1, @First) ELSE @First END;
        SET @End   = CASE WHEN @i < 13 THEN EOMONTH(@Start) ELSE EOMONTH(DATEADD(MONTH, 11, @First)) END;

        EXEC Rpt.spMarginAnalysisByDateRange @Start, @End, @DivisionId, @Buy OUTPUT, @Sell OUTPUT, @Margin OUTPUT, @Funding OUTPUT, @AF OUTPUT;

        INSERT @Out VALUES (YEAR(@Start), CASE WHEN @i < 13 THEN MONTH(@Start) ELSE 13 END,
                            CASE WHEN @i < 13 THEN FORMAT(@Start, 'MMM yyyy') ELSE 'Overall' END,
                            @Buy, @Sell, @Margin, @Funding, ISNULL(@AF, 0),
                            CASE WHEN @i < 13 AND @Start > CAST(GETDATE() AS date) THEN 1 ELSE 0 END,
                            @i, @Start, @End);
        SET @i += 1;
    END

    SELECT * FROM @Out ORDER BY OrderInt;
    COMMIT TRANSACTION;
END

""");
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spMarginAnalysisByDays (@Days int, @DivisionId int)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- read every table as at ONE moment, even if a publish commits part-way through this procedure
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
    BEGIN TRANSACTION;
    DECLARE @Start date = DATEADD(DAY, -@Days, GETDATE()), @End date = GETDATE();
    DECLARE @Buy decimal(18,4), @Sell decimal(18,4), @Margin decimal(18,4), @Funding decimal(18,2), @AF decimal(18,2);

    EXEC Rpt.spMarginAnalysisByDateRange @Start, @End, @DivisionId, @Buy OUTPUT, @Sell OUTPUT, @Margin OUTPUT, @Funding OUTPUT, @AF OUTPUT;

    SELECT CONVERT(nvarchar(10), @Start, 120) + ' - ' + CONVERT(nvarchar(10), @End, 120) AS DateRange,
           @Buy AS BuyRate, @Sell AS SellRate, @Margin AS Margin, @Funding AS TotalFunding, @AF AS TotalAF;
    COMMIT TRANSACTION;
END

""");
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spMinIRRAnalysis
(
    @AnalysisPeriod nvarchar(10) = 'M_This',
    @DivisionId     int          = -1
)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- read every table as at ONE moment, even if a publish commits part-way through this procedure
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
    BEGIN TRANSACTION;

    DECLARE @Grain char(1) = CASE WHEN @AnalysisPeriod LIKE '%M%' THEN 'M' WHEN @AnalysisPeriod LIKE '%Q%' THEN 'Q' ELSE 'Y' END;
    DECLARE @This  bit     = CASE WHEN @AnalysisPeriod LIKE '%This%' THEN 1 ELSE 0 END;
    DECLARE @Today date    = CAST(GETDATE() AS date);
    DECLARE @From  date, @To date;

    IF @Grain = 'M'
    BEGIN
        DECLARE @M date = CASE WHEN @This = 1 THEN @Today ELSE DATEADD(MONTH, -1, @Today) END;
        SET @From = DATEFROMPARTS(YEAR(@M), MONTH(@M), 1);
        SET @To   = EOMONTH(@M);
    END
    ELSE IF @Grain = 'Q'
    BEGIN
        DECLARE @QId int = (SELECT QuarterId FROM Dim.Period WHERE @Today BETWEEN PeriodStartDate AND PeriodEndDate)
                         - CASE WHEN @This = 1 THEN 0 ELSE 1 END;
        SELECT @From = MIN(PeriodStartDate), @To = MAX(PeriodEndDate) FROM Dim.Period WHERE QuarterId = @QId;
    END
    ELSE
    BEGIN
        DECLARE @Y int = YEAR(@Today) - CASE WHEN @This = 1 THEN 0 ELSE 1 END;
        SET @From = DATEFROMPARTS(@Y, 1, 1);
        SET @To   = DATEFROMPARTS(@Y, 12, 31);
    END

    SELECT
        CASE @Grain WHEN 'Y' THEN P.YearName WHEN 'Q' THEN P.QuarterName
             ELSE LEFT(DATENAME(MONTH, F.dtPayReceived), 3) + ' ' + DATENAME(YEAR, F.dtPayReceived) END AS GroupByPeriod,
        F.LeadId, F.IRRInclFees, F.MinIRR, F.SalesId, F.Salesperson, F.Division,
        ISNULL(F.AF, 0)                                                    AS AF,
        ISNULL(F.IRRInclFees, 0) * ISNULL(F.AF, 0) * ISNULL(F.Term, 0)     AS Term_AF_IRR,
        ISNULL(F.AF, 0) * ISNULL(F.Term, 0)                                AS Term_AF
    INTO   #LeadData
    FROM   Fact.Lead F
           INNER JOIN Dim.Period P ON P.PeriodId = F.PeriodId
    WHERE  F.IsOB = 1
      AND  YEAR(F.dtPayReceived) > 2021
      AND  (@DivisionId = -1 OR F.DivisionId = @DivisionId)
      AND  F.dtPayReceived BETWEEN @From AND @To;

    -- per salesperson (active salespeople only, as today)
    SELECT
        ROW_NUMBER() OVER (ORDER BY L.Salesperson) AS OrderInt,
        L.GroupByPeriod, L.Division AS SalesDivision, L.Salesperson,
        COUNT(*)                                                                     AS TransactionsTotal,
        SUM(L.AF)                                                                    AS AFTotal,
        CASE WHEN SUM(L.Term_AF) = 0 THEN 0 ELSE SUM(L.Term_AF_IRR) / SUM(L.Term_AF) END AS IRRAverage,
        SUM(CASE WHEN L.IRRInclFees < L.MinIRR THEN 1 ELSE 0 END)                    AS TransactionsUnderMinIRR,
        SUM(CASE WHEN L.IRRInclFees < L.MinIRR THEN L.AF ELSE 0 END)                 AS AFUnderMinIRR
    INTO   #Results
    FROM   #LeadData L
           INNER JOIN Dim.Salesperson S ON S.SalesId = L.SalesId AND S.IsActive = 1
    GROUP BY L.GroupByPeriod, L.Division, L.Salesperson;

    -- the total row: every deal, and the people rows' below-minimum counts (as today)
    INSERT #Results
    SELECT 1000, GroupByPeriod, 'Total', 'Total', COUNT(*), SUM(AF),
           CASE WHEN SUM(Term_AF) = 0 THEN 0 ELSE SUM(Term_AF_IRR) / SUM(Term_AF) END,
           (SELECT SUM(TransactionsUnderMinIRR) FROM #Results),
           (SELECT SUM(AFUnderMinIRR) FROM #Results)
    FROM   #LeadData
    GROUP BY GroupByPeriod;

    SELECT OrderInt, GroupByPeriod, SalesDivision, Salesperson, TransactionsTotal,
           CAST(AFTotal AS decimal(18,0))                    AS AFTotal,
           CAST(IRRAverage AS decimal(18,4))                 AS IRRAverage,
           TransactionsUnderMinIRR,
           CAST(AFUnderMinIRR AS decimal(18,0))              AS AFUnderMinIRR,
           CAST(CASE WHEN TransactionsTotal > 0 THEN TransactionsUnderMinIRR * 1.0 / TransactionsTotal END AS decimal(18,4)) AS PercentTransactionsUnderMinIRR,
           CAST(CASE WHEN AFTotal > 0 THEN AFUnderMinIRR / AFTotal END AS decimal(18,4))                                     AS PercentAFUnderMinIRR
    FROM   #Results
    ORDER BY OrderInt;

    COMMIT TRANSACTION;
END

""");
        }
    }
}
