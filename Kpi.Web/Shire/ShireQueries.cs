namespace Kpi.Web.Shire;

/// <summary>
/// EVERY Salesforce query the Shire copy runs, in one place for review (3 Nov 2026, Steve). SOQL, read-only, as the
/// pf.integration user in Shire's Salesforce (External Client App "PF Integration": Read / View All on Opportunity,
/// Account, Contact and Opportunity Shareholder - nothing can be written). The SQL these rows feed is
/// Kpi.Data/Shire/001_ShireSalesforce.sql and 002_ShireBusinessAnalysis.sql.
/// </summary>
public static class ShireQueries
{
    /// <summary>
    /// The copy's cheap check (one call): how many opportunities there are and Salesforce's newest change to any of
    /// them. When neither has moved since the last copy there is nothing new - an edit (a deal won, re-opened or lost)
    /// moves the change time, a deletion the count.
    /// </summary>
    public const string Changes = "SELECT COUNT(Id) n, MAX(SystemModstamp) changed FROM Opportunity";

    /// <summary>
    /// The copy: ONLY what the Shire reports use (Steve) - the won opportunities' Id, Close Date, Amount, Income,
    /// owner, and the funder's name (to flag Performance Finance); for Shire Business Analysis, the client company's
    /// Sector, the deal's Finance Product, Loan Purpose and Funder picklist (no names of anyone). "Won" = IsWon, i.e.
    /// stage Closed Won: the basis of Shire's own reports, so the figures match theirs. About 5,500 rows (3 Nov 2026),
    /// a few API calls.
    /// </summary>
    public const string WonSales = "SELECT Id, CloseDate, Amount, Income__c, OwnerId, Owner.Name, Funder__r.Name, "
                                 + "Account.Sector__c, Finance_Product__c, Loan_Purpose__c, Funder1__c FROM Opportunity WHERE IsWon = true";

    /// <summary>The Settings card's connection test: the won deals the integration user can see.</summary>
    public const string WonCount = "SELECT COUNT() FROM Opportunity WHERE IsWon = true";

    /// <summary>The Funder (an Account) that marks a deal as Proposed to PF: Shire keeps one opportunity per funder.</summary>
    public const string PerformanceFinance = "Performance Finance";
}
