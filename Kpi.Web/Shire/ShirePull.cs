using System.Data;
using System.Globalization;
using System.Text.Json;
using Kpi.Web.Integrations;
using Microsoft.Data.SqlClient;

namespace Kpi.Web.Shire;

/// <summary>
/// The Shire copy (3 Nov 2026, Steve): Shire's Salesforce -> schema Shire in CMS_KPI, as <see cref="Data.KpiPull"/> brings
/// CMS's figures - the task "Copy Shire from Salesforce" on Settings > Scheduled tasks (Scheduling/Jobs.cs), every 15
/// minutes unless changed there. ONE cheap call asks Salesforce how many opportunities there are and when any last changed
/// (<see cref="ShireQueries.Changes"/>); only when that has moved since the last copy does it copy the won deals
/// (<see cref="ShireQueries.WonSales"/>, ~5,500 rows) in full - into Shire.StageSale, then Shire.spPublish swaps them into
/// Shire.Sale in one transaction. In full rather than only the changes: a deal deleted, re-opened or lost drops out without
/// any special handling, and it costs a few API calls. Run now always copies. Off while Salesforce (Shire) isn't connected in
/// Settings > Integrations, or is switched off. Writes through the site's usual CmsKpi connection (its identity is in kpi_push).
/// </summary>
public sealed class ShirePull(IConfiguration config, ShireSalesforce salesforce, IntegrationStore store,
                              ShireFiguresSignal figures, ILogger<ShirePull> log)
{
    // what Salesforce said at the last publish: nothing new while it says the same (memory: a restart copies once)
    private (int Count, DateTime? Changed)? _published;

    /// <summary>One run: what happened, in a line for the task's History. Throws when the copy failed.</summary>
    public async Task<string> RunAsync(bool force, CancellationToken ct)
    {
        var kpi = config.GetConnectionString("CmsKpi")!;   // Program.cs refuses to start without it
        var startedUtc = DateTime.UtcNow;
        DateTime? sourceChanged = null;
        try
        {
            var connection = await salesforce.CopyConnectionAsync();
            if (connection is null) return "Off: Salesforce (Shire) isn't connected, or copying is switched off (Settings > Integrations).";
            var (settings, secrets) = connection.Value;

            var token = await salesforce.SignInAsync(settings.Domain, secrets, ct);
            var (check, _) = await salesforce.QueryAsync(token, ShireQueries.Changes, ct: ct);
            var seen = (Count: check[0].GetProperty("n").GetInt32(), Changed: ReadTime(check[0], "changed"));
            sourceChanged = seen.Changed;
            if (!force && _published is { } last && last.Count == seen.Count && last.Changed == seen.Changed)
            {
                await ExecAsync(kpi, "Shire.spChecked", null, ct);
                return $"Nothing changed in Salesforce since {Data.UkTime.ShowShort(seen.Changed)}.";
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (records, _) = await salesforce.QueryAsync(token, ShireQueries.WonSales, ct: ct);
            var table = ToTable(records);
            await PublishAsync(kpi, table, startedUtc, seen.Changed, ct);
            _published = seen;

            await store.RecordCallAsync(ShireSalesforce.Provider, null);   // the card's "Last worked"
            figures.Raise();   // a new generation: the Shire reports' cached figures are obsolete, and open ones reload now
            log.LogInformation("Shire copy published {Rows:N0} won deals in {Seconds:0.0} s", table.Rows.Count, clock.Elapsed.TotalSeconds);
            return $"{table.Rows.Count:N0} won deals copied in {clock.Elapsed.TotalSeconds:0.0} s.";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Shire.spPublish logs its own failure; anything before it (Salesforce, the bulk copy) is logged here
            if (ex is not ShirePublishFailedException)
                try { await RecordFailureAsync(kpi, startedUtc, ex.Message, sourceChanged, ct); } catch { /* CMS_KPI unreachable too */ }
            try { await store.RecordCallAsync(ShireSalesforce.Provider, ex.Message); } catch { /* the card's "Last problem" */ }
            throw;
        }
    }

    // The won deals as Shire.StageSale's rows. Proposed to PF = the funder is Performance Finance (ShireQueries).
    // Sector, Finance Product, Loan Purpose and the Funder picklist feed Shire Business Analysis (002_ShireBusinessAnalysis.sql).
    private static DataTable ToTable(List<JsonElement> records)
    {
        var t = new DataTable();
        t.Columns.Add("OpportunityId", typeof(string));
        t.Columns.Add("CloseDate", typeof(DateTime));
        t.Columns.Add("Amount", typeof(decimal));
        t.Columns.Add("Income", typeof(decimal));
        t.Columns.Add("OwnerId", typeof(string));
        t.Columns.Add("Salesperson", typeof(string));
        t.Columns.Add("ProposedToPF", typeof(bool));
        t.Columns.Add("Sector", typeof(string));
        t.Columns.Add("FinanceProduct", typeof(string));
        t.Columns.Add("LoanPurpose", typeof(string));
        t.Columns.Add("Lender", typeof(string));
        foreach (var r in records)
        {
            var funder = Related(r, "Funder__r", "Name");
            var owner = Related(r, "Owner", "Name");
            t.Rows.Add(
                Text(r, "Id"),
                Text(r, "CloseDate") is { } close ? DateTime.ParseExact(close, "yyyy-MM-dd", CultureInfo.InvariantCulture) : DBNull.Value,
                Money(r, "Amount"),
                Money(r, "Income__c"),
                Text(r, "OwnerId"),
                owner ?? "",
                string.Equals(funder, ShireQueries.PerformanceFinance, StringComparison.OrdinalIgnoreCase),
                (object?)Related(r, "Account", "Sector__c") ?? DBNull.Value,
                (object?)Text(r, "Finance_Product__c") ?? DBNull.Value,
                (object?)Text(r, "Loan_Purpose__c") ?? DBNull.Value,
                (object?)Text(r, "Funder1__c") ?? DBNull.Value);
        }
        return t;
    }

    // a field of a related record (Owner.Name, Account.Sector__c): Salesforce sends the record as an object, or null
    private static string? Related(JsonElement e, string relation, string name) =>
        e.TryGetProperty(relation, out var v) && v.ValueKind == JsonValueKind.Object ? Text(v, name) : null;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static object Money(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Round(v.GetDecimal(), 2) : DBNull.Value;

    // Salesforce's datetimes: "2026-11-03T13:59:11.000+0000"
    private static DateTime? ReadTime(JsonElement e, string name) =>
        Text(e, name) is { } s && DateTimeOffset.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture,
                                                              DateTimeStyles.None, out var d)
            ? d.UtcDateTime
            : null;

    // Stage, then publish, on ONE connection outside the pool holding an app lock, so two copies (a deployment slot swap
    // runs two sites for a moment) never mix their rows; closing the connection releases the lock.
    private static async Task PublishAsync(string kpiConnection, DataTable rows, DateTime startedUtc, DateTime? sourceChanged,
                                           CancellationToken ct)
    {
        await using var kpi = new SqlConnection(new SqlConnectionStringBuilder(kpiConnection) { Pooling = false }.ConnectionString);
        await kpi.OpenAsync(ct);

        await using (var lockCmd = new SqlCommand("sp_getapplock", kpi) { CommandType = CommandType.StoredProcedure })
        {
            lockCmd.Parameters.AddWithValue("@Resource", "ShireCopy");
            lockCmd.Parameters.AddWithValue("@LockMode", "Exclusive");
            lockCmd.Parameters.AddWithValue("@LockOwner", "Session");
            lockCmd.Parameters.AddWithValue("@LockTimeout", 0);
            var result = lockCmd.Parameters.Add("@Result", SqlDbType.Int);
            result.Direction = ParameterDirection.ReturnValue;
            await lockCmd.ExecuteNonQueryAsync(ct);
            if ((int)result.Value < 0) throw new InvalidOperationException("Another Shire copy is running (the lock is held): this one didn't start.");
        }

        await using (var clear = new SqlCommand("DELETE Shire.StageSale", kpi) { CommandTimeout = 120 })
            await clear.ExecuteNonQueryAsync(ct);

        using (var bulk = new SqlBulkCopy(kpi) { DestinationTableName = "Shire.StageSale", BulkCopyTimeout = 300 })
        {
            foreach (DataColumn c in rows.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
            await bulk.WriteToServerAsync(rows, ct);
        }

        try
        {
            await using var publish = new SqlCommand("Shire.spPublish", kpi) { CommandType = CommandType.StoredProcedure, CommandTimeout = 300 };
            publish.Parameters.AddWithValue("@StartedUtc", startedUtc);
            publish.Parameters.AddWithValue("@SourceChangedUtc", (object?)sourceChanged ?? DBNull.Value);
            await publish.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) { throw new ShirePublishFailedException(ex.Message, ex); }
    }

    private static async Task RecordFailureAsync(string kpiConnection, DateTime startedUtc, string error, DateTime? sourceChanged,
                                                 CancellationToken ct) =>
        await ExecAsync(kpiConnection, "Shire.spRecordFailure", new Dictionary<string, object?>
        {
            ["@StartedUtc"] = startedUtc,
            ["@Error"] = error.Length > 2000 ? error[..2000] : error,
            ["@SourceChangedUtc"] = sourceChanged
        }, ct);

    private static async Task ExecAsync(string kpiConnection, string procedure, Dictionary<string, object?>? args, CancellationToken ct)
    {
        await using var kpi = new SqlConnection(kpiConnection);
        await kpi.OpenAsync(ct);
        await using var cmd = new SqlCommand(procedure, kpi) { CommandType = CommandType.StoredProcedure, CommandTimeout = 60 };
        foreach (var (name, value) in args ?? new()) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Shire.spPublish refused or failed - it has already logged the run itself.</summary>
public sealed class ShirePublishFailedException(string message, Exception inner) : Exception(message, inner);
