using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kpi.Data.Migrations
{
    /// <summary>
    /// Auto Approval's reasons (26 Nov 2026, Steve): each application counted once, under the first check it failed in the
    /// order CMS runs them - so a reason panel's figures add up to its applications. Rpt.spAutoApproval's two reason lists.
    /// </summary>
    public partial class AutoApprovalFirstReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
CREATE OR ALTER PROCEDURE Rpt.spAutoApproval
(
    @Part       varchar(20),        -- Funnel | Outcome | NotEligibleReasons | EligibleReasons | ByRule | Performance | Rules
    @DivisionId int = NULL,         -- NULL = every division
    @Period     varchar(5) = '3'    -- this | last | 3 | 6 | 12 (not Performance or Rules: since auto approval began)
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ReportDate date = (SELECT ReportDate FROM Ref.Setting WHERE SettingId = 1);
    -- this = this month to date; last = last calendar month; 3 / 6 / 12 = that many months to the report date (Steve, 26 Nov 2026).
    -- Decided AFTER @From and on or before @To.
    DECLARE @From date, @To date = @ReportDate;
    IF @Period = 'this'      SET @From = EOMONTH(@ReportDate, -1);
    ELSE IF @Period = 'last' SELECT @From = EOMONTH(@ReportDate, -2), @To = EOMONTH(@ReportDate, -1);
    ELSE                     SET @From = DATEADD(MONTH, -CAST(@Period AS int), @ReportDate);

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
        WHERE  A.DecisionOn > @From AND A.DecisionOn <= @To
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
        WHERE  A.DecisionOn > @From AND A.DecisionOn <= @To
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
        WHERE  A.DecisionOn > @From AND A.DecisionOn <= @To
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
        -- checks failed on the closest rule; eligible but not auto approved = the searches. Each application counted ONCE,
        -- under its FIRST reason - the first check that failed, in the order CMS runs them (SortOrder; Steve, 26 Nov 2026).
        DECLARE @Eligible bit = CASE WHEN @Part = 'EligibleReasons' THEN 1 ELSE 0 END;
        WITH F AS
        (
            SELECT R.ApplicationId, R.Reason, R.SortOrder,
                   ROW_NUMBER() OVER (PARTITION BY R.ApplicationId ORDER BY R.SortOrder, R.Reason) AS Rn
            FROM   Fact.AutoApprovalReason R
                   INNER JOIN Fact.AutoApproval A ON A.ApplicationId = R.ApplicationId
            WHERE  A.DecisionOn > @From AND A.DecisionOn <= @To
              AND  (@DivisionId IS NULL OR A.DivisionId = @DivisionId)
              AND  A.IsEligible = @Eligible AND A.ApprovalRuleId IS NULL AND R.ComputedEligible = @Eligible
        )
        SELECT F.Reason, MIN(F.SortOrder) AS SortOrder, COUNT(*) AS Applications,
               (SELECT COUNT(*) FROM F F2 WHERE F2.Rn = 1) AS Total
        FROM   F
        WHERE  F.Rn = 1
        GROUP BY F.Reason
        ORDER BY COUNT(*) DESC, MIN(F.SortOrder);
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
    @Part       varchar(20),        -- Funnel | Outcome | NotEligibleReasons | EligibleReasons | ByRule | Performance | Rules
    @DivisionId int = NULL,         -- NULL = every division
    @Period     varchar(5) = '3'    -- this | last | 3 | 6 | 12 (not Performance or Rules: since auto approval began)
)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ReportDate date = (SELECT ReportDate FROM Ref.Setting WHERE SettingId = 1);
    -- this = this month to date; last = last calendar month; 3 / 6 / 12 = that many months to the report date (Steve, 26 Nov 2026).
    -- Decided AFTER @From and on or before @To.
    DECLARE @From date, @To date = @ReportDate;
    IF @Period = 'this'      SET @From = EOMONTH(@ReportDate, -1);
    ELSE IF @Period = 'last' SELECT @From = EOMONTH(@ReportDate, -2), @To = EOMONTH(@ReportDate, -1);
    ELSE                     SET @From = DATEADD(MONTH, -CAST(@Period AS int), @ReportDate);

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
        WHERE  A.DecisionOn > @From AND A.DecisionOn <= @To
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
        WHERE  A.DecisionOn > @From AND A.DecisionOn <= @To
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
        WHERE  A.DecisionOn > @From AND A.DecisionOn <= @To
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
                WHERE  A2.DecisionOn > @From AND A2.DecisionOn <= @To AND (@DivisionId IS NULL OR A2.DivisionId = @DivisionId)
                  AND  A2.IsEligible = @Eligible AND A2.ApprovalRuleId IS NULL AND R2.ComputedEligible = @Eligible) AS Total
        FROM   Fact.AutoApprovalReason R
               INNER JOIN Fact.AutoApproval A ON A.ApplicationId = R.ApplicationId
        WHERE  A.DecisionOn > @From AND A.DecisionOn <= @To
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
    }
}
