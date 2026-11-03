-- ============================================================================================================
-- SHIRE BUSINESS ANALYSIS (3 Nov 2026, Steve): the third Shire report - as PF's Business Analysis, without a sales
-- office, by Profession / Facility / Purpose / Lender, measured by Amount Financed or Income, with Proposed to PF.
-- Applied by migration ShireBusinessAnalysis, batch by batch (GO). Once applied, never edit it: a new numbered file.
--
-- The copy gains four of each won deal's own fields from Shire's Salesforce - still nothing about who the client is:
--   Sector          the client company's Account.Sector__c       -> Profession (Solicitors, Accountants, Dentists...)
--   FinanceProduct  Opportunity.Finance_Product__c                -> Facility, grouped as PF's: HP / Loan / Lease / Rental
--   LoanPurpose     Opportunity.Loan_Purpose__c                   -> Purpose (Practising Certificates, VAT, PII...)
--   Lender          Opportunity.Funder1__c (the Funder picklist)  -> Lender (Premium Credit, Siemens...)
-- Blank in Salesforce = NULL here = "Not set" on the report, so every dimension adds up to the Year on Year totals.
-- Lender is the Funder PICKLIST, not the Funder lookup that Proposed to PF reads: the picklist is filled on all but 4
-- won deals (the lookup is blank on 113, 74 of them Shire Professional Funding's own) and its names are the short ones.
-- ============================================================================================================

ALTER TABLE Shire.StageSale ADD
    Sector          nvarchar(100)  NULL,
    FinanceProduct  nvarchar(100)  NULL,
    LoanPurpose     nvarchar(100)  NULL,
    Lender          nvarchar(100)  NULL;
GO

ALTER TABLE Shire.Sale ADD
    Sector          nvarchar(100)  NULL,
    FinanceProduct  nvarchar(100)  NULL,
    LoanPurpose     nvarchar(100)  NULL,
    Lender          nvarchar(100)  NULL;
GO

-- As 001's, publishing the four new columns too. (The site copies afresh at its first check after a restart, which
-- fills them; until then they are NULL - "Not set".)
ALTER PROCEDURE Shire.spPublish
(
    @StartedUtc        datetime2(0) = NULL,   -- when the copy began (the site), so Started / Took cover the whole copy
    @SourceChangedUtc  datetime2(0) = NULL    -- Salesforce's newest change time in the data copied
)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- a run left open for 15 minutes was cut off part-way (a restart mid-publish; the publish itself rolled back)
    UPDATE Shire.LoadRun
    SET    Succeeded = 0, Error = 'Stopped part-way: the site restarted or lost its connection (nothing was published)'
    WHERE  Succeeded IS NULL AND FinishedUtc IS NULL AND StartedUtc < DATEADD(MINUTE, -15, SYSUTCDATETIME());

    DECLARE @RunId int, @Count int;
    INSERT Shire.LoadRun (StartedUtc, SourceChangedUtc) VALUES (ISNULL(@StartedUtc, SYSUTCDATETIME()), @SourceChangedUtc);
    SET @RunId = SCOPE_IDENTITY();

    BEGIN TRY
        SELECT @Count = COUNT(*) FROM Shire.StageSale;
        IF @Count = 0
            THROW 50102, 'Shire.StageSale is empty: not publishing (the Shire reports keep the last good figures).', 1;
        IF EXISTS (SELECT 1 FROM Shire.StageSale WHERE OpportunityId IS NULL OR CloseDate IS NULL OR OwnerId IS NULL)
            THROW 50103, 'Shire.StageSale has a row without an Id, a Close Date or an owner: not publishing.', 1;

        BEGIN TRANSACTION;
        DELETE Shire.Sale;
        INSERT Shire.Sale (OpportunityId, CloseDate, Amount, Income, OwnerId, Salesperson, ProposedToPF,
                           Sector, FinanceProduct, LoanPurpose, Lender)
        SELECT OpportunityId, CloseDate, ISNULL(Amount, 0), ISNULL(Income, 0), OwnerId, ISNULL(Salesperson, N''), ISNULL(ProposedToPF, 0),
               NULLIF(LTRIM(RTRIM(Sector)), N''), NULLIF(LTRIM(RTRIM(FinanceProduct)), N''),
               NULLIF(LTRIM(RTRIM(LoanPurpose)), N''), NULLIF(LTRIM(RTRIM(Lender)), N'')
        FROM   Shire.StageSale;

        UPDATE Shire.Setting
        SET    LastCopyUtc = SYSUTCDATETIME(), LastCheckUtc = SYSUTCDATETIME(),
               SourceChangedUtc = @SourceChangedUtc, SaleCount = @Count
        WHERE  SettingId = 1;
        COMMIT TRANSACTION;

        UPDATE Shire.LoadRun SET FinishedUtc = SYSUTCDATETIME(), Succeeded = 1, SaleCount = @Count WHERE LoadRunId = @RunId;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        UPDATE Shire.LoadRun
        SET    FinishedUtc = SYSUTCDATETIME(), Succeeded = 0, Error = LEFT(ERROR_MESSAGE(), 2000)
        WHERE  LoadRunId = @RunId;
        THROW;
    END CATCH
END
GO

-- ---- the report (kpi_site) ------------------------------------------------------------------------------------
-- @Measure: 'AF' = Amount (Amount Financed) | 'Income' = Income.  @ProposedToPF: NULL = all, 1 = yes, 0 = no.

-- Shire Business Analysis by Profession, Facility or Purpose: as PF's Rpt.spBusinessAnalysis - each value's whole-year
-- total and its total to today's day of the year, every year. Facility = Finance Product in PF's groups (Steve):
-- Hire Purchase = HP, Finance Lease = Lease, every kind of loan = Loan (Shire writes no rentals); Title Fee and
-- Invoice Finance are no facility of PF's, so Other.
CREATE PROCEDURE Shire.spBusinessAnalysis
(
    @Dimension     nvarchar(20),
    @Measure       nvarchar(10) = 'AF',
    @Quarter       int          = 0,       -- 0 = all quarters, else 1-4
    @ProposedToPF  bit          = NULL
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @AsOf date = Shire.fnReportDate();
    DECLARE @M int = MONTH(@AsOf), @D int = DAY(@AsOf);

    WITH Base AS
    (
        SELECT
            YEAR(S.CloseDate) AS Yr,
            CASE @Dimension
                WHEN 'Profession' THEN ISNULL(S.Sector, N'Not set')
                WHEN 'Facility'   THEN CASE WHEN S.FinanceProduct IS NULL                            THEN N'Not set'
                                            WHEN S.FinanceProduct = N'Hire Purchase'                 THEN N'HP'
                                            WHEN S.FinanceProduct = N'Finance Lease'                 THEN N'Lease'
                                            WHEN S.FinanceProduct IN (N'Title Fee', N'Invoice Finance') THEN N'Other'
                                            ELSE N'Loan' END
                WHEN 'Purpose'    THEN ISNULL(S.LoanPurpose, N'Not set')
            END AS DimValue,
            CASE WHEN MONTH(S.CloseDate) < @M OR (MONTH(S.CloseDate) = @M AND DAY(S.CloseDate) <= @D)
                 THEN 1 ELSE 0 END AS InYtd,
            CASE WHEN @Measure = 'Income' THEN S.Income ELSE S.Amount END AS Amount
        FROM   Shire.Sale S
        WHERE  (@Quarter = 0 OR DATEPART(QUARTER, S.CloseDate) = @Quarter)
          AND  (@ProposedToPF IS NULL OR S.ProposedToPF = @ProposedToPF)
    )
    SELECT Yr, DimValue,
           SUM(Amount)                                     AS FullTotal,
           SUM(CASE WHEN InYtd = 1 THEN Amount ELSE 0 END) AS YtdTotal
    FROM   Base
    WHERE  DimValue IS NOT NULL
    GROUP BY Yr, DimValue
    ORDER BY Yr, SUM(Amount) DESC;
END
GO

-- Shire Business Analysis by Lender: as PF's Rpt.spBusinessAnalysisByLender - each (year, quarter, lender) and each
-- (year, lender) (Quarter 0), both measures, whole and to today's day of the year. The site sets each lender's year
-- beside its own year before (BusinessAnalysisLenderService.Build, PF's).
CREATE PROCEDURE Shire.spBusinessAnalysisByLender
(
    @ProposedToPF  bit = NULL
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @AsOf date = Shire.fnReportDate();
    DECLARE @M int = MONTH(@AsOf), @D int = DAY(@AsOf);

    WITH Won AS
    (
        SELECT
            YEAR(S.CloseDate)              AS Yr,
            DATEPART(QUARTER, S.CloseDate) AS Qtr,
            ISNULL(S.Lender, N'Not set')   AS Lender,
            S.Amount,
            S.Income,
            CASE WHEN MONTH(S.CloseDate) < @M OR (MONTH(S.CloseDate) = @M AND DAY(S.CloseDate) <= @D)
                 THEN 1 ELSE 0 END AS InYtd
        FROM   Shire.Sale S
        WHERE  (@ProposedToPF IS NULL OR S.ProposedToPF = @ProposedToPF)
    )
    SELECT
        Yr                                                  AS [Year],
        CASE WHEN GROUPING(Qtr) = 1 THEN 0 ELSE Qtr END     AS [Quarter],
        Lender,
        SUM(Amount)                                         AS AF,
        SUM(CASE WHEN InYtd = 1 THEN Amount ELSE 0 END)     AS AFToDate,
        SUM(Income)                                         AS Income,
        SUM(CASE WHEN InYtd = 1 THEN Income ELSE 0 END)     AS IncomeToDate
    FROM   Won
    GROUP BY GROUPING SETS ((Yr, Qtr, Lender), (Yr, Lender))
    ORDER BY [Year], [Quarter], AF DESC, Lender;
END
GO

-- ---- rights -------------------------------------------------------------------------------------------------------
GRANT EXECUTE ON Shire.spBusinessAnalysis          TO kpi_site;
GRANT EXECUTE ON Shire.spBusinessAnalysisByLender  TO kpi_site;
GRANT EXECUTE ON Shire.spBusinessAnalysis          TO kpi_reader;
GRANT EXECUTE ON Shire.spBusinessAnalysisByLender  TO kpi_reader;
