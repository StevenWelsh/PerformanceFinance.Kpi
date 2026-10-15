/*  KPI_Export - a LOCAL stand-in for the KpiExport schema that will live in CMS_Live.

    It lets the KPI push copy real data from CMS_Dev without touching CMS_Dev: this is a database of its
    own, holding only views, one function, the two nightly snapshot tables and the procedure that fills
    them. The views and the procedure are the CMS_Live text word for word; they read CMS_Dev through
    SYNONYMS (named pointers, e.g. Lead.vLead here means CMS_Dev.Lead.vLead), so moving them into CMS later
    is a copy with no edits. The one difference: KpiExport.ConisterRCF_IWFA points at CMS_Dev's
    WebKPI.ConisterRCF_IWFA instead of the table being moved.

    Run once, as yourself, in SSMS or with:
        sqlcmd -S .\SQLEXPRESS -E -i KPI_Export.sql
    Safe to run again: it drops and recreates everything except the database.
    To point it at another copy of CMS, change @CmsDb below and run it again.  */

USE master;
GO
IF DB_ID(N'KPI_Export') IS NULL
    CREATE DATABASE KPI_Export COLLATE Latin1_General_CI_AS;
GO
USE KPI_Export;
GO

-- ================================================================================================
-- clear out a previous run (objects only; there is no data here but last night's snapshots)
-- ================================================================================================
DECLARE @sql nvarchar(max) = N'';
SELECT @sql += N'DROP ' + CASE O.type WHEN 'V' THEN N'VIEW' WHEN 'P' THEN N'PROCEDURE' WHEN 'U' THEN N'TABLE'
                                      WHEN 'SN' THEN N'SYNONYM' ELSE N'FUNCTION' END
             + N' ' + QUOTENAME(SCHEMA_NAME(O.schema_id)) + N'.' + QUOTENAME(O.name) + N';' + CHAR(10)
FROM   sys.objects O
WHERE  O.type IN ('V', 'P', 'U', 'SN', 'FN', 'IF', 'TF')
  AND  O.is_ms_shipped = 0;
EXEC (@sql);
GO

-- ================================================================================================
-- schemas: KpiExport, and the CMS schemas the synonyms sit in
-- ================================================================================================
IF SCHEMA_ID(N'KpiExport') IS NULL EXEC (N'CREATE SCHEMA KpiExport');
IF SCHEMA_ID(N'Lead')      IS NULL EXEC (N'CREATE SCHEMA Lead');
IF SCHEMA_ID(N'CON')       IS NULL EXEC (N'CREATE SCHEMA CON');
IF SCHEMA_ID(N'CMS')       IS NULL EXEC (N'CREATE SCHEMA CMS');
IF SCHEMA_ID(N'OB')        IS NULL EXEC (N'CREATE SCHEMA OB');
IF SCHEMA_ID(N'Reports')   IS NULL EXEC (N'CREATE SCHEMA Reports');
GO

-- ================================================================================================
-- synonyms: each CMS object the export reads, pointing at the same object in @CmsDb
-- ================================================================================================
DECLARE @CmsDb sysname = N'CMS_Dev';

IF DB_ID(@CmsDb) IS NULL
    THROW 50001, 'The CMS database named in @CmsDb does not exist on this server.', 1;

DECLARE @sql nvarchar(max) = N'';
SELECT @sql += N'CREATE SYNONYM ' + QUOTENAME(S.LocalSchema) + N'.' + QUOTENAME(S.ObjectName)
             + N' FOR ' + QUOTENAME(@CmsDb) + N'.' + QUOTENAME(S.CmsSchema) + N'.' + QUOTENAME(S.ObjectName) + N';' + CHAR(10)
FROM   (VALUES
    (N'Lead', N'vLead', N'Lead'),
    (N'Lead', N'Lead', N'Lead'),
    (N'Lead', N'vLeadWithFinancials', N'Lead'),
    (N'Lead', N'Equipment', N'Lead'),
    (N'Lead', N'vEquipment', N'Lead'),
    (N'Lead', N'LeadCategory', N'Lead'),
    (N'dbo', N'Company', N'dbo'),
    (N'dbo', N'param', N'dbo'),
    (N'dbo', N'KPIWorkingDays', N'dbo'),
    (N'dbo', N'OBRCFInterestRate', N'dbo'),
    (N'dbo', N'GetEffectiveMinIRR', N'dbo'),
    (N'CON', N'Contact', N'CON'),
    (N'CMS', N'Address', N'CMS'),
    (N'CMS', N'CMSYear', N'CMS'),
    (N'CMS', N'CMSQuarter', N'CMS'),
    (N'CMS', N'CMSPeriod', N'CMS'),
    (N'CMS', N'Introducer', N'CMS'),
    (N'CMS', N'LookupCode', N'CMS'),
    (N'CMS', N'SalesPerson', N'CMS'),
    (N'CMS', N'SalesOffice', N'CMS'),
    (N'CMS', N'SalesTargetYear', N'CMS'),
    (N'OB', N'Lead', N'OB'),
    (N'OB', N'Block', N'OB'),
    (N'OB', N'RCF', N'OB'),
    (N'OB', N'ScheduleHeader', N'OB'),
    (N'OB', N'Schedule', N'OB'),
    (N'OB', N'IWFACohort', N'OB'),
    (N'OB', N'IWFALead', N'OB'),
    (N'OB', N'FundLender', N'OB'),
    (N'OB', N'Fund', N'OB'),
    (N'OB', N'AnnualBadDebtList', N'OB'),
    (N'Reports', N'Exposure', N'Reports'),
    (N'KpiExport', N'ConisterRCF_IWFA', N'WebKPI')
       ) S (LocalSchema, ObjectName, CmsSchema);
EXEC (@sql);
GO

-- ================================================================================================
-- KpiExport.fnReportDate  (function)
-- The business 'as at' date: today, or 31 December when CMS's current year is set to a past year. The loader stamps it into CMS_KPI on every load, so the KPI database never asks CMS.
-- ================================================================================================
CREATE FUNCTION KpiExport.fnReportDate ()
RETURNS date
AS
BEGIN
    DECLARE @CurrentYear int;

    SELECT @CurrentYear = Y.Number
    FROM   dbo.param P
           INNER JOIN CMS.CMSYear Y ON Y.Id = P.CurrentYearId;

    RETURN CASE WHEN @CurrentYear = YEAR(GETDATE())
                THEN CAST(GETDATE() AS date)
                ELSE DATEFROMPARTS(@CurrentYear, 12, 31) END;
END
GO

-- ================================================================================================
-- KpiExport.vLead  (view)
-- Every paid-out lead, one row each, with exactly the columns the reports read. It folds today's three shared WebKPI functions into one definition and adds what the heat map, Margin Analysis and Minimum IRR currently read straight from Lead.vLead.
-- ================================================================================================
CREATE VIEW KpiExport.vLead
AS
SELECT
    L.LeadId,
    CAST(L.dtPayReceived AS date)                                         AS dtPayReceived,
    CAST(CASE WHEN L.dtUnwound IS NULL THEN 0 ELSE 1 END AS bit)          AS IsUnwound,
    L.TrueAmountFinanced                                                  AS AF,
    L.GrossFeeIncome                                                      AS GFI,
    L.TotalFeeIncome                                                      AS TFI,
    ISNULL(EA.SalesII, 0)                                                 AS SalesII,
    L.SalesCategoryId                                                     AS DivisionId,
    L.SalesCategory                                                       AS Division,
    L.SalesId,
    L.Salesperson,
    L.SalesCode,
    L.SalesOfficeId,
    L.LenderName,
    L.ProfessionCat                                                       AS Profession,
    L.FacilityCategory                                                    AS Facility,
    L.LeadCategory1                                                       AS Arrangement,
    L.IsOB,
    CAST(CASE WHEN L.ProfessionCat = 'Optical' THEN 1 ELSE 0 END AS bit)  AS IsOptical,
    CAST(CASE WHEN FL.FirstPayReceived >= L.dtPayReceived
              THEN 1 ELSE 0 END AS bit)                                   AS IsNewCustomer,
    FIN.IRR,
    L.IRRInclFees,
    L.Term,
    dbo.GetEffectiveMinIRR(L.dtPayReceived, L.SalesCategoryId)            AS MinIRR,
    LL.CMSPeriodId                                                        AS PeriodId,
    PC.AreaCode                                                           AS PostcodeArea
FROM   Lead.vLead L
       INNER JOIN Lead.Lead LL               ON LL.ID = L.LeadId
       LEFT  JOIN Lead.vLeadWithFinancials FIN ON FIN.LeadId = L.LeadId
       -- managed commission credited to the lead's own salesperson (was WebKPI.fnEquipmentAttribution)
       LEFT  JOIN
       (
           SELECT E.LeadID, E.AccountManagerId, SUM(E.ManagedCommission) AS SalesII
           FROM   Lead.Equipment E
           WHERE  E.ManagedByHQ = 0
             AND  E.AccountManagerId IS NOT NULL
             AND  ISNULL(E.ManagedCommission, 0) <> 0
           GROUP BY E.LeadID, E.AccountManagerId
       ) EA ON EA.LeadID = L.LeadId AND EA.AccountManagerId = L.SalesId
       -- each customer's first paid-out lead (was inside WebKPI.fnLeadDetail)
       LEFT  JOIN
       (
           SELECT CASE WHEN LE.LeadType = 0 THEN LE.CompanyID ELSE LE.ContactId END AS CustomerId,
                  MIN(LE.dtPayReceived) AS FirstPayReceived
           FROM   Lead.Lead LE
           WHERE  LE.dtPayReceived IS NOT NULL
           GROUP BY CASE WHEN LE.LeadType = 0 THEN LE.CompanyID ELSE LE.ContactId END
       ) FL ON FL.CustomerId = CASE WHEN L.LeadTypeId = 0 THEN L.CompanyId ELSE L.ContactId END
       -- the customer's postcode AREA only (was inside WebKPI.spHeatMap)
       LEFT  JOIN dbo.Company C  ON C.CompanyId = L.CompanyId
       LEFT  JOIN CON.Contact CO ON CO.Id = L.ContactId
       LEFT  JOIN CMS.Address A  ON A.Id = CO.AddressId
       CROSS APPLY (SELECT CASE WHEN L.LeadType = 0 THEN C.PostCode ELSE A.PostCode END AS PostCode) P
       CROSS APPLY (SELECT NULLIF(CASE WHEN ISNUMERIC(SUBSTRING(P.PostCode, 2, 1)) = 1
                                       THEN LEFT(P.PostCode, 1) ELSE LEFT(P.PostCode, 2) END, '') AS AreaCode) PC
WHERE  L.dtPayReceived IS NOT NULL;
GO

-- ================================================================================================
-- KpiExport.vLeadPipeline  (view)
-- Every lead created, approved or activated in the last six years, paid out or not, with its status and introducer. Sales Productivity's funnel needs leads that never paid out, which the paid-out view cannot give it.
-- ================================================================================================
CREATE VIEW KpiExport.vLeadPipeline
AS
SELECT
    L.ID                                 AS LeadId,
    L.SalesID                            AS SalesId,
    CAST(L.dtLead AS date)               AS dtLead,
    L.LeadStatus,
    L.TrueAmountFinanced                 AS AF,
    L.GrossFeeIncome                     AS GFI,
    CAST(L.dtAppAccept AS date)          AS dtAppAccept,
    CAST(L.dtActivation AS date)         AS dtActivation,
    I.Id                                 AS IntroducerId,
    I.ManagedByHQ                        AS IntroducerManagedByHQ,
    I.AccountManagerId                   AS IntroducerAccountManagerId
FROM   Lead.Lead L
       LEFT JOIN CMS.Introducer I ON I.Id = L.SourceIntroducerID
WHERE  L.dtLead       >= DATEFROMPARTS(YEAR(GETDATE()) - 6, 1, 1)
   OR  L.dtAppAccept  >= DATEFROMPARTS(YEAR(GETDATE()) - 6, 1, 1)
   OR  L.dtActivation >= DATEFROMPARTS(YEAR(GETDATE()) - 6, 1, 1);
GO

-- ================================================================================================
-- KpiExport.vSupplierYear  (view)
-- Amount financed per supplier per year from 2014, Klipfolio's rule unchanged: each equipment line's cost financed, less its share of any deposit not financed. Typed-in suppliers (no CMS.Introducer record) are left out, as Klipfolio leaves them out.
-- ================================================================================================
CREATE VIEW KpiExport.vSupplierYear
AS
SELECT
    YEAR(L.dtPayReceived)                   AS [Year],
    I.Id                                    AS IntroducerId,
    I.IntroducerName                        AS Supplier,
    ISNULL(T.LookupCode, 'Other')           AS Profession,
    I.AccountManagerId,
    CAST(ISNULL(I.ManagedByHQ, 0) AS bit)   AS ManagedByHQ,
    SUM(ISNULL(E.CostFinanced, 0.00) -
        CASE WHEN ISNULL(L.FacilityIsVatable, 0) = 1
             THEN CASE WHEN L.TotalEquipmentNet = 0 THEN 0
                       ELSE (ISNULL(L.DepositNotFinancedNet, 0) / L.TotalEquipmentNet) * ISNULL(E.CostFinanced, 0.00) END
             ELSE CASE WHEN L.TotalEquipmentGross = 0 THEN 0
                       ELSE (ISNULL(L.DepositNotFinancedGross, 0) / L.TotalEquipmentGross) * ISNULL(E.CostFinanced, 0.00) END
        END)                                AS AmountFinanced
FROM   Lead.vEquipment E
       INNER JOIN Lead.vLead L        ON L.LeadId = E.LeadID
       INNER JOIN CMS.Introducer I    ON I.Id = E.IntroducerID
       LEFT  JOIN CMS.LookupCode T    ON T.Id = I.BusinessTypeId
WHERE  L.dtPayReceived >= '20140101'
GROUP BY
    YEAR(L.dtPayReceived), I.Id, I.IntroducerName, ISNULL(T.LookupCode, 'Other'),
    I.AccountManagerId, ISNULL(I.ManagedByHQ, 0);
GO

-- ================================================================================================
-- KpiExport.vBookPosition  (view)
-- The serviced book now: every activated, not-unwound lead in OB.Lead with its remaining capital. That includes brokered deals Performance Finance services, as the original heat map did.
-- ================================================================================================
CREATE VIEW KpiExport.vBookPosition
AS
SELECT
    L.ID                                                     AS LeadId,
    CAST(ISNULL(LC.IsOB, 0) AS bit)                          AS IsOB,
    OBL.RemainingCapital,
    CASE WHEN L.LeadType = 0 THEN 'C' + CAST(L.CompanyID AS varchar(20))
         ELSE 'P' + CAST(L.ContactId AS varchar(20)) END     AS CustomerKey,
    PC.AreaCode                                              AS PostcodeArea
FROM   Lead.Lead L
       INNER JOIN OB.Lead OBL          ON OBL.LeadId = L.ID
       INNER JOIN Lead.LeadCategory LC ON LC.Id = L.LeadCategoryId
       LEFT  JOIN dbo.Company C        ON C.CompanyId = L.CompanyID
       LEFT  JOIN CON.Contact CO       ON CO.Id = L.ContactId
       LEFT  JOIN CMS.Address A        ON A.Id = CO.AddressId
       CROSS APPLY (SELECT CASE WHEN L.LeadType = 0 THEN C.PostCode ELSE A.PostCode END AS PostCode) P
       CROSS APPLY (SELECT NULLIF(CASE WHEN ISNUMERIC(SUBSTRING(P.PostCode, 2, 1)) = 1
                                       THEN LEFT(P.PostCode, 1) ELSE LEFT(P.PostCode, 2) END, '') AS AreaCode) PC
WHERE  L.dtUnwound IS NULL;
GO

-- ================================================================================================
-- KpiExport.vFundingLine  (view)
-- Every piece of funding with its buy rate and term, worked out once: blocks, RCF allocations (including the Conister RCF leads later moved to the IWFA) and IWFA drawdowns. Today two cursors work out the rate in force and the payments remaining one lead at a time, for every report call. Here it is done once per load, set-based.
-- ================================================================================================
CREATE VIEW KpiExport.vFundingLine
AS
-- Blocks: shared across divisions by the report (no lead link)
SELECT
    'Block'                                           AS Source,
    B.BlockId                                         AS SourceId,
    CAST(NULL AS int)                                 AS LeadId,
    B.StartDate                                       AS EventDate,
    CAST(NULL AS int)                                 AS DivisionId,
    B.BlockAmount                                     AS Amount,
    B.Term,
    ISNULL(NULLIF(B.ScheduleIRR, 0), B.IRR) / 100.0   AS Rate,
    CAST(0 AS bit)                                    AS IsBulkMove
FROM   OB.Block B
WHERE  B.StartDate <= CAST(GETDATE() AS date)

UNION ALL

-- RCF allocations: capital outstanding and payments remaining at the allocation date, the rate in force then
SELECT
    'RCF', A.RCFId, A.LeadId, A.AllocatedDate, VL.SalesCategoryId,
    S.CapitalElement + S.CapitalOS,
    ISNULL(S.PaymentsRemaining, 0),
    ISNULL(R.InterestRate, 0),
    CAST(CASE WHEN (A.RCFId = 1478 AND A.AllocatedDate = '20210701')     -- Shawbrook: the initial bulk move
                OR (A.RCFId = 1497 AND A.AllocatedDate = '20211110')     -- Conister: the initial bulk move
              THEN 1 ELSE 0 END AS bit)
FROM   (
           SELECT RC.RCFId, OL.LeadID AS LeadId, CAST(OL.FundAllocatedDate AS date) AS AllocatedDate
           FROM   OB.Lead OL
                  INNER JOIN OB.RCF RC ON RC.RCFId = OL.FundId
           WHERE  OL.FundAllocatedDate IS NOT NULL
           UNION
           -- Conister RCF leads later moved to the IWFA (recovered from backup)
           SELECT 1497, I.LeadId, I.FundAllocatedDate
           FROM   KpiExport.ConisterRCF_IWFA I
       ) A
       INNER JOIN Lead.vLead VL ON VL.LeadId = A.LeadId
       OUTER APPLY (SELECT TOP 1 IR.InterestRate
                    FROM   dbo.OBRCFInterestRate IR
                    WHERE  IR.RCFId = A.RCFId AND IR.FromDate <= A.AllocatedDate
                    ORDER BY IR.FromDate DESC) R
       OUTER APPLY (SELECT TOP 1 S.PaymentsRemaining, S.CapitalElement, S.CapitalOS
                    FROM   OB.ScheduleHeader H
                           INNER JOIN OB.Schedule S ON S.ScheduleHeaderID = H.ID
                    WHERE  H.LeadID = A.LeadId AND H.Active = 1 AND S.DueDate >= A.AllocatedDate
                    ORDER BY S.DueDate) S

UNION ALL

-- IWFA drawdowns: the cohort rate plus the 0.1% service fee; cohort 1 was the initial RCF -> IWFA move
SELECT
    'IWFA', C.IWFAId, OL.LeadID, C.DrawdownDate, VL.SalesCategoryId,
    IL.AllocatedCapital,
    ISNULL(S.PaymentsRemaining, 0),
    C.InterestRate + 0.001,
    CAST(0 AS bit)
FROM   OB.IWFACohort C
       INNER JOIN OB.IWFALead IL ON IL.IWFACohortId = C.Id
       INNER JOIN OB.Lead OL     ON OL.LeadID = IL.OBLeadId
       INNER JOIN Lead.vLead VL  ON VL.LeadId = OL.LeadID
       OUTER APPLY (SELECT TOP 1 S.PaymentsRemaining, S.CapitalElement, S.CapitalOS
                    FROM   OB.ScheduleHeader H
                           INNER JOIN OB.Schedule S ON S.ScheduleHeaderID = H.ID
                    WHERE  H.LeadID = OL.LeadID AND H.Active = 1 AND S.DueDate >= C.DrawdownDate
                    ORDER BY S.DueDate) S
WHERE  C.Id <> 1;
GO

-- ================================================================================================
-- KpiExport.vSalesperson  (view)
-- Every salesperson with their office, for filter lists, names and the office of each figure. No phone, mobile or email.
-- ================================================================================================
CREATE VIEW KpiExport.vSalesperson
AS
SELECT S.Id AS SalesId, S.FullName AS Salesperson, S.SalesCode, S.DivisionId, S.SalesOfficeId, S.IsActive
FROM   CMS.SalesPerson S;
GO

-- ================================================================================================
-- KpiExport.vSalesOffice  (view)
-- The sales offices (Kettering, Mobberley), for the Sales Office filter. That filter is a mock today; it becomes real once the copied figures carry each lead's office, which Fact.Lead does.
-- ================================================================================================
CREATE VIEW KpiExport.vSalesOffice
AS
SELECT O.Id AS SalesOfficeId, O.OfficeName, O.OrderInt, O.IsActive
FROM   CMS.SalesOffice O;
GO

-- ================================================================================================
-- KpiExport.vPeriod  (view)
-- CMS's own calendar of periods, quarters and years, which decides 'this month / quarter / year to date' and Minimum IRR's groupings.
-- ================================================================================================
CREATE VIEW KpiExport.vPeriod
AS
SELECT P.Id AS PeriodId, P.Number AS PeriodNumber, P.Name AS PeriodName,
       CAST(P.PeriodStartDate AS date) AS PeriodStartDate, CAST(P.PeriodEndDate AS date) AS PeriodEndDate,
       Q.Id AS QuarterId, Q.Number AS QuarterNumber, Q.Name AS QuarterName,
       Y.Id AS YearId, Y.Number AS YearNumber, Y.Name AS YearName
FROM   CMS.CMSPeriod P
       INNER JOIN CMS.CMSQuarter Q ON Q.Id = P.QuarterId
       INNER JOIN CMS.CMSYear    Y ON Y.Id = P.YearId;
GO

-- ================================================================================================
-- KpiExport.vWorkingDay  (view)
-- The working-day calendar, used to pro-rate a year's target to date.
-- ================================================================================================
CREATE VIEW KpiExport.vWorkingDay
AS
SELECT DISTINCT CAST(W.WorkingDay AS date) AS WorkingDay
FROM   dbo.KPIWorkingDays W;
GO

-- ================================================================================================
-- KpiExport.vSalesTarget  (view)
-- Each salesperson's year targets, maintained in the desktop (Sales People > Targets).
-- ================================================================================================
CREATE VIEW KpiExport.vSalesTarget
AS
SELECT T.SalesPersonId AS SalesId, Y.Number AS [Year], T.AFTarget, T.TFITarget
FROM   CMS.SalesTargetYear T
       INNER JOIN CMS.CMSYear Y ON Y.Id = T.YearId;
GO

-- ================================================================================================
-- KpiExport.vFundLender  (view)
-- Fund lenders, for Exposure's first filter.
-- ================================================================================================
CREATE VIEW KpiExport.vFundLender
AS
SELECT FL.Id AS FundLenderId, FL.LenderName AS FundLenderName
FROM   OB.FundLender FL;
GO

-- ================================================================================================
-- KpiExport.vFund  (view)
-- Funds and their lender, for Exposure's second filter. No FTP or bank settings.
-- ================================================================================================
CREATE VIEW KpiExport.vFund
AS
SELECT F.Id AS FundId, F.FundName, F.FundLenderId
FROM   OB.Fund F;
GO

-- ================================================================================================
-- KpiExport.BadDebtSnapshot  (table)
-- Last night's results of the desktop's own OB.AnnualBadDebtList, for all three 'which applications' choices, kept in CMS_Live so the push program can copy them like any other table.
-- ================================================================================================
CREATE TABLE KpiExport.BadDebtSnapshot
(
    IsAutoApproved       tinyint        NOT NULL,
    BadDebtYear          int            NOT NULL,
    Id                   int            NOT NULL,
    YearStartDate        date           NOT NULL,
    YearEndDate          date           NOT NULL,
    AmountFinanced       decimal(19,2)  NOT NULL,
    RemainingCapital     decimal(19,2)  NOT NULL,
    ActiveLeadCount      int            NOT NULL,
    WrittenOffAmount     decimal(19,2)  NOT NULL,
    WrittenOffLeadCount  int            NOT NULL,
    RecoveredAmount      decimal(19,2)  NOT NULL,
    BadDebtCosts         decimal(19,2)  NOT NULL,
    TerminatedLeadCount  int            NOT NULL,
    ArrearsLeadCount     int            NOT NULL,
    ArrearsCompanyCount  int            NOT NULL,
    CONSTRAINT PK_BadDebtSnapshot PRIMARY KEY (IsAutoApproved, BadDebtYear)
);
GO

-- ================================================================================================
-- KpiExport.ExposureSnapshot  (table)
-- Last night's top 10 from the desktop's own Reports.Exposure, for all lenders and for each lender (the KPI site's Exposure has a lender filter, no fund filter).
-- ================================================================================================
CREATE TABLE KpiExport.ExposureSnapshot
(
    GroupingKey              tinyint        NOT NULL,
    FundLenderId             int            NOT NULL,
    FundId                   int            NOT NULL,
    RowNumber                int            NOT NULL,
    Id                       int            NOT NULL,
    Name                     nvarchar(200)  NULL,
    LeadCount                int            NULL,
    RemainingCapital         decimal(18,2)  NULL,
    TotalRemainingCapital    decimal(18,2)  NULL,
    PercentRemainingCapital  decimal(18,6)  NULL,
    CustomerType             int            NULL,
    CONSTRAINT PK_ExposureSnapshot PRIMARY KEY (GroupingKey, FundLenderId, FundId, RowNumber)
);
GO

-- ================================================================================================
-- KpiExport.spPrepareSnapshots  (procedure)
-- Runs the desktop's Bad Debt procedure three times and its Exposure procedure once per filter combination, keeping only the top 10 of each, and replaces the two snapshot tables in one transaction. Bad Debt and Exposure keep the desktop's own definitions, and the slow procedures run once a night instead of on every page view.
-- ================================================================================================
CREATE PROCEDURE KpiExport.spPrepareSnapshots
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- 1. Bad Debt, for each 'which applications' choice
    CREATE TABLE #BadDebtOne
    (
        Id int, BadDebtYear int, YearStartDate date, YearEndDate date, AmountFinanced decimal(19,2),
        RemainingCapital decimal(19,2), ActiveLeadCount int, WrittenOffAmount decimal(19,2),
        WrittenOffLeadCount int, RecoveredAmount decimal(19,2), BadDebtCosts decimal(19,2),
        TerminatedLeadCount int, ArrearsLeadCount int, ArrearsCompanyCount int
    );
    SELECT CAST(0 AS tinyint) AS IsAutoApproved, * INTO #BadDebt FROM #BadDebtOne WHERE 1 = 0;

    DECLARE @a tinyint = 0;
    WHILE @a <= 2                      -- 0 = approved by hand, 1 = auto approved, 2 = all
    BEGIN
        DELETE #BadDebtOne;
        INSERT #BadDebtOne EXEC OB.AnnualBadDebtList @IsAutoApproved = @a;
        INSERT #BadDebt SELECT @a, * FROM #BadDebtOne;
        SET @a += 1;
    END

    -- 2. Exposure, for all lenders and each lender; only the top 10 kept
    CREATE TABLE #ExposureOne
    (
        RowNumber int, Id int, [Name] nvarchar(200), LeadCount int, LeadList nvarchar(max),
        RemainingCapital decimal(18,2), TotalRemainingCapital decimal(18,2),
        PercentRemainingCapital decimal(18,6), CustomerType int
    );
    SELECT TOP (0) * INTO #Exposure FROM KpiExport.ExposureSnapshot;

    DECLARE @k tinyint, @FundLenderId int, @FundId int;
    DECLARE @Grouping int, @LenderList nvarchar(20), @FundList nvarchar(20);
    DECLARE combos CURSOR LOCAL FAST_FORWARD FOR
        SELECT G.k, X.FundLenderId, X.FundId
        FROM   (VALUES (0), (1), (2)) G(k)                                -- customer, business group, combined
               CROSS JOIN
               (
                   SELECT 0 AS FundLenderId, 0 AS FundId                  -- every lender
                   UNION SELECT FL.Id, 0 FROM OB.FundLender FL            -- each lender
                   -- no fund filter on the KPI site: one run per fund would be ~300 more a night
               ) X;
    OPEN combos;
    FETCH NEXT FROM combos INTO @k, @FundLenderId, @FundId;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @Grouping   = CASE @k WHEN 0 THEN 0 WHEN 1 THEN 1 END;           -- NULL = combined
        SET @LenderList = CASE WHEN @FundLenderId = 0 THEN NULL ELSE CAST(@FundLenderId AS nvarchar(20)) END;
        SET @FundList   = CASE WHEN @FundId = 0 THEN NULL ELSE CAST(@FundId AS nvarchar(20)) END;

        DELETE #ExposureOne;
        INSERT #ExposureOne
        EXEC Reports.Exposure @CustomerBusinessGroup = @Grouping, @FundLenderIds = @LenderList,
                              @FundIds = @FundList, @OBBrokered = 1;

        INSERT #Exposure (GroupingKey, FundLenderId, FundId, RowNumber, Id, [Name], LeadCount,
                          RemainingCapital, TotalRemainingCapital, PercentRemainingCapital, CustomerType)
        SELECT @k, @FundLenderId, @FundId, RowNumber, Id, [Name], LeadCount, RemainingCapital,
               TotalRemainingCapital, PercentRemainingCapital, CustomerType
        FROM   #ExposureOne
        WHERE  RowNumber <= 10;                                           -- the page shows ten; keep only those

        FETCH NEXT FROM combos INTO @k, @FundLenderId, @FundId;
    END
    CLOSE combos;
    DEALLOCATE combos;

    -- 3. replace both snapshots together
    BEGIN TRANSACTION;
        DELETE KpiExport.BadDebtSnapshot;
        INSERT KpiExport.BadDebtSnapshot (IsAutoApproved, BadDebtYear, Id, YearStartDate, YearEndDate, AmountFinanced, RemainingCapital, ActiveLeadCount, WrittenOffAmount, WrittenOffLeadCount, RecoveredAmount, BadDebtCosts, TerminatedLeadCount, ArrearsLeadCount, ArrearsCompanyCount)
        SELECT IsAutoApproved, BadDebtYear, Id, YearStartDate, YearEndDate, AmountFinanced, RemainingCapital, ActiveLeadCount, WrittenOffAmount, WrittenOffLeadCount, RecoveredAmount, BadDebtCosts, TerminatedLeadCount, ArrearsLeadCount, ArrearsCompanyCount FROM #BadDebt;

        DELETE KpiExport.ExposureSnapshot;
        INSERT KpiExport.ExposureSnapshot SELECT * FROM #Exposure;
    COMMIT TRANSACTION;
END
GO
