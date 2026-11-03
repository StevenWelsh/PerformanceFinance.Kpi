-- ============================================================================================================
-- SHIRE SALESFORCE (3 Nov 2026, Steve): the two Shire reports - Shire Year on Year and Shire Sales Performance.
-- Applied by migration ShireSalesforce (Kpi.Data/Migrations), which runs this file batch by batch (GO). Once applied,
-- never edit it: change Shire with a NEW numbered file and a new migration.
--
-- Everything Shire lives in schema Shire, apart from PF's own tables. The site (Kpi.Web/Shire/ShirePull) copies from
-- Shire's Salesforce ONLY what these reports use (Steve): the WON opportunities - Id, Close Date, Amount, Income,
-- owner and whether the funder was Performance Finance. No company or deal names, directors, addresses or notes.
--
-- The figures MIRROR Shire's own Salesforce reports (Steve: "figures should match"): a sale is an opportunity at
-- Closed Won, counted on its Close Date (their "Closed Won ... by Salesperson" and "Current Month ..." reports),
-- Amount = Amount Financed, Income = Shire's Income. A deal that falls through is moved off Closed Won in
-- Salesforce (there is no "unwound" stage), so it simply drops out of the next copy - as from their reports.
-- ============================================================================================================

IF SCHEMA_ID(N'Shire') IS NULL EXEC(N'CREATE SCHEMA Shire');
GO

-- The copy lands here first (bulk copy), then Shire.spPublish swaps it into Shire.Sale in one transaction.
CREATE TABLE Shire.StageSale
(
    OpportunityId  char(18)       NULL,
    CloseDate      date           NULL,
    Amount         decimal(18,2)  NULL,
    Income         decimal(18,2)  NULL,
    OwnerId        char(18)       NULL,
    Salesperson    nvarchar(121)  NULL,
    ProposedToPF   bit            NULL
);
GO

-- One row per won opportunity, as at the last copy. ProposedToPF = its funder is Performance Finance (Shire keeps
-- one opportunity per funder, so the PF copy of a deal is the one with Funder = Performance Finance).
CREATE TABLE Shire.Sale
(
    OpportunityId  char(18)       NOT NULL,
    CloseDate      date           NOT NULL,
    Amount         decimal(18,2)  NOT NULL,
    Income         decimal(18,2)  NOT NULL,
    OwnerId        char(18)       NOT NULL,
    Salesperson    nvarchar(121)  NOT NULL,
    ProposedToPF   bit            NOT NULL,
    CONSTRAINT PK_ShireSale PRIMARY KEY (OpportunityId)
);
CREATE INDEX IX_ShireSale_CloseDate ON Shire.Sale (CloseDate) INCLUDE (Amount, Income, OwnerId, ProposedToPF);
GO

-- One row: when the copy last checked Salesforce and last published, and Salesforce's own last-change time it saw.
CREATE TABLE Shire.Setting
(
    SettingId         tinyint       NOT NULL  DEFAULT 1,
    LastCheckUtc      datetime2(0)  NULL,      -- the last check that reached Salesforce (copied or not)
    LastCopyUtc       datetime2(0)  NULL,      -- the last publish
    SourceChangedUtc  datetime2(0)  NULL,      -- Salesforce's newest change (SystemModstamp) in the published data
    SaleCount         int           NULL,
    CONSTRAINT PK_ShireSetting PRIMARY KEY (SettingId),
    CONSTRAINT CK_ShireSetting_OneRow CHECK (SettingId = 1)
);
INSERT Shire.Setting (SettingId) VALUES (1);
GO

-- Every copy (published or failed) - Settings > Shire sync.
CREATE TABLE Shire.LoadRun
(
    LoadRunId         int IDENTITY(1,1)  NOT NULL,
    StartedUtc        datetime2(0)       NOT NULL,
    FinishedUtc       datetime2(0)       NULL,
    Succeeded         bit                NULL,
    SaleCount         int                NULL,
    SourceChangedUtc  datetime2(0)       NULL,
    Error             nvarchar(2000)     NULL,
    CONSTRAINT PK_ShireLoadRun PRIMARY KEY (LoadRunId)
);
CREATE INDEX IX_ShireLoadRun_StartedUtc ON Shire.LoadRun (StartedUtc DESC);
GO

-- The Shire reports' "as at": today in the UK (Shire's own reports are relative to today, e.g. "This Month").
CREATE FUNCTION Shire.fnReportDate ()
RETURNS date
AS
BEGIN
    RETURN CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'GMT Standard Time' AS date);
END
GO

-- ---- the copy (kpi_push) ------------------------------------------------------------------------------------

CREATE PROCEDURE Shire.spPublish
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
        INSERT Shire.Sale (OpportunityId, CloseDate, Amount, Income, OwnerId, Salesperson, ProposedToPF)
        SELECT OpportunityId, CloseDate, ISNULL(Amount, 0), ISNULL(Income, 0), OwnerId, ISNULL(Salesperson, N''), ISNULL(ProposedToPF, 0)
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

-- A copy that failed before publishing (signing in to Salesforce, the query, the bulk copy).
CREATE PROCEDURE Shire.spRecordFailure
(
    @StartedUtc        datetime2(0),
    @Error             nvarchar(2000),
    @SourceChangedUtc  datetime2(0) = NULL
)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT Shire.LoadRun (StartedUtc, FinishedUtc, Succeeded, SourceChangedUtc, Error)
    VALUES (@StartedUtc, SYSUTCDATETIME(), 0, @SourceChangedUtc, LEFT(@Error, 2000));
END
GO

-- A check that reached Salesforce and found nothing new since the last copy: the figures are still current.
CREATE PROCEDURE Shire.spChecked
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE Shire.Setting SET LastCheckUtc = SYSUTCDATETIME() WHERE SettingId = 1;
END
GO

-- ---- status and history (kpi_site) ----------------------------------------------------------------------------

-- Stale = no successful check for 45 minutes in office hours (as the CMS copy's overdue rule; a check with nothing new
-- still counts - Shire's data only changes when Shire works on it).
CREATE PROCEDURE Shire.spDataStatus
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
        Shire.fnReportDate() AS ReportDate,
        S.LastCheckUtc,
        S.LastCopyUtc,
        S.SourceChangedUtc,
        S.SaleCount,
        CAST(CASE WHEN @InOfficeHours = 1
                   AND (S.LastCheckUtc IS NULL OR S.LastCheckUtc < DATEADD(MINUTE, -45, @NowUtc))
                  THEN 1 ELSE 0 END AS bit) AS Stale
    FROM   Shire.Setting S
    WHERE  S.SettingId = 1;
END
GO

CREATE PROCEDURE Shire.spLoadHistory
(
    @Top int = 50
)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Top)
           LoadRunId, StartedUtc, FinishedUtc, SaleCount, SourceChangedUtc,
           -- open for over 15 minutes = cut off part-way (the next publish also closes it so)
           CASE WHEN Succeeded IS NULL AND FinishedUtc IS NULL AND StartedUtc < DATEADD(MINUTE, -15, SYSUTCDATETIME())
                THEN CAST(0 AS bit) ELSE Succeeded END AS Succeeded,
           CASE WHEN Succeeded IS NULL AND FinishedUtc IS NULL AND StartedUtc < DATEADD(MINUTE, -15, SYSUTCDATETIME())
                THEN 'Stopped part-way: the site restarted or lost its connection (nothing was published)' ELSE Error END AS Error,
           DATEDIFF(SECOND, StartedUtc, FinishedUtc) AS Seconds
    FROM   Shire.LoadRun
    ORDER BY StartedUtc DESC, LoadRunId DESC;
END
GO

-- ---- the reports (kpi_site) -------------------------------------------------------------------------------------
-- @Measure: 'AF' = Amount (Amount Financed) | 'Income' = Income.  @ProposedToPF: NULL = all, 1 = yes, 0 = no.

-- The salespeople (opportunity owners) with a sale in the last ten years: the Salesperson filter.
CREATE PROCEDURE Shire.spSalespeople
AS
BEGIN
    SET NOCOUNT ON;
    SELECT   OwnerId AS Id, MAX(Salesperson) AS Name
    FROM     Shire.Sale
    WHERE    YEAR(CloseDate) > YEAR(Shire.fnReportDate()) - 10
    GROUP BY OwnerId
    ORDER BY MAX(Salesperson);
END
GO

-- Shire Year on Year: as PF's Rpt.spYearOnYear - the last ten years by month or quarter, and each period's figure up
-- to today's day of the year (TotalToDate), which the current year is compared with.
CREATE PROCEDURE Shire.spYearOnYear
(
    @Measure       nvarchar(10) = 'AF',
    @Interval      nvarchar(10) = 'Month',
    @OwnerId       char(18)     = NULL,     -- NULL = every salesperson
    @ProposedToPF  bit          = NULL
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @AsOf date = Shire.fnReportDate();
    DECLARE @M int = MONTH(@AsOf), @D int = DAY(@AsOf);

    SELECT
        YEAR(S.CloseDate)                                                          AS [Year],
        CASE WHEN @Interval = 'Quarter' THEN DATEPART(QUARTER, S.CloseDate)
             ELSE MONTH(S.CloseDate) END                                           AS Period,
        SUM(CASE WHEN @Measure = 'Income' THEN S.Income ELSE S.Amount END)         AS Total,
        SUM(CASE WHEN MONTH(S.CloseDate) < @M OR (MONTH(S.CloseDate) = @M AND DAY(S.CloseDate) <= @D)
                 THEN CASE WHEN @Measure = 'Income' THEN S.Income ELSE S.Amount END ELSE 0 END)   AS TotalToDate
    FROM   Shire.Sale S
    WHERE  YEAR(S.CloseDate) > YEAR(@AsOf) - 10
      AND  (@OwnerId      IS NULL OR S.OwnerId      = @OwnerId)
      AND  (@ProposedToPF IS NULL OR S.ProposedToPF = @ProposedToPF)
    GROUP BY
        YEAR(S.CloseDate),
        CASE WHEN @Interval = 'Quarter' THEN DATEPART(QUARTER, S.CloseDate) ELSE MONTH(S.CloseDate) END
    ORDER BY [Year], Period;
END
GO

-- Shire Sales Performance: a year's sales by salesperson (opportunity owner), highest first - as their
-- "Closed Won ... by Salesperson" report.
CREATE PROCEDURE Shire.spSalesLeague
(
    @Year          int,
    @Measure       nvarchar(10) = 'AF',
    @ProposedToPF  bit          = NULL
)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        @Year                                                                AS Yr,
        S.OwnerId,
        MAX(S.Salesperson)                                                   AS Salesperson,
        SUM(CASE WHEN @Measure = 'Income' THEN S.Income ELSE S.Amount END)   AS MeasureValue,
        COUNT(*)                                                             AS Deals
    FROM   Shire.Sale S
    WHERE  YEAR(S.CloseDate) = @Year
      AND  (@ProposedToPF IS NULL OR S.ProposedToPF = @ProposedToPF)
    GROUP BY S.OwnerId
    ORDER BY MeasureValue DESC, Salesperson;
END
GO

-- ---- rights -------------------------------------------------------------------------------------------------------
GRANT EXECUTE ON Shire.spDataStatus   TO kpi_site;
GRANT EXECUTE ON Shire.spLoadHistory  TO kpi_site;
GRANT EXECUTE ON Shire.spSalespeople  TO kpi_site;
GRANT EXECUTE ON Shire.spYearOnYear   TO kpi_site;
GRANT EXECUTE ON Shire.spSalesLeague  TO kpi_site;
GRANT EXECUTE ON Shire.spSalespeople  TO kpi_reader;
GRANT EXECUTE ON Shire.spYearOnYear   TO kpi_reader;
GRANT EXECUTE ON Shire.spSalesLeague  TO kpi_reader;
GRANT SELECT, INSERT, DELETE ON Shire.StageSale TO kpi_push;
GRANT EXECUTE ON Shire.spPublish        TO kpi_push;
GRANT EXECUTE ON Shire.spRecordFailure  TO kpi_push;
GRANT EXECUTE ON Shire.spChecked        TO kpi_push;
