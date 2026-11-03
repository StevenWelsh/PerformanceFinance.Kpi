using System.Text.RegularExpressions;

namespace Kpi.Data.Shire;

/// <summary>
/// The Shire Salesforce SQL (3 Nov 2026, Steve: "keep queries in an obvious, separate area so we can review"): real .sql
/// files in Kpi.Data/Shire, embedded in this assembly, which the Shire migrations run batch by batch. A file is numbered
/// with its migration and is never edited once applied: a change is a new file and a new migration.
/// </summary>
public static partial class ShireScripts
{
    /// <summary>The file's batches (split on the GO lines, as SSMS does), for migrationBuilder.Sql one at a time.</summary>
    public static IReadOnlyList<string> Batches(string fileName)
    {
        var name = typeof(ShireScripts).Assembly.GetManifestResourceNames()
                       .SingleOrDefault(n => n.EndsWith("." + fileName, StringComparison.Ordinal))
                   ?? throw new InvalidOperationException($"Shire script {fileName} is not embedded in Kpi.Data.");
        using var stream = typeof(ShireScripts).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return GoLine().Split(reader.ReadToEnd())
                       .Select(b => b.Trim())
                       .Where(b => b.Length > 0 && !b.Split('\n').All(l => l.TrimStart().StartsWith("--") || l.Trim().Length == 0))
                       .ToList();
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();
}
