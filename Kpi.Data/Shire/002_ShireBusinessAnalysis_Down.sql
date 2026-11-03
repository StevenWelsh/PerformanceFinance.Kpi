-- Undoes 002_ShireBusinessAnalysis.sql (migration ShireBusinessAnalysis, Down): the two procedures, Shire.spPublish
-- back as 001 left it, then the four columns.
DROP PROCEDURE IF EXISTS Shire.spBusinessAnalysisByLender;
DROP PROCEDURE IF EXISTS Shire.spBusinessAnalysis;
GO

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

ALTER TABLE Shire.Sale DROP COLUMN Sector, FinanceProduct, LoanPurpose, Lender;
ALTER TABLE Shire.StageSale DROP COLUMN Sector, FinanceProduct, LoanPurpose, Lender;
