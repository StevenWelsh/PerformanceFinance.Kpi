using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>
    /// The copy slowed from about a minute to 15 (29 Oct 2026, Steve): it emptied the Stage tables with DELETE, and on a
    /// heap (Stage has no keys) under read-committed snapshot a DELETE leaves its pages allocated - Stage.Lead had grown to
    /// 4.3 GB for 25,000 rows, read in full by every copy, until the publish ran past the site's 10-minute limit. Now the
    /// copy empties each table with Stage.spClear (TRUNCATE, which frees the pages). And a publish the site gives up on
    /// is closed with the real reason (Stage.spRecordFailure @CloseOpenRun) instead of later as "the site restarted".
    /// </summary>
    public partial class StageClear : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TRUNCATE needs ALTER on the table, which kpi_push doesn't have and shouldn't: Stage.spClear runs as a user with
            // no login that may alter Stage's tables and nothing else. Not EXECUTE AS OWNER: the owner (dbo) can be a Windows
            // account the server can't look up - locally it failed "Could not obtain information about Windows NT group/user".
            migrationBuilder.Sql("""
IF USER_ID(N'kpi_stage_owner') IS NULL CREATE USER kpi_stage_owner WITHOUT LOGIN;
GRANT ALTER ON SCHEMA::Stage TO kpi_stage_owner;
""");
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Stage.spClear
(
    @Table sysname   -- a Stage table's name, without the schema
)
WITH EXECUTE AS 'kpi_stage_owner'   -- may TRUNCATE Stage's tables; kpi_push may only run this
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE schema_id = SCHEMA_ID(N'Stage') AND name = @Table)
        THROW 50003, 'Stage.spClear: that is not a Stage table.', 1;

    DECLARE @Sql nvarchar(400) = N'TRUNCATE TABLE Stage.' + QUOTENAME(@Table) + N';';
    EXEC sys.sp_executesql @Sql;
END
""");
            migrationBuilder.Sql("""
GRANT EXECUTE ON Stage.spClear TO kpi_push;
""");
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Stage.spRecordFailure
(
    @Scope              varchar(10),
    @StartedUtc         datetime2(0),
    @Error              nvarchar(2000),
    @SourceRefreshedUtc datetime2(0) = NULL,
    @CloseOpenRun       bit = 0   -- the site gave up on a PUBLISH (@StartedUtc = the one it passed to Stage.spPublish)
)
AS
BEGIN
    SET NOCOUNT ON;

    IF @CloseOpenRun = 1
    BEGIN
        -- a publish the site stopped (its time limit) or lost the connection to never reaches spPublish's own CATCH, so
        -- its run would sit open until the next publish closed it as "stopped part-way": close it now, with the reason
        UPDATE Ref.LoadRun
        SET    FinishedUtc = SYSUTCDATETIME(), Succeeded = 0, Error = LEFT(@Error, 2000)
        WHERE  Scope = @Scope AND StartedUtc = @StartedUtc AND Succeeded IS NULL AND FinishedUtc IS NULL;

        -- closed now, or already closed by spPublish (it caught the error itself): nothing more to record
        IF EXISTS (SELECT 1 FROM Ref.LoadRun WHERE Scope = @Scope AND StartedUtc = @StartedUtc) RETURN;
    END

    INSERT Ref.LoadRun (Scope, StartedUtc, FinishedUtc, Succeeded, Error, SourceRefreshedUtc)
    VALUES (@Scope, @StartedUtc, SYSUTCDATETIME(), 0, LEFT(@Error, 2000), @SourceRefreshedUtc);
END
""");
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Stage.spPublish
(
    @Scope              varchar(10),
    @ReportDate         date,
    @StartedUtc         datetime2(0) = NULL,   -- when the COPY began (the site), so Started / Took cover the whole copy
    @SourceRefreshedUtc datetime2(0) = NULL,   -- when the office made the data this copy carries (KpiCopy.Refresh)
    @SourceSnapshotsUtc datetime2(0) = NULL    -- a Nightly copy: when the office prepared Bad Debt & Exposure
)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- a run left open for 15 minutes was cut off part-way: the publish itself rolled back, and the site couldn't say why
    -- (it closes its own run when it gives up - Stage.spRecordFailure @CloseOpenRun - so this is a restart, or a
    -- connection lost for good): close it as failed, so Data sync doesn't show it "running" for ever
    UPDATE Ref.LoadRun
    SET    Succeeded = 0, Error = 'Stopped part-way: the KPI site restarted, or lost its connection to its own database (CMS_KPI), while publishing. Nothing was published.'
    WHERE  Succeeded IS NULL AND FinishedUtc IS NULL AND StartedUtc < DATEADD(MINUTE, -15, SYSUTCDATETIME());

    DECLARE @RunId int;
    INSERT Ref.LoadRun (Scope, StartedUtc, SourceRefreshedUtc, SourceSnapshotsUtc)
    VALUES (@Scope, ISNULL(@StartedUtc, SYSUTCDATETIME()), @SourceRefreshedUtc, @SourceSnapshotsUtc);
    SET @RunId = SCOPE_IDENTITY();

    BEGIN TRY
        IF NOT EXISTS (SELECT 1 FROM Stage.Lead)
            THROW 50002, 'Stage.Lead is empty: not publishing (the site keeps the last good figures).', 1;

        BEGIN TRANSACTION;
        DELETE Dim.SalesOffice;
        INSERT Dim.SalesOffice (SalesOfficeId, OfficeName, OrderInt, IsActive)
        SELECT SalesOfficeId, OfficeName, OrderInt, IsActive FROM Stage.SalesOffice;
        DELETE Dim.Salesperson;
        INSERT Dim.Salesperson (SalesId, Salesperson, SalesCode, DivisionId, SalesOfficeId, IsActive)
        SELECT SalesId, Salesperson, SalesCode, DivisionId, SalesOfficeId, IsActive FROM Stage.Salesperson;
        DELETE Dim.Period;
        INSERT Dim.Period (PeriodId, PeriodNumber, PeriodName, PeriodStartDate, PeriodEndDate, QuarterId, QuarterNumber, QuarterName, YearId, YearNumber, YearName)
        SELECT PeriodId, PeriodNumber, PeriodName, PeriodStartDate, PeriodEndDate, QuarterId, QuarterNumber, QuarterName, YearId, YearNumber, YearName FROM Stage.Period;
        DELETE Dim.WorkingDay;
        INSERT Dim.WorkingDay (WorkingDay)
        SELECT WorkingDay FROM Stage.WorkingDay;
        DELETE Dim.SalesTarget;
        INSERT Dim.SalesTarget (SalesId, Year, AFTarget, TFITarget)
        SELECT SalesId, Year, AFTarget, TFITarget FROM Stage.SalesTarget;
        DELETE Dim.FundLender;
        INSERT Dim.FundLender (FundLenderId, FundLenderName)
        SELECT FundLenderId, FundLenderName FROM Stage.FundLender;
        DELETE Dim.Fund;
        INSERT Dim.Fund (FundId, FundName, FundLenderId)
        SELECT FundId, FundName, FundLenderId FROM Stage.Fund;
        DELETE Fact.Lead;
        INSERT Fact.Lead (LeadId, dtPayReceived, IsUnwound, AF, GFI, TFI, SalesII, DivisionId, Division, SalesId, Salesperson, SalesCode, SalesOfficeId, LenderName, Profession, Facility, Arrangement, IsOB, IsOptical, IsNewCustomer, IRR, IRRInclFees, Term, MinIRR, PeriodId, PostcodeArea)
        SELECT LeadId, dtPayReceived, IsUnwound, AF, GFI, TFI, SalesII, DivisionId, Division, SalesId, Salesperson, SalesCode, SalesOfficeId, LenderName, Profession, Facility, Arrangement, IsOB, IsOptical, IsNewCustomer, IRR, IRRInclFees, Term, MinIRR, PeriodId, PostcodeArea FROM Stage.Lead;
        DELETE Fact.LeadPipeline;
        INSERT Fact.LeadPipeline (LeadId, SalesId, dtLead, LeadStatus, AF, GFI, dtAppAccept, dtActivation, IntroducerId, IntroducerManagedByHQ, IntroducerAccountManagerId)
        SELECT LeadId, SalesId, dtLead, LeadStatus, AF, GFI, dtAppAccept, dtActivation, IntroducerId, IntroducerManagedByHQ, IntroducerAccountManagerId FROM Stage.LeadPipeline;
        DELETE Fact.SupplierYear;
        INSERT Fact.SupplierYear (Year, IntroducerId, Supplier, Profession, AccountManagerId, ManagedByHQ, AmountFinanced)
        SELECT Year, IntroducerId, Supplier, Profession, AccountManagerId, ManagedByHQ, AmountFinanced FROM Stage.SupplierYear;
        DELETE Fact.BookPosition;
        INSERT Fact.BookPosition (LeadId, IsOB, RemainingCapital, CustomerKey, PostcodeArea)
        SELECT LeadId, IsOB, RemainingCapital, CustomerKey, PostcodeArea FROM Stage.BookPosition;
        DELETE Fact.FundingLine;
        INSERT Fact.FundingLine (Source, SourceId, LeadId, EventDate, DivisionId, Amount, Term, Rate, IsBulkMove)
        SELECT Source, SourceId, LeadId, EventDate, DivisionId, Amount, Term, Rate, IsBulkMove FROM Stage.FundingLine;

        IF @Scope = 'Nightly'
        BEGIN
            DELETE Fact.BadDebtYear;
            INSERT Fact.BadDebtYear (IsAutoApproved, BadDebtYear, Id, YearStartDate, YearEndDate, AmountFinanced, RemainingCapital, ActiveLeadCount, WrittenOffAmount, WrittenOffLeadCount, RecoveredAmount, BadDebtCosts, TerminatedLeadCount, ArrearsLeadCount, ArrearsCompanyCount)
            SELECT IsAutoApproved, BadDebtYear, Id, YearStartDate, YearEndDate, AmountFinanced, RemainingCapital, ActiveLeadCount, WrittenOffAmount, WrittenOffLeadCount, RecoveredAmount, BadDebtCosts, TerminatedLeadCount, ArrearsLeadCount, ArrearsCompanyCount FROM Stage.BadDebtYear;
            DELETE Fact.Exposure;
            INSERT Fact.Exposure (GroupingKey, FundLenderId, FundId, RowNumber, Id, Name, LeadCount, RemainingCapital, TotalRemainingCapital, PercentRemainingCapital, CustomerType)
            SELECT GroupingKey, FundLenderId, FundId, RowNumber, Id, Name, LeadCount, RemainingCapital, TotalRemainingCapital, PercentRemainingCapital, CustomerType FROM Stage.Exposure;
        END

        UPDATE Ref.Setting
        SET    ReportDate          = @ReportDate,
               LastFrequentLoadUtc = SYSUTCDATETIME(),
               LastNightlyLoadUtc  = CASE WHEN @Scope = 'Nightly' THEN SYSUTCDATETIME() ELSE LastNightlyLoadUtc END
        WHERE  SettingId = 1;
        COMMIT TRANSACTION;

        UPDATE Ref.LoadRun SET FinishedUtc = SYSUTCDATETIME(), Succeeded = 1 WHERE LoadRunId = @RunId;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        UPDATE Ref.LoadRun
        SET    FinishedUtc = SYSUTCDATETIME(), Succeeded = 0, Error = LEFT(ERROR_MESSAGE(), 2000)
        WHERE  LoadRunId = @RunId;
        THROW;
    END CATCH
END
""");
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spLoadHistory
(
    @Top int = 50
)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Top)
           LoadRunId, Scope, StartedUtc, FinishedUtc, SourceRefreshedUtc, SourceSnapshotsUtc,
           -- open for over 15 minutes = cut off part-way (the next publish also closes it so)
           CASE WHEN Succeeded IS NULL AND FinishedUtc IS NULL AND StartedUtc < DATEADD(MINUTE, -15, SYSUTCDATETIME())
                THEN CAST(0 AS bit) ELSE Succeeded END AS Succeeded,
           CASE WHEN Succeeded IS NULL AND FinishedUtc IS NULL AND StartedUtc < DATEADD(MINUTE, -15, SYSUTCDATETIME())
                THEN 'Stopped part-way: the KPI site restarted, or lost its connection to its own database (CMS_KPI), while publishing. Nothing was published.' ELSE Error END AS Error,
           DATEDIFF(SECOND, StartedUtc, FinishedUtc) AS Seconds
    FROM   Ref.LoadRun
    ORDER BY StartedUtc DESC, LoadRunId DESC;
END
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spLoadHistory
(
    @Top int = 50
)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Top)
           LoadRunId, Scope, StartedUtc, FinishedUtc, SourceRefreshedUtc, SourceSnapshotsUtc,
           -- open for over 15 minutes = cut off part-way (the next publish also closes it so)
           CASE WHEN Succeeded IS NULL AND FinishedUtc IS NULL AND StartedUtc < DATEADD(MINUTE, -15, SYSUTCDATETIME())
                THEN CAST(0 AS bit) ELSE Succeeded END AS Succeeded,
           CASE WHEN Succeeded IS NULL AND FinishedUtc IS NULL AND StartedUtc < DATEADD(MINUTE, -15, SYSUTCDATETIME())
                THEN 'Stopped part-way: the site restarted or lost its connection (nothing was published)' ELSE Error END AS Error,
           DATEDIFF(SECOND, StartedUtc, FinishedUtc) AS Seconds
    FROM   Ref.LoadRun
    ORDER BY StartedUtc DESC, LoadRunId DESC;
END
""");
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Stage.spPublish
(
    @Scope              varchar(10),
    @ReportDate         date,
    @StartedUtc         datetime2(0) = NULL,   -- when the COPY began (the site), so Started / Took cover the whole copy
    @SourceRefreshedUtc datetime2(0) = NULL,   -- when the office made the data this copy carries (KpiCopy.Refresh)
    @SourceSnapshotsUtc datetime2(0) = NULL    -- a Nightly copy: when the office prepared Bad Debt & Exposure
)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- a run left open for 15 minutes was cut off part-way (the site restarted or lost its connection mid-publish; the
    -- publish itself rolled back): close it as failed, so Data sync doesn't show it "running" for ever
    UPDATE Ref.LoadRun
    SET    Succeeded = 0, Error = 'Stopped part-way: the site restarted or lost its connection (nothing was published)'
    WHERE  Succeeded IS NULL AND FinishedUtc IS NULL AND StartedUtc < DATEADD(MINUTE, -15, SYSUTCDATETIME());

    DECLARE @RunId int;
    INSERT Ref.LoadRun (Scope, StartedUtc, SourceRefreshedUtc, SourceSnapshotsUtc)
    VALUES (@Scope, ISNULL(@StartedUtc, SYSUTCDATETIME()), @SourceRefreshedUtc, @SourceSnapshotsUtc);
    SET @RunId = SCOPE_IDENTITY();

    BEGIN TRY
        IF NOT EXISTS (SELECT 1 FROM Stage.Lead)
            THROW 50002, 'Stage.Lead is empty: not publishing (the site keeps the last good figures).', 1;

        BEGIN TRANSACTION;
        DELETE Dim.SalesOffice;
        INSERT Dim.SalesOffice (SalesOfficeId, OfficeName, OrderInt, IsActive)
        SELECT SalesOfficeId, OfficeName, OrderInt, IsActive FROM Stage.SalesOffice;
        DELETE Dim.Salesperson;
        INSERT Dim.Salesperson (SalesId, Salesperson, SalesCode, DivisionId, SalesOfficeId, IsActive)
        SELECT SalesId, Salesperson, SalesCode, DivisionId, SalesOfficeId, IsActive FROM Stage.Salesperson;
        DELETE Dim.Period;
        INSERT Dim.Period (PeriodId, PeriodNumber, PeriodName, PeriodStartDate, PeriodEndDate, QuarterId, QuarterNumber, QuarterName, YearId, YearNumber, YearName)
        SELECT PeriodId, PeriodNumber, PeriodName, PeriodStartDate, PeriodEndDate, QuarterId, QuarterNumber, QuarterName, YearId, YearNumber, YearName FROM Stage.Period;
        DELETE Dim.WorkingDay;
        INSERT Dim.WorkingDay (WorkingDay)
        SELECT WorkingDay FROM Stage.WorkingDay;
        DELETE Dim.SalesTarget;
        INSERT Dim.SalesTarget (SalesId, Year, AFTarget, TFITarget)
        SELECT SalesId, Year, AFTarget, TFITarget FROM Stage.SalesTarget;
        DELETE Dim.FundLender;
        INSERT Dim.FundLender (FundLenderId, FundLenderName)
        SELECT FundLenderId, FundLenderName FROM Stage.FundLender;
        DELETE Dim.Fund;
        INSERT Dim.Fund (FundId, FundName, FundLenderId)
        SELECT FundId, FundName, FundLenderId FROM Stage.Fund;
        DELETE Fact.Lead;
        INSERT Fact.Lead (LeadId, dtPayReceived, IsUnwound, AF, GFI, TFI, SalesII, DivisionId, Division, SalesId, Salesperson, SalesCode, SalesOfficeId, LenderName, Profession, Facility, Arrangement, IsOB, IsOptical, IsNewCustomer, IRR, IRRInclFees, Term, MinIRR, PeriodId, PostcodeArea)
        SELECT LeadId, dtPayReceived, IsUnwound, AF, GFI, TFI, SalesII, DivisionId, Division, SalesId, Salesperson, SalesCode, SalesOfficeId, LenderName, Profession, Facility, Arrangement, IsOB, IsOptical, IsNewCustomer, IRR, IRRInclFees, Term, MinIRR, PeriodId, PostcodeArea FROM Stage.Lead;
        DELETE Fact.LeadPipeline;
        INSERT Fact.LeadPipeline (LeadId, SalesId, dtLead, LeadStatus, AF, GFI, dtAppAccept, dtActivation, IntroducerId, IntroducerManagedByHQ, IntroducerAccountManagerId)
        SELECT LeadId, SalesId, dtLead, LeadStatus, AF, GFI, dtAppAccept, dtActivation, IntroducerId, IntroducerManagedByHQ, IntroducerAccountManagerId FROM Stage.LeadPipeline;
        DELETE Fact.SupplierYear;
        INSERT Fact.SupplierYear (Year, IntroducerId, Supplier, Profession, AccountManagerId, ManagedByHQ, AmountFinanced)
        SELECT Year, IntroducerId, Supplier, Profession, AccountManagerId, ManagedByHQ, AmountFinanced FROM Stage.SupplierYear;
        DELETE Fact.BookPosition;
        INSERT Fact.BookPosition (LeadId, IsOB, RemainingCapital, CustomerKey, PostcodeArea)
        SELECT LeadId, IsOB, RemainingCapital, CustomerKey, PostcodeArea FROM Stage.BookPosition;
        DELETE Fact.FundingLine;
        INSERT Fact.FundingLine (Source, SourceId, LeadId, EventDate, DivisionId, Amount, Term, Rate, IsBulkMove)
        SELECT Source, SourceId, LeadId, EventDate, DivisionId, Amount, Term, Rate, IsBulkMove FROM Stage.FundingLine;

        IF @Scope = 'Nightly'
        BEGIN
            DELETE Fact.BadDebtYear;
            INSERT Fact.BadDebtYear (IsAutoApproved, BadDebtYear, Id, YearStartDate, YearEndDate, AmountFinanced, RemainingCapital, ActiveLeadCount, WrittenOffAmount, WrittenOffLeadCount, RecoveredAmount, BadDebtCosts, TerminatedLeadCount, ArrearsLeadCount, ArrearsCompanyCount)
            SELECT IsAutoApproved, BadDebtYear, Id, YearStartDate, YearEndDate, AmountFinanced, RemainingCapital, ActiveLeadCount, WrittenOffAmount, WrittenOffLeadCount, RecoveredAmount, BadDebtCosts, TerminatedLeadCount, ArrearsLeadCount, ArrearsCompanyCount FROM Stage.BadDebtYear;
            DELETE Fact.Exposure;
            INSERT Fact.Exposure (GroupingKey, FundLenderId, FundId, RowNumber, Id, Name, LeadCount, RemainingCapital, TotalRemainingCapital, PercentRemainingCapital, CustomerType)
            SELECT GroupingKey, FundLenderId, FundId, RowNumber, Id, Name, LeadCount, RemainingCapital, TotalRemainingCapital, PercentRemainingCapital, CustomerType FROM Stage.Exposure;
        END

        UPDATE Ref.Setting
        SET    ReportDate          = @ReportDate,
               LastFrequentLoadUtc = SYSUTCDATETIME(),
               LastNightlyLoadUtc  = CASE WHEN @Scope = 'Nightly' THEN SYSUTCDATETIME() ELSE LastNightlyLoadUtc END
        WHERE  SettingId = 1;
        COMMIT TRANSACTION;

        UPDATE Ref.LoadRun SET FinishedUtc = SYSUTCDATETIME(), Succeeded = 1 WHERE LoadRunId = @RunId;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        UPDATE Ref.LoadRun
        SET    FinishedUtc = SYSUTCDATETIME(), Succeeded = 0, Error = LEFT(ERROR_MESSAGE(), 2000)
        WHERE  LoadRunId = @RunId;
        THROW;
    END CATCH
END
""");
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Stage.spRecordFailure
(
    @Scope      varchar(10),
    @StartedUtc datetime2(0),
    @Error      nvarchar(2000),
    @SourceRefreshedUtc datetime2(0) = NULL
)
AS
BEGIN
    SET NOCOUNT ON;

    INSERT Ref.LoadRun (Scope, StartedUtc, FinishedUtc, Succeeded, Error, SourceRefreshedUtc)
    VALUES (@Scope, @StartedUtc, SYSUTCDATETIME(), 0, LEFT(@Error, 2000), @SourceRefreshedUtc);
END
""");
            migrationBuilder.Sql("""
DROP PROCEDURE IF EXISTS Stage.spClear;
IF USER_ID(N'kpi_stage_owner') IS NOT NULL DROP USER kpi_stage_owner;
""");
        }
    }
}
