using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>
    /// Fund Capacity: how much was drawn from each lender in the last 3 months (26 Nov 2026, Steve) - from Fact.FundingLine,
    /// already on the site, so no change to CMS's views or the export. Rpt.spFundCapacity gains Drawn3m.
    /// </summary>
    public partial class FundCapacityDrawn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spFundCapacity
(
    @List varchar(10) = 'Lenders'   -- 'Lenders': one row per active block lender; 'Funds': their live funds
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ReportDate date = (SELECT ReportDate FROM Ref.Setting WHERE SettingId = 1);

    -- the live funds that count towards capacity, as CMS's Own Book > Fund Lender Capacity: not excluded, money out;
    -- a block counts at its capacity percentage, an RCF or IWFA in full
    SELECT F.FundId, F.FundName, F.FundLenderId, F.FundType, F.StartDate, F.EndDate, F.BlockAmount, F.CapitalOS,
           F.FacilityLimit, F.CurrentRate, F.ServiceFeeRate,
           CASE WHEN F.FundType = 'Block' THEN ROUND(F.CapitalOS * ISNULL(F.ExcludeFromCapacityPercent, 1), 2)
                ELSE F.CapitalOS END AS Exposure
    INTO   #Live
    FROM   Dim.Fund F
    WHERE  F.ExcludeFromCapacity = 0 AND ISNULL(F.CapitalOS, 0) <> 0;

    IF @List = 'Funds'
    BEGIN
        SELECT L.FundLenderId, L.FundName, L.FundType, L.EndDate, L.BlockAmount, L.CapitalOS
        FROM   #Live L
               INNER JOIN Dim.FundLender FL ON FL.FundLenderId = L.FundLenderId AND FL.IsActiveBlockLender = 1
        ORDER BY L.FundLenderId, L.FundType, L.EndDate DESC, L.FundName;
        RETURN;
    END

    -- the lenders CMS lists (its "active block lender" tick), each with:
    --   Limit    its own capital limit; when it has none, its live RCF's / IWFA's own limit (Conister); else none
    --   BuyRate  the rate for NEW funding, a fraction (0.0775 = 7.75%): a live RCF's or IWFA's current rate (+ the
    --            IWFA's service fee), else the latest block's rate
    SELECT FL.FundLenderId,
           FL.FundLenderName                                                   AS LenderName,
           CONCAT_WS(', ', CASE WHEN T.HasBlock = 1 THEN 'Block' END,
                           CASE WHEN T.HasIWFA  = 1 THEN 'IWFA'  END,
                           CASE WHEN T.HasRCF   = 1 THEN 'RCF'   END)           AS FundType,
           CAST(CASE WHEN FL.CapitalLimit > 0 THEN FL.CapitalLimit
                     ELSE NULLIF(FAC.FacilityLimit, 0) END AS decimal(18,2))   AS Limit,
           CAST(ISNULL(T.Capital, 0) AS decimal(18,2))                          AS Capital,
           CAST(ISNULL(T.Exposure, 0) AS decimal(18,2))                         AS Exposure,
           CAST(COALESCE(FAC.Rate, BLK.Rate) AS decimal(12,8))                  AS BuyRate,
           CAST(ISNULL(DRW.Drawn, 0) AS decimal(18,2))                          AS Drawn3m
    FROM   Dim.FundLender FL
           OUTER APPLY (SELECT SUM(L.CapitalOS) AS Capital, SUM(L.Exposure) AS Exposure,
                               MAX(CASE WHEN L.FundType = 'Block' THEN 1 ELSE 0 END) AS HasBlock,
                               MAX(CASE WHEN L.FundType = 'IWFA'  THEN 1 ELSE 0 END) AS HasIWFA,
                               MAX(CASE WHEN L.FundType = 'RCF'   THEN 1 ELSE 0 END) AS HasRCF
                        FROM   #Live L WHERE L.FundLenderId = FL.FundLenderId) T
           OUTER APPLY (SELECT SUM(L.FacilityLimit) AS FacilityLimit,
                               (SELECT TOP 1 L2.CurrentRate + ISNULL(L2.ServiceFeeRate, 0)
                                FROM   #Live L2
                                WHERE  L2.FundLenderId = FL.FundLenderId AND L2.FundType IN ('RCF', 'IWFA')
                                ORDER BY L2.StartDate DESC, L2.FundId DESC) AS Rate
                        FROM   #Live L WHERE L.FundLenderId = FL.FundLenderId AND L.FundType IN ('RCF', 'IWFA')) FAC
           OUTER APPLY (SELECT TOP 1 F.CurrentRate AS Rate
                        FROM   Dim.Fund F
                        WHERE  F.FundLenderId = FL.FundLenderId AND F.FundType = 'Block'
                          AND  F.StartDate <= CAST(GETDATE() AS date)
                        ORDER BY F.StartDate DESC, F.FundId DESC) BLK
           -- drawn in the 3 months to the report date (26 Nov 2026, Steve): new blocks at their amount, deals moved onto an
           -- RCF at the capital moved (not the original bulk moves), IWFA cohorts at their capital - Fact.FundingLine, as
           -- Margin Analysis
           OUTER APPLY (SELECT SUM(FLN.Amount) AS Drawn
                        FROM   Fact.FundingLine FLN
                               INNER JOIN Dim.Fund F ON F.FundId = FLN.SourceId
                        WHERE  F.FundLenderId = FL.FundLenderId AND FLN.IsBulkMove = 0
                          AND  FLN.EventDate >  DATEADD(MONTH, -3, @ReportDate)
                          AND  FLN.EventDate <= @ReportDate) DRW
    WHERE  FL.IsActiveBlockLender = 1
    ORDER BY FL.FundLenderName;
END
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spFundCapacity
(
    @List varchar(10) = 'Lenders'   -- 'Lenders': one row per active block lender; 'Funds': their live funds
)
AS
BEGIN
    SET NOCOUNT ON;

    -- the live funds that count towards capacity, as CMS's Own Book > Fund Lender Capacity: not excluded, money out;
    -- a block counts at its capacity percentage, an RCF or IWFA in full
    SELECT F.FundId, F.FundName, F.FundLenderId, F.FundType, F.StartDate, F.EndDate, F.BlockAmount, F.CapitalOS,
           F.FacilityLimit, F.CurrentRate, F.ServiceFeeRate,
           CASE WHEN F.FundType = 'Block' THEN ROUND(F.CapitalOS * ISNULL(F.ExcludeFromCapacityPercent, 1), 2)
                ELSE F.CapitalOS END AS Exposure
    INTO   #Live
    FROM   Dim.Fund F
    WHERE  F.ExcludeFromCapacity = 0 AND ISNULL(F.CapitalOS, 0) <> 0;

    IF @List = 'Funds'
    BEGIN
        SELECT L.FundLenderId, L.FundName, L.FundType, L.EndDate, L.BlockAmount, L.CapitalOS
        FROM   #Live L
               INNER JOIN Dim.FundLender FL ON FL.FundLenderId = L.FundLenderId AND FL.IsActiveBlockLender = 1
        ORDER BY L.FundLenderId, L.FundType, L.EndDate DESC, L.FundName;
        RETURN;
    END

    -- the lenders CMS lists (its "active block lender" tick), each with:
    --   Limit    its own capital limit; when it has none, its live RCF's / IWFA's own limit (Conister); else none
    --   BuyRate  the rate for NEW funding, a fraction (0.0775 = 7.75%): a live RCF's or IWFA's current rate (+ the
    --            IWFA's service fee), else the latest block's rate
    SELECT FL.FundLenderId,
           FL.FundLenderName                                                   AS LenderName,
           CONCAT_WS(', ', CASE WHEN T.HasBlock = 1 THEN 'Block' END,
                           CASE WHEN T.HasIWFA  = 1 THEN 'IWFA'  END,
                           CASE WHEN T.HasRCF   = 1 THEN 'RCF'   END)           AS FundType,
           CAST(CASE WHEN FL.CapitalLimit > 0 THEN FL.CapitalLimit
                     ELSE NULLIF(FAC.FacilityLimit, 0) END AS decimal(18,2))   AS Limit,
           CAST(ISNULL(T.Capital, 0) AS decimal(18,2))                          AS Capital,
           CAST(ISNULL(T.Exposure, 0) AS decimal(18,2))                         AS Exposure,
           CAST(COALESCE(FAC.Rate, BLK.Rate) AS decimal(12,8))                  AS BuyRate
    FROM   Dim.FundLender FL
           OUTER APPLY (SELECT SUM(L.CapitalOS) AS Capital, SUM(L.Exposure) AS Exposure,
                               MAX(CASE WHEN L.FundType = 'Block' THEN 1 ELSE 0 END) AS HasBlock,
                               MAX(CASE WHEN L.FundType = 'IWFA'  THEN 1 ELSE 0 END) AS HasIWFA,
                               MAX(CASE WHEN L.FundType = 'RCF'   THEN 1 ELSE 0 END) AS HasRCF
                        FROM   #Live L WHERE L.FundLenderId = FL.FundLenderId) T
           OUTER APPLY (SELECT SUM(L.FacilityLimit) AS FacilityLimit,
                               (SELECT TOP 1 L2.CurrentRate + ISNULL(L2.ServiceFeeRate, 0)
                                FROM   #Live L2
                                WHERE  L2.FundLenderId = FL.FundLenderId AND L2.FundType IN ('RCF', 'IWFA')
                                ORDER BY L2.StartDate DESC, L2.FundId DESC) AS Rate
                        FROM   #Live L WHERE L.FundLenderId = FL.FundLenderId AND L.FundType IN ('RCF', 'IWFA')) FAC
           OUTER APPLY (SELECT TOP 1 F.CurrentRate AS Rate
                        FROM   Dim.Fund F
                        WHERE  F.FundLenderId = FL.FundLenderId AND F.FundType = 'Block'
                          AND  F.StartDate <= CAST(GETDATE() AS date)
                        ORDER BY F.StartDate DESC, F.FundId DESC) BLK
    WHERE  FL.IsActiveBlockLender = 1
    ORDER BY FL.FundLenderName;
END
""");
        }
    }
}