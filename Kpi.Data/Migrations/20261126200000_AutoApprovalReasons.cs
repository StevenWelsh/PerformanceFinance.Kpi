using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>
    /// Auto Approval's two reason panels (26 Nov 2026, Steve): "Not eligible: reason" and "Eligible: not auto approved
    /// reason", from the decisions CMS records since 16 Oct 2026. The CMS side adds KpiExport.vAutoApprovalReason (each
    /// decided application checked against the rule set in force, from its decision snapshot); this adds its Stage table and
    /// Fact.AutoApprovalReason, carries it in Stage.spPublish, and gives Rpt.spAutoApproval the two reason lists.
    /// </summary>
    public partial class AutoApprovalReasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
CREATE TABLE Stage.AutoApprovalReason
(
    ApplicationId int NULL, DecisionOn datetime2 NULL, DecisionType int NULL, IsEligible bit NULL, ComputedEligible bit NULL,
    RuleId int NULL, Reason nvarchar(max) NULL, SortOrder int NULL
);
CREATE TABLE Fact.AutoApprovalReason
(
    ApplicationId    int            NOT NULL,
    DecisionOn       date           NULL,
    DecisionType     int            NULL,   -- 1 Auto approved, 2 Approved, 3 Declined
    IsEligible       bit            NULL,   -- CMS's own
    ComputedEligible bit            NULL,   -- the check's, from the snapshot (should agree)
    RuleId           int            NULL,   -- the rule the reasons come from
    Reason           nvarchar(100)  NOT NULL,
    SortOrder        int            NULL
);
CREATE INDEX IX_AutoApprovalReason_ApplicationId ON Fact.AutoApprovalReason (ApplicationId) INCLUDE (ComputedEligible, Reason, SortOrder);
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

        DELETE Dim.AutoApprovalRule;
        INSERT Dim.AutoApprovalRule (RuleId, RuleSetId, RuleSetStatus, RuleSetCreatedOn, RuleSetActivatedOn, RuleName, CreditLimitTo, FacilityLimitTo, AmountTo, TermTo)
        SELECT RuleId, RuleSetId, RuleSetStatus, RuleSetCreatedOn, RuleSetActivatedOn, LEFT(RuleName, 200), CreditLimitTo, FacilityLimitTo, AmountTo, TermTo FROM Stage.AutoApprovalRule;
        DELETE Fact.AutoApproval;
        INSERT Fact.AutoApproval (ApplicationId, LeadId, DivisionId, Division, DecisionOn, IsEligible, ApprovalRuleId, ApprovalRuleSetId, Status, AF, dtPayReceived, IsUnwound, HasSeriousArrears, IsTerminated, IsBadDebt, AmountWrittenOff)
        SELECT ApplicationId, LeadId, DivisionId, Division, DecisionOn, ISNULL(IsEligible, 0), ApprovalRuleId, ApprovalRuleSetId, Status, ISNULL(AF, 0), dtPayReceived, ISNULL(IsUnwound, 0),
               ISNULL(HasSeriousArrears, 0), ISNULL(IsTerminated, 0), ISNULL(IsBadDebt, 0), ISNULL(AmountWrittenOff, 0) FROM Stage.AutoApproval;
        DELETE Fact.AutoApprovalReason;
        INSERT Fact.AutoApprovalReason (ApplicationId, DecisionOn, DecisionType, IsEligible, ComputedEligible, RuleId, Reason, SortOrder)
        SELECT ApplicationId, DecisionOn, DecisionType, IsEligible, ComputedEligible, RuleId, LEFT(Reason, 100), SortOrder FROM Stage.AutoApprovalReason;
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
CREATE OR ALTER PROCEDURE Rpt.spAutoApproval
(
    @Part       varchar(20),        -- Funnel | Outcome | NotEligibleReasons | EligibleReasons | ByRule | Performance | Rules
    @DivisionId int = NULL,         -- NULL = every division
    @Months     int = 3             -- the period, to the report date (not Performance or Rules: since auto approval began)
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ReportDate date = (SELECT ReportDate FROM Ref.Setting WHERE SettingId = 1);
    DECLARE @From date = DATEADD(MONTH, -@Months, @ReportDate);

    -- the rule names in today's active set: any other name is an earlier rule (CMS copies rules with no link back, so a
    -- rule's versions are grouped by name)
    SELECT DISTINCT RuleName INTO #Current FROM Dim.AutoApprovalRule WHERE RuleSetStatus = 1;

    IF @Part = 'Funnel'
    BEGIN
        -- Application to paid out: decided -> eligible -> auto approved -> paid out (auto approved, paid, not unwound)
        SELECT A.DivisionId, MAX(A.Division) AS Division,
               COUNT(*)                                                                                 AS Decided,
               SUM(CASE WHEN A.IsEligible = 1 THEN 1 ELSE 0 END)                                        AS Eligible,
               SUM(CASE WHEN A.ApprovalRuleId IS NOT NULL THEN 1 ELSE 0 END)                            AS AutoApproved,
               SUM(CASE WHEN A.ApprovalRuleId IS NOT NULL AND A.dtPayReceived IS NOT NULL AND A.IsUnwound = 0
                        THEN 1 ELSE 0 END)                                                              AS PaidOut,
               CAST(SUM(A.AF) AS decimal(18,2))                                                         AS DecidedAF,
               CAST(SUM(CASE WHEN A.IsEligible = 1 THEN A.AF ELSE 0 END) AS decimal(18,2))              AS EligibleAF,
               CAST(SUM(CASE WHEN A.ApprovalRuleId IS NOT NULL THEN A.AF ELSE 0 END) AS decimal(18,2))  AS AutoApprovedAF,
               CAST(SUM(CASE WHEN A.ApprovalRuleId IS NOT NULL AND A.dtPayReceived IS NOT NULL AND A.IsUnwound = 0
                             THEN A.AF ELSE 0 END) AS decimal(18,2))                                    AS PaidOutAF
        FROM   Fact.AutoApproval A
        WHERE  A.DecisionOn > @From AND A.DecisionOn <= @ReportDate
          AND  (@DivisionId IS NULL OR A.DivisionId = @DivisionId)
        GROUP BY A.DivisionId
        ORDER BY A.DivisionId;
        RETURN;
    END

    IF @Part = 'Outcome'
    BEGIN
        -- Eligible but not auto approved: what a person decided instead (the application's status)
        SELECT ISNULL(SUM(CASE WHEN A.Status = 'Accepted' THEN 1 ELSE 0 END), 0)                  AS ApprovedByHand,
               ISNULL(SUM(CASE WHEN A.Status = 'Declined' THEN 1 ELSE 0 END), 0)                  AS DeclinedByHand,
               ISNULL(SUM(CASE WHEN A.Status NOT IN ('Accepted', 'Declined') THEN 1 ELSE 0 END), 0) AS Other
        FROM   Fact.AutoApproval A
        WHERE  A.DecisionOn > @From AND A.DecisionOn <= @ReportDate
          AND  (@DivisionId IS NULL OR A.DivisionId = @DivisionId)
          AND  A.IsEligible = 1 AND A.ApprovalRuleId IS NULL;
        RETURN;
    END

    IF @Part = 'ByRule'
    BEGIN
        -- Auto approved in the period, by the name of the rule that approved each deal
        SELECT ISNULL(R.RuleName, '(rule not found)')                    AS RuleName,
               CAST(CASE WHEN C.RuleName IS NULL THEN 0 ELSE 1 END AS bit) AS IsCurrent,
               COUNT(*)                                                  AS Deals,
               CAST(SUM(A.AF) AS decimal(18,2))                          AS AF
        FROM   Fact.AutoApproval A
               LEFT JOIN Dim.AutoApprovalRule R ON R.RuleId = A.ApprovalRuleId
               LEFT JOIN #Current C             ON C.RuleName = R.RuleName
        WHERE  A.DecisionOn > @From AND A.DecisionOn <= @ReportDate
          AND  (@DivisionId IS NULL OR A.DivisionId = @DivisionId)
          AND  A.ApprovalRuleId IS NOT NULL
        GROUP BY R.RuleName, C.RuleName
        ORDER BY COUNT(*) DESC;
        RETURN;
    END

    IF @Part = 'Performance'
    BEGIN
        -- Every deal paid out (not unwound) since auto approval began, counted ONCE at its worst: bad debt, else
        -- terminated, else serious arrears (CMS's own 60+ day flag). Auto approved: by rule name. Control: every other
        -- deal decided in the same time (not auto approved). The period filter does not apply.
        DECLARE @Began date = (SELECT MIN(DecisionOn) FROM Fact.AutoApproval WHERE ApprovalRuleId IS NOT NULL);

        SELECT X.Kind, X.RuleName, X.IsCurrent,
               COUNT(*)                                                                                       AS PaidOut,
               SUM(CASE WHEN A.IsBadDebt = 0 AND A.IsTerminated = 0 AND A.HasSeriousArrears = 1 THEN 1 ELSE 0 END) AS SeriousArrears,
               SUM(CASE WHEN A.IsBadDebt = 0 AND A.IsTerminated = 1 THEN 1 ELSE 0 END)                       AS Terminated,
               SUM(CASE WHEN A.IsBadDebt = 1 THEN 1 ELSE 0 END)                                              AS BadDebt,
               CAST(SUM(A.AmountWrittenOff) AS decimal(18,2))                                                AS WrittenOff
        FROM   Fact.AutoApproval A
               LEFT JOIN Dim.AutoApprovalRule R ON R.RuleId = A.ApprovalRuleId
               LEFT JOIN #Current C             ON C.RuleName = R.RuleName
               CROSS APPLY (SELECT CASE WHEN A.ApprovalRuleId IS NULL THEN 'Control' ELSE 'Rule' END AS Kind,
                                   CASE WHEN A.ApprovalRuleId IS NULL THEN NULL
                                        ELSE ISNULL(R.RuleName, '(rule not found)') END AS RuleName,
                                   CAST(CASE WHEN A.ApprovalRuleId IS NULL OR C.RuleName IS NOT NULL THEN 1 ELSE 0 END AS bit) AS IsCurrent) X
        WHERE  A.dtPayReceived IS NOT NULL AND A.IsUnwound = 0
          AND  A.DecisionOn >= @Began
          AND  (@DivisionId IS NULL OR A.DivisionId = @DivisionId)
        GROUP BY X.Kind, X.RuleName, X.IsCurrent
        ORDER BY X.Kind DESC, COUNT(*) DESC;
        RETURN;
    END

    IF @Part IN ('NotEligibleReasons', 'EligibleReasons')
    BEGIN
        -- Why, from the decisions CMS records (since 16 Oct 2026; KpiExport.vAutoApprovalReason): not eligible = the
        -- checks failed on the closest rule; eligible but not auto approved = the searches. Applications in the period by
        -- CMS's own eligibility; each counted once per reason (it can have several). Applications = how many had a reason.
        DECLARE @Eligible bit = CASE WHEN @Part = 'EligibleReasons' THEN 1 ELSE 0 END;
        SELECT R.Reason, MIN(R.SortOrder) AS SortOrder, COUNT(DISTINCT R.ApplicationId) AS Applications,
               (SELECT COUNT(DISTINCT R2.ApplicationId)
                FROM   Fact.AutoApprovalReason R2 INNER JOIN Fact.AutoApproval A2 ON A2.ApplicationId = R2.ApplicationId
                WHERE  A2.DecisionOn > @From AND A2.DecisionOn <= @ReportDate AND (@DivisionId IS NULL OR A2.DivisionId = @DivisionId)
                  AND  A2.IsEligible = @Eligible AND A2.ApprovalRuleId IS NULL AND R2.ComputedEligible = @Eligible) AS Total
        FROM   Fact.AutoApprovalReason R
               INNER JOIN Fact.AutoApproval A ON A.ApplicationId = R.ApplicationId
        WHERE  A.DecisionOn > @From AND A.DecisionOn <= @ReportDate
          AND  (@DivisionId IS NULL OR A.DivisionId = @DivisionId)
          AND  A.IsEligible = @Eligible AND A.ApprovalRuleId IS NULL AND R.ComputedEligible = @Eligible
        GROUP BY R.Reason
        ORDER BY COUNT(DISTINCT R.ApplicationId) DESC, MIN(R.SortOrder);
        RETURN;
    END

    IF @Part = 'Rules'
    BEGIN
        -- every version of every rule, for each rule's history (the info button)
        SELECT R.RuleName, R.RuleId, R.RuleSetId, R.RuleSetStatus, R.RuleSetCreatedOn, R.RuleSetActivatedOn,
               R.CreditLimitTo, R.FacilityLimitTo, R.AmountTo, R.TermTo
        FROM   Dim.AutoApprovalRule R
        ORDER BY R.RuleName, R.RuleSetId;
        RETURN;
    END
END
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spAutoApproval
(
    @Part       varchar(12),        -- Funnel | Outcome | ByRule | Performance | Rules
    @DivisionId int = NULL,         -- NULL = every division
    @Months     int = 3             -- the period, to the report date (not Performance or Rules: since auto approval began)
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ReportDate date = (SELECT ReportDate FROM Ref.Setting WHERE SettingId = 1);
    DECLARE @From date = DATEADD(MONTH, -@Months, @ReportDate);

    -- the rule names in today's active set: any other name is an earlier rule (CMS copies rules with no link back, so a
    -- rule's versions are grouped by name)
    SELECT DISTINCT RuleName INTO #Current FROM Dim.AutoApprovalRule WHERE RuleSetStatus = 1;

    IF @Part = 'Funnel'
    BEGIN
        -- Application to paid out: decided -> eligible -> auto approved -> paid out (auto approved, paid, not unwound)
        SELECT A.DivisionId, MAX(A.Division) AS Division,
               COUNT(*)                                                                                 AS Decided,
               SUM(CASE WHEN A.IsEligible = 1 THEN 1 ELSE 0 END)                                        AS Eligible,
               SUM(CASE WHEN A.ApprovalRuleId IS NOT NULL THEN 1 ELSE 0 END)                            AS AutoApproved,
               SUM(CASE WHEN A.ApprovalRuleId IS NOT NULL AND A.dtPayReceived IS NOT NULL AND A.IsUnwound = 0
                        THEN 1 ELSE 0 END)                                                              AS PaidOut,
               CAST(SUM(A.AF) AS decimal(18,2))                                                         AS DecidedAF,
               CAST(SUM(CASE WHEN A.IsEligible = 1 THEN A.AF ELSE 0 END) AS decimal(18,2))              AS EligibleAF,
               CAST(SUM(CASE WHEN A.ApprovalRuleId IS NOT NULL THEN A.AF ELSE 0 END) AS decimal(18,2))  AS AutoApprovedAF,
               CAST(SUM(CASE WHEN A.ApprovalRuleId IS NOT NULL AND A.dtPayReceived IS NOT NULL AND A.IsUnwound = 0
                             THEN A.AF ELSE 0 END) AS decimal(18,2))                                    AS PaidOutAF
        FROM   Fact.AutoApproval A
        WHERE  A.DecisionOn > @From AND A.DecisionOn <= @ReportDate
          AND  (@DivisionId IS NULL OR A.DivisionId = @DivisionId)
        GROUP BY A.DivisionId
        ORDER BY A.DivisionId;
        RETURN;
    END

    IF @Part = 'Outcome'
    BEGIN
        -- Eligible but not auto approved: what a person decided instead (the application's status)
        SELECT ISNULL(SUM(CASE WHEN A.Status = 'Accepted' THEN 1 ELSE 0 END), 0)                  AS ApprovedByHand,
               ISNULL(SUM(CASE WHEN A.Status = 'Declined' THEN 1 ELSE 0 END), 0)                  AS DeclinedByHand,
               ISNULL(SUM(CASE WHEN A.Status NOT IN ('Accepted', 'Declined') THEN 1 ELSE 0 END), 0) AS Other
        FROM   Fact.AutoApproval A
        WHERE  A.DecisionOn > @From AND A.DecisionOn <= @ReportDate
          AND  (@DivisionId IS NULL OR A.DivisionId = @DivisionId)
          AND  A.IsEligible = 1 AND A.ApprovalRuleId IS NULL;
        RETURN;
    END

    IF @Part = 'ByRule'
    BEGIN
        -- Auto approved in the period, by the name of the rule that approved each deal
        SELECT ISNULL(R.RuleName, '(rule not found)')                    AS RuleName,
               CAST(CASE WHEN C.RuleName IS NULL THEN 0 ELSE 1 END AS bit) AS IsCurrent,
               COUNT(*)                                                  AS Deals,
               CAST(SUM(A.AF) AS decimal(18,2))                          AS AF
        FROM   Fact.AutoApproval A
               LEFT JOIN Dim.AutoApprovalRule R ON R.RuleId = A.ApprovalRuleId
               LEFT JOIN #Current C             ON C.RuleName = R.RuleName
        WHERE  A.DecisionOn > @From AND A.DecisionOn <= @ReportDate
          AND  (@DivisionId IS NULL OR A.DivisionId = @DivisionId)
          AND  A.ApprovalRuleId IS NOT NULL
        GROUP BY R.RuleName, C.RuleName
        ORDER BY COUNT(*) DESC;
        RETURN;
    END

    IF @Part = 'Performance'
    BEGIN
        -- Every deal paid out (not unwound) since auto approval began, counted ONCE at its worst: bad debt, else
        -- terminated, else serious arrears (CMS's own 60+ day flag). Auto approved: by rule name. Control: every other
        -- deal decided in the same time (not auto approved). The period filter does not apply.
        DECLARE @Began date = (SELECT MIN(DecisionOn) FROM Fact.AutoApproval WHERE ApprovalRuleId IS NOT NULL);

        SELECT X.Kind, X.RuleName, X.IsCurrent,
               COUNT(*)                                                                                       AS PaidOut,
               SUM(CASE WHEN A.IsBadDebt = 0 AND A.IsTerminated = 0 AND A.HasSeriousArrears = 1 THEN 1 ELSE 0 END) AS SeriousArrears,
               SUM(CASE WHEN A.IsBadDebt = 0 AND A.IsTerminated = 1 THEN 1 ELSE 0 END)                       AS Terminated,
               SUM(CASE WHEN A.IsBadDebt = 1 THEN 1 ELSE 0 END)                                              AS BadDebt,
               CAST(SUM(A.AmountWrittenOff) AS decimal(18,2))                                                AS WrittenOff
        FROM   Fact.AutoApproval A
               LEFT JOIN Dim.AutoApprovalRule R ON R.RuleId = A.ApprovalRuleId
               LEFT JOIN #Current C             ON C.RuleName = R.RuleName
               CROSS APPLY (SELECT CASE WHEN A.ApprovalRuleId IS NULL THEN 'Control' ELSE 'Rule' END AS Kind,
                                   CASE WHEN A.ApprovalRuleId IS NULL THEN NULL
                                        ELSE ISNULL(R.RuleName, '(rule not found)') END AS RuleName,
                                   CAST(CASE WHEN A.ApprovalRuleId IS NULL OR C.RuleName IS NOT NULL THEN 1 ELSE 0 END AS bit) AS IsCurrent) X
        WHERE  A.dtPayReceived IS NOT NULL AND A.IsUnwound = 0
          AND  A.DecisionOn >= @Began
          AND  (@DivisionId IS NULL OR A.DivisionId = @DivisionId)
        GROUP BY X.Kind, X.RuleName, X.IsCurrent
        ORDER BY X.Kind DESC, COUNT(*) DESC;
        RETURN;
    END

    IF @Part = 'Rules'
    BEGIN
        -- every version of every rule, for each rule's history (the info button)
        SELECT R.RuleName, R.RuleId, R.RuleSetId, R.RuleSetStatus, R.RuleSetCreatedOn, R.RuleSetActivatedOn,
               R.CreditLimitTo, R.FacilityLimitTo, R.AmountTo, R.TermTo
        FROM   Dim.AutoApprovalRule R
        ORDER BY R.RuleName, R.RuleSetId;
        RETURN;
    END
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

        DELETE Dim.AutoApprovalRule;
        INSERT Dim.AutoApprovalRule (RuleId, RuleSetId, RuleSetStatus, RuleSetCreatedOn, RuleSetActivatedOn, RuleName, CreditLimitTo, FacilityLimitTo, AmountTo, TermTo)
        SELECT RuleId, RuleSetId, RuleSetStatus, RuleSetCreatedOn, RuleSetActivatedOn, LEFT(RuleName, 200), CreditLimitTo, FacilityLimitTo, AmountTo, TermTo FROM Stage.AutoApprovalRule;
        DELETE Fact.AutoApproval;
        INSERT Fact.AutoApproval (ApplicationId, LeadId, DivisionId, Division, DecisionOn, IsEligible, ApprovalRuleId, ApprovalRuleSetId, Status, AF, dtPayReceived, IsUnwound, HasSeriousArrears, IsTerminated, IsBadDebt, AmountWrittenOff)
        SELECT ApplicationId, LeadId, DivisionId, Division, DecisionOn, ISNULL(IsEligible, 0), ApprovalRuleId, ApprovalRuleSetId, Status, ISNULL(AF, 0), dtPayReceived, ISNULL(IsUnwound, 0),
               ISNULL(HasSeriousArrears, 0), ISNULL(IsTerminated, 0), ISNULL(IsBadDebt, 0), ISNULL(AmountWrittenOff, 0) FROM Stage.AutoApproval;
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
            migrationBuilder.Sql("DROP TABLE IF EXISTS Fact.AutoApprovalReason; DROP TABLE IF EXISTS Stage.AutoApprovalReason;");
        }
    }
}
