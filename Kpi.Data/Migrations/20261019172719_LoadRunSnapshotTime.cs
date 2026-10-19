using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>
    /// Settings > Data sync's "Data from" (19 Oct 2026, Steve): a Nightly copy also records when the office prepared Bad
    /// Debt & Exposure (Ref.LoadRun.SourceSnapshotsUtc), beside when it made the day's figures (SourceRefreshedUtc).
    /// </summary>
    public partial class LoadRunSnapshotTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
IF COL_LENGTH('Ref.LoadRun', 'SourceSnapshotsUtc') IS NULL
    ALTER TABLE Ref.LoadRun ADD SourceSnapshotsUtc datetime2(0) NULL;
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Stage.spPublish
(
    @Scope              varchar(10),
    @ReportDate         date,
    @StartedUtc         datetime2(0) = NULL,   -- when the COPY began (the site), so Started / Took cover the whole copy
    @SourceRefreshedUtc datetime2(0) = NULL    -- when the office made the data this copy carries (KpiCopy.Refresh)
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
    INSERT Ref.LoadRun (Scope, StartedUtc, SourceRefreshedUtc) VALUES (@Scope, ISNULL(@StartedUtc, SYSUTCDATETIME()), @SourceRefreshedUtc);
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
           LoadRunId, Scope, StartedUtc, FinishedUtc, SourceRefreshedUtc,
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
ALTER TABLE Ref.LoadRun DROP COLUMN SourceSnapshotsUtc;
""");
        }
    }
}
