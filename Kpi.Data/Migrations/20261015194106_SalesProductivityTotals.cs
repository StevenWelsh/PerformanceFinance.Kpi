using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>
    /// Rpt.spSalesProductivity for one salesperson: the row numbers and the all / others totals are worked out across
    /// every salesperson first, then narrowed to the one asked for, as WebKPI.spSalesProductivity does. Before this a
    /// single salesperson's "all salespeople" total was their own.
    /// </summary>
    public partial class SalesProductivityTotals : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spSalesProductivity
(
    @SalespersonId int = -1
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Year int = YEAR(Rpt.fnReportDate());

    -- statuses still waiting for approval, and approved but waiting for activation
    DECLARE @AwaitingApproval   TABLE (LeadStatus nvarchar(50) PRIMARY KEY);
    DECLARE @AwaitingActivation TABLE (LeadStatus nvarchar(50) PRIMARY KEY);
    INSERT @AwaitingApproval VALUES ('LEAD'), ('PROPOSAL'), ('PROPOSAL ACCEPTED'), ('APPLICATION');
    INSERT @AwaitingActivation VALUES ('APPLICATION ACCEPTED'), ('CLIENT APPROVAL LETTER'), ('INVOICE REQUEST LETTER'),
        ('INVOICE RECEIVED'), ('DOCUMENTS SENT'), ('DOCUMENTS SIGNED'), ('PRE-LEASE'), ('PRE/PART PAYMENT'), ('SENT FOR PAYMENT');

    WITH Years AS
    (
        SELECT DISTINCT YearNumber AS Yr FROM Dim.Period WHERE YearNumber > @Year - 4
    ),
    People AS          -- the reportable salespeople x those years
    (
        SELECT DISTINCT SP.SalesId, SP.Salesperson, Y.Yr
        FROM   Dim.Salesperson SP
               INNER JOIN Cfg.SalespersonScope SC ON SC.SalesId = SP.SalesId AND SC.InProductivity = 1
               INNER JOIN Years Y ON Y.Yr >= SC.FromYear AND (SC.ToYear IS NULL OR Y.Yr <= SC.ToYear)
    ),
    Leads AS           -- by the year the lead came in
    (
        SELECT
            L.SalesId, YEAR(L.dtLead) AS Yr,
            SUM(L.AF) AS TotalLead_AF, COUNT(*) AS TotalLead_Count,
            SUM(CASE WHEN L.IntroducerId IS NULL THEN 1 ELSE 0 END)    AS NonIntroduced_Count,
            SUM(CASE WHEN L.IntroducerId IS NULL THEN L.AF ELSE 0 END) AS NonIntroduced_AF,
            SUM(CASE WHEN L.IntroducerManagedByHQ = 1 THEN L.AF ELSE 0 END) AS HQLead_AF,
            SUM(CASE WHEN L.IntroducerManagedByHQ = 1 THEN 1 ELSE 0 END)    AS HQLead_Count,
            SUM(CASE WHEN L.IntroducerManagedByHQ = 0 AND ISNULL(L.IntroducerAccountManagerId, 0) =  L.SalesId THEN L.AF ELSE 0 END) AS Self_ThisSales_AF,
            SUM(CASE WHEN L.IntroducerManagedByHQ = 0 AND ISNULL(L.IntroducerAccountManagerId, 0) =  L.SalesId THEN 1 ELSE 0 END)    AS Self_ThisSales_Count,
            SUM(CASE WHEN L.IntroducerManagedByHQ = 0 AND ISNULL(L.IntroducerAccountManagerId, 0) <> L.SalesId THEN L.AF ELSE 0 END) AS Self_OtherSales_AF,
            SUM(CASE WHEN L.IntroducerManagedByHQ = 0 AND ISNULL(L.IntroducerAccountManagerId, 0) <> L.SalesId THEN 1 ELSE 0 END)    AS Self_OtherSales_Count,
            SUM(CASE WHEN AA.LeadStatus IS NOT NULL THEN 1 ELSE 0 END)    AS AWApproval_AsSubset_Count,
            SUM(CASE WHEN AA.LeadStatus IS NOT NULL THEN L.AF ELSE 0 END) AS AWApproval_AsSubset_AF,
            SUM(CASE WHEN L.dtAppAccept IS NOT NULL THEN 1 ELSE 0 END)    AS Approved_AsSubset_Count,
            SUM(CASE WHEN AV.LeadStatus IS NOT NULL THEN 1 ELSE 0 END)    AS AWActivation_AsSubset_Count,
            SUM(CASE WHEN AV.LeadStatus IS NOT NULL THEN L.AF ELSE 0 END) AS AWActivation_AsSubset_AF,
            SUM(CASE WHEN L.dtActivation IS NOT NULL THEN 1 ELSE 0 END)    AS Activated_AsSubset_Count,
            SUM(CASE WHEN L.dtActivation IS NOT NULL THEN L.AF ELSE 0 END) AS Activated_AsSubset_AF
        FROM   Fact.LeadPipeline L
               LEFT JOIN @AwaitingApproval   AA ON AA.LeadStatus = L.LeadStatus
               LEFT JOIN @AwaitingActivation AV ON AV.LeadStatus = L.LeadStatus
        WHERE  YEAR(L.dtLead) > @Year - 4
        GROUP BY L.SalesId, YEAR(L.dtLead)
    ),
    Approved AS        -- by the year of approval
    (
        SELECT SalesId, YEAR(dtAppAccept) AS Yr, COUNT(*) AS Approved_Year_Count
        FROM   Fact.LeadPipeline WHERE YEAR(dtAppAccept) > @Year - 4
        GROUP BY SalesId, YEAR(dtAppAccept)
    ),
    Activated AS       -- by the year of activation
    (
        SELECT SalesId, YEAR(dtActivation) AS Yr, COUNT(*) AS Activated_Year_Count,
               SUM(AF) AS Activated_Year_AF, SUM(GFI) AS Activated_Year_GFI
        FROM   Fact.LeadPipeline WHERE YEAR(dtActivation) > @Year - 4
        GROUP BY SalesId, YEAR(dtActivation)
    ),
    ForOthers AS       -- leads their own suppliers brought in for colleagues
    (
        SELECT IntroducerAccountManagerId AS SalesId, YEAR(dtLead) AS Yr, SUM(AF) AS AF
        FROM   Fact.LeadPipeline
        WHERE  IntroducerId IS NOT NULL
          AND  ISNULL(IntroducerManagedByHQ, 0) = 0
          AND  SalesId <> IntroducerAccountManagerId
          AND  YEAR(dtLead) > @Year - 4
        GROUP BY IntroducerAccountManagerId, YEAR(dtLead)
    ),
    R AS
    (
        SELECT
            P.SalesId, P.Salesperson, P.Yr,
            ISNULL(L.TotalLead_AF, 0) AS TotalLead_AF,                 ISNULL(L.TotalLead_Count, 0) AS TotalLead_Count,
            ISNULL(L.NonIntroduced_AF, 0) AS NonIntroduced_AF,         ISNULL(L.NonIntroduced_Count, 0) AS NonIntroduced_Count,
            ISNULL(L.HQLead_AF, 0) AS HQLead_AF,                       ISNULL(L.HQLead_Count, 0) AS HQLead_Count,
            ISNULL(L.Self_ThisSales_AF, 0) AS Self_ThisSales_AF,       ISNULL(L.Self_ThisSales_Count, 0) AS Self_ThisSales_Count,
            ISNULL(L.Self_OtherSales_AF, 0) AS Self_OtherSales_AF,     ISNULL(L.Self_OtherSales_Count, 0) AS Self_OtherSales_Count,
            ISNULL(L.AWApproval_AsSubset_AF, 0) AS AWApproval_AsSubset_AF,
            ISNULL(L.AWApproval_AsSubset_Count, 0) AS AWApproval_AsSubset_Count,
            ISNULL(A.Approved_Year_Count, 0) AS Approved_Year_Count,
            ISNULL(L.Approved_AsSubset_Count, 0) AS Approved_AsSubset_Count,
            ISNULL(L.AWActivation_AsSubset_AF, 0) AS AWActivation_AsSubset_AF,
            ISNULL(L.AWActivation_AsSubset_Count, 0) AS AWActivation_AsSubset_Count,
            ISNULL(V.Activated_Year_AF, 0) AS Activated_Year_AF,       ISNULL(V.Activated_Year_Count, 0) AS Activated_Year_Count,
            ISNULL(V.Activated_Year_GFI, 0) AS Activated_Year_GFI,
            ISNULL(L.Activated_AsSubset_AF, 0) AS Activated_AsSubset_AF,
            ISNULL(L.Activated_AsSubset_Count, 0) AS Activated_AsSubset_Count,
            ISNULL(O.AF, 0) AS GeneratedForOthers_Year_AF
        FROM   People P
               LEFT JOIN Leads     L ON L.SalesId = P.SalesId AND L.Yr = P.Yr
               LEFT JOIN Approved  A ON A.SalesId = P.SalesId AND A.Yr = P.Yr
               LEFT JOIN Activated V ON V.SalesId = P.SalesId AND V.Yr = P.Yr
               LEFT JOIN ForOthers O ON O.SalesId = P.SalesId AND O.Yr = P.Yr
    ),
    -- numbered and totalled across EVERY salesperson first; only then narrowed to the one asked for
    Final AS
    (
    SELECT
        CAST(ROW_NUMBER() OVER (ORDER BY R.Salesperson, R.Yr DESC) AS int) AS Id,
        R.SalesId, R.Salesperson, R.Yr AS [Year],
        R.TotalLead_AF, R.TotalLead_Count, R.NonIntroduced_AF, R.NonIntroduced_Count, R.HQLead_AF, R.HQLead_Count,
        R.Self_ThisSales_AF, R.Self_ThisSales_Count, R.Self_OtherSales_AF, R.Self_OtherSales_Count,
        CAST(CASE WHEN R.TotalLead_AF = 0 THEN 0 ELSE (R.NonIntroduced_AF + R.Self_ThisSales_AF) / (R.TotalLead_AF / 1.0000) END AS decimal(18,2)) AS SelfGen_Percent_AF,
        CAST(CASE WHEN R.TotalLead_Count = 0 THEN 0 ELSE (R.NonIntroduced_Count + R.Self_ThisSales_Count) / (R.TotalLead_Count / 1.0000) END AS decimal(18,2)) AS SelfGen_Percent_Count,
        CAST(CASE WHEN R.TotalLead_AF = 0 THEN 0 ELSE R.HQLead_AF / (R.TotalLead_AF / 1.0000) END AS decimal(18,2)) AS HQ_Percent_AF,
        CAST(CASE WHEN R.TotalLead_Count = 0 THEN 0 ELSE R.HQLead_Count / (R.TotalLead_Count / 1.0000) END AS decimal(18,2)) AS HQ_Percent_Count,
        CAST(CASE WHEN R.TotalLead_AF = 0 THEN 0 ELSE R.Self_OtherSales_AF / (R.TotalLead_AF / 1.0000) END AS decimal(18,2)) AS OtherSales_Percent_AF,
        CAST(CASE WHEN R.TotalLead_Count = 0 THEN 0 ELSE R.Self_OtherSales_Count / (R.TotalLead_Count / 1.0000) END AS decimal(18,2)) AS OtherSales_Percent_Count,
        R.AWApproval_AsSubset_AF, R.AWApproval_AsSubset_Count, R.Approved_Year_Count, R.Approved_AsSubset_Count,
        CAST(CASE WHEN R.TotalLead_Count - R.AWApproval_AsSubset_Count = 0 THEN 0
                  ELSE R.Approved_AsSubset_Count / ((R.TotalLead_Count - R.AWApproval_AsSubset_Count) / 1.0000) END AS decimal(18,2)) AS L_APR_Percent_Count,
        R.AWActivation_AsSubset_Count, R.AWActivation_AsSubset_AF,
        R.Activated_Year_Count, R.Activated_Year_AF, R.Activated_Year_GFI,
        R.Activated_AsSubset_Count, R.Activated_AsSubset_AF,
        CAST(CASE WHEN R.TotalLead_AF - R.AWApproval_AsSubset_AF - R.AWActivation_AsSubset_AF = 0 THEN 0
                  ELSE R.Activated_AsSubset_AF / ((R.TotalLead_AF - R.AWApproval_AsSubset_AF - R.AWActivation_AsSubset_AF) / 1.0000) END AS decimal(18,2)) AS L_ACT_Percent_AF,
        CAST(CASE WHEN R.TotalLead_Count - R.AWApproval_AsSubset_Count - R.AWActivation_AsSubset_Count = 0 THEN 0
                  ELSE R.Activated_AsSubset_Count / ((R.TotalLead_Count - R.AWApproval_AsSubset_Count - R.AWActivation_AsSubset_Count) / 1.0000) END AS decimal(18,2)) AS L_ACT_Percent_Count,
        CAST(CASE WHEN R.Approved_AsSubset_Count - R.AWActivation_AsSubset_Count = 0 THEN 0
                  ELSE R.Activated_AsSubset_Count / ((R.Approved_AsSubset_Count - R.AWActivation_AsSubset_Count) / 1.0000) END AS decimal(18,2)) AS APR_ACT_Percent_Count,
        R.GeneratedForOthers_Year_AF,
        SUM(R.TotalLead_AF) OVER (PARTITION BY R.Yr)                   AS TotalLeadAll_AF,
        SUM(R.TotalLead_AF) OVER (PARTITION BY R.Yr) - R.TotalLead_AF  AS TotalLeadOthers_AF,
        CAST(CASE WHEN SUM(R.TotalLead_AF) OVER (PARTITION BY R.Yr) - R.TotalLead_AF = 0 THEN 0
                  ELSE R.GeneratedForOthers_Year_AF / (SUM(R.TotalLead_AF) OVER (PARTITION BY R.Yr) - R.TotalLead_AF) END AS decimal(18,2)) AS GenForOthers_Percent_AF
    FROM   R
    )
    SELECT *
    FROM   Final
    WHERE  @SalespersonId = -1 OR SalesId = @SalespersonId
    ORDER BY Salesperson, [Year] DESC;
END
""");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spSalesProductivity
(
    @SalespersonId int = -1
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Year int = YEAR(Rpt.fnReportDate());

    -- statuses still waiting for approval, and approved but waiting for activation
    DECLARE @AwaitingApproval   TABLE (LeadStatus nvarchar(50) PRIMARY KEY);
    DECLARE @AwaitingActivation TABLE (LeadStatus nvarchar(50) PRIMARY KEY);
    INSERT @AwaitingApproval VALUES ('LEAD'), ('PROPOSAL'), ('PROPOSAL ACCEPTED'), ('APPLICATION');
    INSERT @AwaitingActivation VALUES ('APPLICATION ACCEPTED'), ('CLIENT APPROVAL LETTER'), ('INVOICE REQUEST LETTER'),
        ('INVOICE RECEIVED'), ('DOCUMENTS SENT'), ('DOCUMENTS SIGNED'), ('PRE-LEASE'), ('PRE/PART PAYMENT'), ('SENT FOR PAYMENT');

    WITH Years AS
    (
        SELECT DISTINCT YearNumber AS Yr FROM Dim.Period WHERE YearNumber > @Year - 4
    ),
    People AS          -- the reportable salespeople x those years
    (
        SELECT DISTINCT SP.SalesId, SP.Salesperson, Y.Yr
        FROM   Dim.Salesperson SP
               INNER JOIN Cfg.SalespersonScope SC ON SC.SalesId = SP.SalesId AND SC.InProductivity = 1
               INNER JOIN Years Y ON Y.Yr >= SC.FromYear AND (SC.ToYear IS NULL OR Y.Yr <= SC.ToYear)
    ),
    Leads AS           -- by the year the lead came in
    (
        SELECT
            L.SalesId, YEAR(L.dtLead) AS Yr,
            SUM(L.AF) AS TotalLead_AF, COUNT(*) AS TotalLead_Count,
            SUM(CASE WHEN L.IntroducerId IS NULL THEN 1 ELSE 0 END)    AS NonIntroduced_Count,
            SUM(CASE WHEN L.IntroducerId IS NULL THEN L.AF ELSE 0 END) AS NonIntroduced_AF,
            SUM(CASE WHEN L.IntroducerManagedByHQ = 1 THEN L.AF ELSE 0 END) AS HQLead_AF,
            SUM(CASE WHEN L.IntroducerManagedByHQ = 1 THEN 1 ELSE 0 END)    AS HQLead_Count,
            SUM(CASE WHEN L.IntroducerManagedByHQ = 0 AND ISNULL(L.IntroducerAccountManagerId, 0) =  L.SalesId THEN L.AF ELSE 0 END) AS Self_ThisSales_AF,
            SUM(CASE WHEN L.IntroducerManagedByHQ = 0 AND ISNULL(L.IntroducerAccountManagerId, 0) =  L.SalesId THEN 1 ELSE 0 END)    AS Self_ThisSales_Count,
            SUM(CASE WHEN L.IntroducerManagedByHQ = 0 AND ISNULL(L.IntroducerAccountManagerId, 0) <> L.SalesId THEN L.AF ELSE 0 END) AS Self_OtherSales_AF,
            SUM(CASE WHEN L.IntroducerManagedByHQ = 0 AND ISNULL(L.IntroducerAccountManagerId, 0) <> L.SalesId THEN 1 ELSE 0 END)    AS Self_OtherSales_Count,
            SUM(CASE WHEN AA.LeadStatus IS NOT NULL THEN 1 ELSE 0 END)    AS AWApproval_AsSubset_Count,
            SUM(CASE WHEN AA.LeadStatus IS NOT NULL THEN L.AF ELSE 0 END) AS AWApproval_AsSubset_AF,
            SUM(CASE WHEN L.dtAppAccept IS NOT NULL THEN 1 ELSE 0 END)    AS Approved_AsSubset_Count,
            SUM(CASE WHEN AV.LeadStatus IS NOT NULL THEN 1 ELSE 0 END)    AS AWActivation_AsSubset_Count,
            SUM(CASE WHEN AV.LeadStatus IS NOT NULL THEN L.AF ELSE 0 END) AS AWActivation_AsSubset_AF,
            SUM(CASE WHEN L.dtActivation IS NOT NULL THEN 1 ELSE 0 END)    AS Activated_AsSubset_Count,
            SUM(CASE WHEN L.dtActivation IS NOT NULL THEN L.AF ELSE 0 END) AS Activated_AsSubset_AF
        FROM   Fact.LeadPipeline L
               LEFT JOIN @AwaitingApproval   AA ON AA.LeadStatus = L.LeadStatus
               LEFT JOIN @AwaitingActivation AV ON AV.LeadStatus = L.LeadStatus
        WHERE  YEAR(L.dtLead) > @Year - 4
        GROUP BY L.SalesId, YEAR(L.dtLead)
    ),
    Approved AS        -- by the year of approval
    (
        SELECT SalesId, YEAR(dtAppAccept) AS Yr, COUNT(*) AS Approved_Year_Count
        FROM   Fact.LeadPipeline WHERE YEAR(dtAppAccept) > @Year - 4
        GROUP BY SalesId, YEAR(dtAppAccept)
    ),
    Activated AS       -- by the year of activation
    (
        SELECT SalesId, YEAR(dtActivation) AS Yr, COUNT(*) AS Activated_Year_Count,
               SUM(AF) AS Activated_Year_AF, SUM(GFI) AS Activated_Year_GFI
        FROM   Fact.LeadPipeline WHERE YEAR(dtActivation) > @Year - 4
        GROUP BY SalesId, YEAR(dtActivation)
    ),
    ForOthers AS       -- leads their own suppliers brought in for colleagues
    (
        SELECT IntroducerAccountManagerId AS SalesId, YEAR(dtLead) AS Yr, SUM(AF) AS AF
        FROM   Fact.LeadPipeline
        WHERE  IntroducerId IS NOT NULL
          AND  ISNULL(IntroducerManagedByHQ, 0) = 0
          AND  SalesId <> IntroducerAccountManagerId
          AND  YEAR(dtLead) > @Year - 4
        GROUP BY IntroducerAccountManagerId, YEAR(dtLead)
    ),
    R AS
    (
        SELECT
            P.SalesId, P.Salesperson, P.Yr,
            ISNULL(L.TotalLead_AF, 0) AS TotalLead_AF,                 ISNULL(L.TotalLead_Count, 0) AS TotalLead_Count,
            ISNULL(L.NonIntroduced_AF, 0) AS NonIntroduced_AF,         ISNULL(L.NonIntroduced_Count, 0) AS NonIntroduced_Count,
            ISNULL(L.HQLead_AF, 0) AS HQLead_AF,                       ISNULL(L.HQLead_Count, 0) AS HQLead_Count,
            ISNULL(L.Self_ThisSales_AF, 0) AS Self_ThisSales_AF,       ISNULL(L.Self_ThisSales_Count, 0) AS Self_ThisSales_Count,
            ISNULL(L.Self_OtherSales_AF, 0) AS Self_OtherSales_AF,     ISNULL(L.Self_OtherSales_Count, 0) AS Self_OtherSales_Count,
            ISNULL(L.AWApproval_AsSubset_AF, 0) AS AWApproval_AsSubset_AF,
            ISNULL(L.AWApproval_AsSubset_Count, 0) AS AWApproval_AsSubset_Count,
            ISNULL(A.Approved_Year_Count, 0) AS Approved_Year_Count,
            ISNULL(L.Approved_AsSubset_Count, 0) AS Approved_AsSubset_Count,
            ISNULL(L.AWActivation_AsSubset_AF, 0) AS AWActivation_AsSubset_AF,
            ISNULL(L.AWActivation_AsSubset_Count, 0) AS AWActivation_AsSubset_Count,
            ISNULL(V.Activated_Year_AF, 0) AS Activated_Year_AF,       ISNULL(V.Activated_Year_Count, 0) AS Activated_Year_Count,
            ISNULL(V.Activated_Year_GFI, 0) AS Activated_Year_GFI,
            ISNULL(L.Activated_AsSubset_AF, 0) AS Activated_AsSubset_AF,
            ISNULL(L.Activated_AsSubset_Count, 0) AS Activated_AsSubset_Count,
            ISNULL(O.AF, 0) AS GeneratedForOthers_Year_AF
        FROM   People P
               LEFT JOIN Leads     L ON L.SalesId = P.SalesId AND L.Yr = P.Yr
               LEFT JOIN Approved  A ON A.SalesId = P.SalesId AND A.Yr = P.Yr
               LEFT JOIN Activated V ON V.SalesId = P.SalesId AND V.Yr = P.Yr
               LEFT JOIN ForOthers O ON O.SalesId = P.SalesId AND O.Yr = P.Yr
    )
    SELECT
        CAST(ROW_NUMBER() OVER (ORDER BY R.Salesperson, R.Yr DESC) AS int) AS Id,
        R.SalesId, R.Salesperson, R.Yr AS [Year],
        R.TotalLead_AF, R.TotalLead_Count, R.NonIntroduced_AF, R.NonIntroduced_Count, R.HQLead_AF, R.HQLead_Count,
        R.Self_ThisSales_AF, R.Self_ThisSales_Count, R.Self_OtherSales_AF, R.Self_OtherSales_Count,
        CAST(CASE WHEN R.TotalLead_AF = 0 THEN 0 ELSE (R.NonIntroduced_AF + R.Self_ThisSales_AF) / (R.TotalLead_AF / 1.0000) END AS decimal(18,2)) AS SelfGen_Percent_AF,
        CAST(CASE WHEN R.TotalLead_Count = 0 THEN 0 ELSE (R.NonIntroduced_Count + R.Self_ThisSales_Count) / (R.TotalLead_Count / 1.0000) END AS decimal(18,2)) AS SelfGen_Percent_Count,
        CAST(CASE WHEN R.TotalLead_AF = 0 THEN 0 ELSE R.HQLead_AF / (R.TotalLead_AF / 1.0000) END AS decimal(18,2)) AS HQ_Percent_AF,
        CAST(CASE WHEN R.TotalLead_Count = 0 THEN 0 ELSE R.HQLead_Count / (R.TotalLead_Count / 1.0000) END AS decimal(18,2)) AS HQ_Percent_Count,
        CAST(CASE WHEN R.TotalLead_AF = 0 THEN 0 ELSE R.Self_OtherSales_AF / (R.TotalLead_AF / 1.0000) END AS decimal(18,2)) AS OtherSales_Percent_AF,
        CAST(CASE WHEN R.TotalLead_Count = 0 THEN 0 ELSE R.Self_OtherSales_Count / (R.TotalLead_Count / 1.0000) END AS decimal(18,2)) AS OtherSales_Percent_Count,
        R.AWApproval_AsSubset_AF, R.AWApproval_AsSubset_Count, R.Approved_Year_Count, R.Approved_AsSubset_Count,
        CAST(CASE WHEN R.TotalLead_Count - R.AWApproval_AsSubset_Count = 0 THEN 0
                  ELSE R.Approved_AsSubset_Count / ((R.TotalLead_Count - R.AWApproval_AsSubset_Count) / 1.0000) END AS decimal(18,2)) AS L_APR_Percent_Count,
        R.AWActivation_AsSubset_Count, R.AWActivation_AsSubset_AF,
        R.Activated_Year_Count, R.Activated_Year_AF, R.Activated_Year_GFI,
        R.Activated_AsSubset_Count, R.Activated_AsSubset_AF,
        CAST(CASE WHEN R.TotalLead_AF - R.AWApproval_AsSubset_AF - R.AWActivation_AsSubset_AF = 0 THEN 0
                  ELSE R.Activated_AsSubset_AF / ((R.TotalLead_AF - R.AWApproval_AsSubset_AF - R.AWActivation_AsSubset_AF) / 1.0000) END AS decimal(18,2)) AS L_ACT_Percent_AF,
        CAST(CASE WHEN R.TotalLead_Count - R.AWApproval_AsSubset_Count - R.AWActivation_AsSubset_Count = 0 THEN 0
                  ELSE R.Activated_AsSubset_Count / ((R.TotalLead_Count - R.AWApproval_AsSubset_Count - R.AWActivation_AsSubset_Count) / 1.0000) END AS decimal(18,2)) AS L_ACT_Percent_Count,
        CAST(CASE WHEN R.Approved_AsSubset_Count - R.AWActivation_AsSubset_Count = 0 THEN 0
                  ELSE R.Activated_AsSubset_Count / ((R.Approved_AsSubset_Count - R.AWActivation_AsSubset_Count) / 1.0000) END AS decimal(18,2)) AS APR_ACT_Percent_Count,
        R.GeneratedForOthers_Year_AF,
        SUM(R.TotalLead_AF) OVER (PARTITION BY R.Yr)                   AS TotalLeadAll_AF,
        SUM(R.TotalLead_AF) OVER (PARTITION BY R.Yr) - R.TotalLead_AF  AS TotalLeadOthers_AF,
        CAST(CASE WHEN SUM(R.TotalLead_AF) OVER (PARTITION BY R.Yr) - R.TotalLead_AF = 0 THEN 0
                  ELSE R.GeneratedForOthers_Year_AF / (SUM(R.TotalLead_AF) OVER (PARTITION BY R.Yr) - R.TotalLead_AF) END AS decimal(18,2)) AS GenForOthers_Percent_AF
    FROM   R
    WHERE  @SalespersonId = -1 OR R.SalesId = @SalespersonId
    ORDER BY R.Salesperson, R.Yr DESC;
END
""");
        }
    }
}
