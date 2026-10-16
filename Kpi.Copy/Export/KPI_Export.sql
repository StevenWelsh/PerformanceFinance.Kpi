/*  KPI_Export - the database, beside CMS on the same SQL Server, that defines EXACTLY what leaves for the KPI site
    (docs/kpi-copy-plan.html). Nothing KPI-specific is created inside CMS itself.

    The views (KpiExport.v*) read CMS through SYNONYMS (named pointers: Lead.vLead here means <CMS database>.Lead.vLead);
    the CMS database is set on ONE line below (@CmsDb): CMS_Dev on a developer's machine, CMS_Live on the office server.
    The one difference from CMS's own text: KpiExport.ConisterRCF_IWFA points at CMS's WebKPI.ConisterRCF_IWFA.

    THE PULL (Oct 2026): SQL Server Agent refreshes the copies here (KpiExport.spRefreshCopies: every view vX into the
    table KpiCopy.X, in one transaction) every 10 minutes, and nightly prepares Bad Debt and Exposure first. The KPI site
    in Azure reads KpiCopy.Refresh every two minutes and copies when it has moved, through a login in kpi_pull that can
    read the copies and nothing else - it has no user in CMS. The script creates the Agent jobs too (when missing).

    Apply it with Kpi.Tools (sets @CmsDb for you, checks the version, runs every batch):
        dotnet run --project Kpi.Tools -- export-apply --cms CMS_Live --remote   (or CMS_Dev on a developer's machine)
        dotnet run --project Kpi.Tools -- export-check                          (is every view still valid?)
    or in SSMS, after changing @CmsDb by hand. Safe to run again: it drops and recreates everything except the
    database, the kpi_pull role and its members, and the Agent jobs. Run by a person, never by an app.

    VERSION: when this script changes, raise the number in KpiExport.Version near the bottom AND ExportVersion.Current
    (Kpi.Copy/Export/ExportVersion.cs) together - the copy refuses a KPI_Export of any other version, and export-apply
    refuses a script whose number disagrees with the code.  */

USE master;
GO
IF DB_ID(N'KPI_Export') IS NULL
    CREATE DATABASE KPI_Export COLLATE Latin1_General_CI_AS;
GO
-- nothing here needs restoring to a point in time (it is all rebuilt from CMS), so the log never needs backing up
ALTER DATABASE KPI_Export SET RECOVERY SIMPLE;
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
-- schemas: KpiExport, KpiCopy (what the KPI site reads), and the CMS schemas the synonyms sit in
-- ================================================================================================
IF SCHEMA_ID(N'KpiExport') IS NULL EXEC (N'CREATE SCHEMA KpiExport');
IF SCHEMA_ID(N'KpiCopy')   IS NULL EXEC (N'CREATE SCHEMA KpiCopy');
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

        -- tells the KPI site to copy Bad Debt and Exposure with its next copy
        UPDATE KpiCopy.Refresh SET SnapshotsUtc = SYSUTCDATETIME();
        IF @@ROWCOUNT = 0           -- a new database, never refreshed: the nightly job's refresh follows at once
            INSERT KpiCopy.Refresh (RefreshedUtc, ReportDate, SnapshotsUtc)
            VALUES ('2000-01-01', KpiExport.fnReportDate(), SYSUTCDATETIME());
    COMMIT TRANSACTION;
END
GO

-- ================================================================================================
-- KpiCopy.Refresh  (table)
-- One row: when the copies were last refreshed, the business date they are 'as at', and when Bad Debt / Exposure were
-- last prepared. The KPI site reads only this every two minutes, and copies only when a time has moved.
-- ================================================================================================
CREATE TABLE KpiCopy.Refresh
(
    RefreshedUtc  datetime2(3)  NOT NULL,
    ReportDate    date          NOT NULL,
    SnapshotsUtc  datetime2(3)  NULL
);
GO

-- ================================================================================================
-- KpiExport.spRefreshCopies  (procedure)
-- Copies every KpiExport view vX into the table KpiCopy.X, all in ONE transaction with the refresh time, so the site
-- never sees half a refresh. Run by the 'KPI_Export - refresh' Agent job (and by Kpi.Push on a developer's machine).
-- The tables are rebuilt each time (SELECT INTO), so they always have exactly the views' columns; a new view gets a copy
-- without any change here. The site's login reads only these copies: it has no rights in CMS at all.
-- ================================================================================================
CREATE PROCEDURE KpiExport.spRefreshCopies
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @sql nvarchar(max) = N'';
    SELECT @sql += N'DROP TABLE IF EXISTS KpiCopy.' + QUOTENAME(STUFF(V.name, 1, 1, N'')) + N'; '
                 + N'SELECT * INTO KpiCopy.' + QUOTENAME(STUFF(V.name, 1, 1, N''))
                 + N' FROM KpiExport.' + QUOTENAME(V.name) + N';' + CHAR(10)
    FROM   sys.views V
    WHERE  V.schema_id = SCHEMA_ID(N'KpiExport')
      AND  V.name LIKE N'v%';

    DECLARE @ReportDate date = KpiExport.fnReportDate();

    BEGIN TRANSACTION;
        -- one refresh at a time: a second one started while this runs gives up at once (not a failure)
        DECLARE @lock int;
        EXEC @lock = sp_getapplock @Resource = N'KpiExportRefresh', @LockMode = N'Exclusive',
                                   @LockOwner = N'Transaction', @LockTimeout = 0;
        IF @lock < 0
        BEGIN
            ROLLBACK TRANSACTION;
            PRINT 'Another refresh was still running, so this one was skipped.';
            RETURN;
        END

        EXEC (@sql);

        UPDATE KpiCopy.Refresh SET RefreshedUtc = SYSUTCDATETIME(), ReportDate = @ReportDate;
        IF @@ROWCOUNT = 0
            INSERT KpiCopy.Refresh (RefreshedUtc, ReportDate) VALUES (SYSUTCDATETIME(), @ReportDate);
    COMMIT TRANSACTION;
END
GO

-- ================================================================================================
-- kpi_pull: the KPI site's login (Kpi.Tools grant-pull) reads the copies, the two snapshots and the version - nothing
-- else here, and it has no user in CMS. The role and its members survive a rerun; the grants are made again each time.
-- ================================================================================================
IF DATABASE_PRINCIPAL_ID(N'kpi_pull') IS NULL CREATE ROLE kpi_pull;
GRANT SELECT ON SCHEMA::KpiCopy               TO kpi_pull;
GRANT SELECT ON KpiExport.BadDebtSnapshot     TO kpi_pull;
GRANT SELECT ON KpiExport.ExposureSnapshot    TO kpi_pull;
GO

-- ================================================================================================
-- the version: the KPI site and Kpi.Push copy only from the KPI_Export they were built for (ExportVersion.Current) -
-- raise both together
-- ================================================================================================
CREATE TABLE KpiExport.Version (Version int NOT NULL, AppliedUtc datetime2(0) NOT NULL);
INSERT KpiExport.Version (Version, AppliedUtc) VALUES (2, SYSUTCDATETIME());
GRANT SELECT ON KpiExport.Version TO kpi_pull;
GO

-- ================================================================================================
-- the SQL Server Agent jobs (not on SQL Express, which has no Agent: there run EXEC KpiExport.spRefreshCopies by hand,
-- or let Kpi.Push do it). Created only when MISSING, so a schedule changed in SSMS survives running this again.
--   KPI_Export - refresh   every 10 minutes, 07:00-20:00, Monday to Saturday
--   KPI_Export - nightly   00:15 daily: Bad Debt and Exposure (a few minutes), then a refresh
-- Owned by sa (whatever it is called here), so the steps can read CMS through the synonyms; no program, no password.
-- ================================================================================================
IF CAST(SERVERPROPERTY('EngineEdition') AS int) = 4
    PRINT 'SQL Express has no SQL Server Agent: no jobs created. Refresh by hand with EXEC KPI_Export.KpiExport.spRefreshCopies.';
ELSE
BEGIN
    DECLARE @owner sysname = SUSER_SNAME(0x01);

    IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'KPI_Export - refresh')
    BEGIN
        EXEC msdb.dbo.sp_add_job @job_name = N'KPI_Export - refresh', @owner_login_name = @owner,
             @description = N'Refreshes KPI_Export''s copies (KpiCopy) from CMS for the KPI website, which collects them within two minutes. From the PerformanceFinance.Kpi repo (Kpi.Push/Export/KPI_Export.sql).';
        EXEC msdb.dbo.sp_add_jobstep @job_name = N'KPI_Export - refresh', @step_name = N'Refresh the copies',
             @subsystem = N'TSQL', @database_name = N'KPI_Export', @command = N'EXEC KpiExport.spRefreshCopies;';
        EXEC msdb.dbo.sp_add_jobschedule @job_name = N'KPI_Export - refresh', @name = N'Every 10 minutes, 07:00-20:00, Mon-Sat',
             @freq_type = 8, @freq_interval = 126, @freq_recurrence_factor = 1,    -- weekly: Mon+Tue+Wed+Thu+Fri+Sat
             @freq_subday_type = 4, @freq_subday_interval = 10,                  -- every 10 minutes
             @active_start_time = 70000, @active_end_time = 200000;
        EXEC msdb.dbo.sp_add_jobserver @job_name = N'KPI_Export - refresh', @server_name = N'(local)';
        PRINT 'Created the job KPI_Export - refresh.';
    END
    ELSE PRINT 'The job KPI_Export - refresh already exists: left as it is.';

    IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'KPI_Export - nightly')
    BEGIN
        EXEC msdb.dbo.sp_add_job @job_name = N'KPI_Export - nightly', @owner_login_name = @owner,
             @description = N'Prepares the Bad Debt and Exposure snapshots for the KPI website, then refreshes the copies. From the PerformanceFinance.Kpi repo (Kpi.Push/Export/KPI_Export.sql).';
        EXEC msdb.dbo.sp_add_jobstep @job_name = N'KPI_Export - nightly', @step_name = N'Bad Debt and Exposure',
             @subsystem = N'TSQL', @database_name = N'KPI_Export', @command = N'EXEC KpiExport.spPrepareSnapshots;',
             @on_success_action = 3;                                              -- then the next step
        EXEC msdb.dbo.sp_add_jobstep @job_name = N'KPI_Export - nightly', @step_name = N'Refresh the copies',
             @subsystem = N'TSQL', @database_name = N'KPI_Export', @command = N'EXEC KpiExport.spRefreshCopies;';
        EXEC msdb.dbo.sp_add_jobschedule @job_name = N'KPI_Export - nightly', @name = N'Daily at 00:15',
             @freq_type = 4, @freq_interval = 1, @active_start_time = 1500;
        EXEC msdb.dbo.sp_add_jobserver @job_name = N'KPI_Export - nightly', @server_name = N'(local)';
        PRINT 'Created the job KPI_Export - nightly.';
    END
    ELSE PRINT 'The job KPI_Export - nightly already exists: left as it is.';
END
GO
