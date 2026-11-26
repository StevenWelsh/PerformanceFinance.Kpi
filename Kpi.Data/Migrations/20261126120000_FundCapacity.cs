using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>
    /// Fund Capacity (26 Nov 2026, Steve - for Stuart): CMS's Own Book > Fund Lender Capacity on the KPI site, with the
    /// rate for new funding. The CMS side widened KpiExport.vFundLender (capital limit, "active block lender") and
    /// KpiExport.vFund (type, capacity exclusions, capital out, an RCF's / IWFA's own limit, the current rate, the IWFA's
    /// service fee); this adds those columns to Stage and Dim, carries them in Stage.spPublish, and adds Rpt.spFundCapacity.
    /// No EF model change.
    /// </summary>
    public partial class FundCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Stage: wide and nullable, as every Stage table (SqlBulkCopy truncates decimals; spPublish converts)
            migrationBuilder.Sql("""
ALTER TABLE Stage.FundLender ADD CapitalLimit decimal(38,12) NULL, IsActiveBlockLender bit NULL;
ALTER TABLE Stage.Fund ADD FundType nvarchar(max) NULL, ExcludeFromCapacity bit NULL, ExcludeFromCapacityPercent decimal(38,12) NULL,
    LenderRef nvarchar(max) NULL, StartDate datetime2 NULL, EndDate datetime2 NULL, BlockAmount decimal(38,12) NULL,
    CapitalOS decimal(38,12) NULL, FacilityLimit decimal(38,12) NULL, CurrentRate decimal(38,12) NULL, ServiceFeeRate decimal(38,12) NULL;
""");
            migrationBuilder.Sql("""
ALTER TABLE Dim.FundLender ADD CapitalLimit decimal(18,2) NULL,
    IsActiveBlockLender bit NOT NULL CONSTRAINT DF_FundLender_IsActiveBlockLender DEFAULT 0;
ALTER TABLE Dim.Fund ADD FundType varchar(10) NULL,
    ExcludeFromCapacity bit NOT NULL CONSTRAINT DF_Fund_ExcludeFromCapacity DEFAULT 0,
    ExcludeFromCapacityPercent decimal(9,4) NULL, LenderRef nvarchar(100) NULL, StartDate date NULL, EndDate date NULL,
    BlockAmount decimal(18,2) NULL, CapitalOS decimal(18,2) NULL, FacilityLimit decimal(18,2) NULL,
    CurrentRate decimal(12,8) NULL, ServiceFeeRate decimal(12,8) NULL;
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
        INSERT Dim.FundLender (FundLenderId, FundLenderName, CapitalLimit, IsActiveBlockLender)
        SELECT FundLenderId, FundLenderName, CapitalLimit, ISNULL(IsActiveBlockLender, 0) FROM Stage.FundLender;
        DELETE Dim.Fund;
        INSERT Dim.Fund (FundId, FundName, FundLenderId, FundType, ExcludeFromCapacity, ExcludeFromCapacityPercent, LenderRef,
                         StartDate, EndDate, BlockAmount, CapitalOS, FacilityLimit, CurrentRate, ServiceFeeRate)
        SELECT FundId, FundName, FundLenderId, FundType, ISNULL(ExcludeFromCapacity, 0), ExcludeFromCapacityPercent, LenderRef,
               StartDate, EndDate, BlockAmount, CapitalOS, FacilityLimit, CurrentRate, ServiceFeeRate FROM Stage.Fund;
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS Rpt.spFundCapacity;");
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
ALTER TABLE Dim.Fund DROP CONSTRAINT DF_Fund_ExcludeFromCapacity;
ALTER TABLE Dim.Fund DROP COLUMN FundType, ExcludeFromCapacity, ExcludeFromCapacityPercent, LenderRef, StartDate, EndDate,
    BlockAmount, CapitalOS, FacilityLimit, CurrentRate, ServiceFeeRate;
ALTER TABLE Dim.FundLender DROP CONSTRAINT DF_FundLender_IsActiveBlockLender;
ALTER TABLE Dim.FundLender DROP COLUMN CapitalLimit, IsActiveBlockLender;
ALTER TABLE Stage.Fund DROP COLUMN FundType, ExcludeFromCapacity, ExcludeFromCapacityPercent, LenderRef, StartDate, EndDate,
    BlockAmount, CapitalOS, FacilityLimit, CurrentRate, ServiceFeeRate;
ALTER TABLE Stage.FundLender DROP COLUMN CapitalLimit, IsActiveBlockLender;
""");
        }
    }
}
