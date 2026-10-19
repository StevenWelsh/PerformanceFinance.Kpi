using Kpi.Data.Auth;
using Kpi.Data.Cfg;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage;

namespace Kpi.Data;

/// <summary>
/// The KPI database, CMS_KPI. The report figures are read through the Rpt procedures, never through EF, so the
/// model maps only what the site itself owns: sign-in (ASP.NET Identity, as DavidsonHR, in the Auth schema), the
/// audit trail, the integrations and the published links. Everything else is created by the migrations as SQL.
/// </summary>
public sealed class KpiDbContext(DbContextOptions<KpiDbContext> options) : IdentityDbContext<KpiUser>(options)
{
    /// <summary>CMS_Live's collation: every join between copied text and seeded text depends on it.</summary>
    public const string Collation = "Latin1_General_CI_AS";

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<ReportView> ReportViews => Set<ReportView>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<PublishedLink> PublishedLinks => Set<PublishedLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseCollation(Collation);

        // Identity's tables, in Auth with plain names
        modelBuilder.Entity<KpiUser>(e =>
        {
            e.ToTable("Users", "Auth");
            e.Property(u => u.DisplayName).HasMaxLength(100);
            e.Property(u => u.InvitedByUserId).HasMaxLength(450);
            e.Property(u => u.InviteNonce).HasMaxLength(64).IsUnicode(false);
            e.Property(u => u.DisabledByUserId).HasMaxLength(450);
            e.Property(u => u.ReenrolNonce).HasMaxLength(64).IsUnicode(false);
            e.Property(u => u.SetupTokenHash).HasMaxLength(64).IsUnicode(false);
            e.Property(u => u.Reports).HasMaxLength(600).IsUnicode(false);
            e.Property(u => u.FavouriteReports).HasMaxLength(400).IsUnicode(false);
            e.Property(u => u.FavouritesMenuView).HasMaxLength(10).IsUnicode(false);
        });
        modelBuilder.Entity<IdentityRole>().ToTable("Roles", "Auth");
        modelBuilder.Entity<IdentityUserRole<string>>().ToTable("UserRoles", "Auth");
        modelBuilder.Entity<IdentityUserClaim<string>>().ToTable("UserClaims", "Auth");
        modelBuilder.Entity<IdentityUserLogin<string>>().ToTable("UserLogins", "Auth");
        modelBuilder.Entity<IdentityUserToken<string>>().ToTable("UserTokens", "Auth");
        modelBuilder.Entity<IdentityRoleClaim<string>>().ToTable("RoleClaims", "Auth");

        modelBuilder.Entity<ReportView>(e =>
        {
            e.ToTable("ReportView", "Auth");
            e.Property(v => v.OpenedUtc).HasColumnType("datetime2(0)");
            e.Property(v => v.Report).HasMaxLength(60).IsUnicode(false);
            e.Property(v => v.UserId).HasMaxLength(450);
            e.Property(v => v.UserName).HasMaxLength(256);
            e.Property(v => v.Source).HasMaxLength(20).IsUnicode(false);
            e.HasIndex(v => v.OpenedUtc);
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.ToTable("AuditEntry", "Auth");
            e.Property(a => a.OccurredUtc).HasColumnType("datetime2(0)");
            e.Property(a => a.Event).HasMaxLength(40).IsUnicode(false);
            e.Property(a => a.ActorUserId).HasMaxLength(450);
            e.Property(a => a.ActorName).HasMaxLength(200);
            e.Property(a => a.SubjectUserId).HasMaxLength(450);
            e.Property(a => a.SubjectName).HasMaxLength(200);
            e.Property(a => a.Detail).HasMaxLength(1000);
            e.Property(a => a.IpAddress).HasMaxLength(45).IsUnicode(false);
            e.HasIndex(a => a.OccurredUtc);
        });

        modelBuilder.Entity<Integration>(e =>
        {
            e.ToTable("Integration", "Cfg");
            e.HasKey(i => i.Provider);
            e.Property(i => i.Provider).HasMaxLength(30).IsUnicode(false);
            e.Property(i => i.Status).HasMaxLength(20).IsUnicode(false);
            e.Property(i => i.RedirectTo).HasMaxLength(256);
            e.Property(i => i.ConnectedByUserId).HasMaxLength(450);
            e.Property(i => i.LastError).HasMaxLength(1000);
            e.Property(i => i.UpdatedByUserId).HasMaxLength(450);
            e.Property(i => i.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<PublishedLink>(e =>
        {
            e.ToTable("PublishedLink", "Cfg");
            e.Property(l => l.Label).HasMaxLength(200);
            e.Property(l => l.ReportSlug).HasMaxLength(60).IsUnicode(false);
            e.Property(l => l.KeyHash).HasMaxLength(32).IsFixedLength();
            e.HasIndex(l => l.KeyHash).IsUnique();
            e.Property(l => l.KeyHint).HasMaxLength(8).IsUnicode(false);
            e.Property(l => l.CreatedByUserId).HasMaxLength(450);
            e.Property(l => l.RevokedByUserId).HasMaxLength(450);
            e.Property(l => l.LastUsedAddress).HasMaxLength(45).IsUnicode(false);
        });
    }

    // The audit trail is append only: refused here, and denied to the site's database role (the migration).
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAudit();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardAudit();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardAudit()
    {
        if (ChangeTracker.Entries<AuditEntry>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Audit entries are never changed or deleted.");
    }

    /// <summary>The options every host uses: the migrations history lives in Ref, beside the load log.</summary>
    public static DbContextOptions<KpiDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<KpiDbContext>().UseKpi(connectionString).Options;
}

public static class KpiDbContextOptions
{
    /// <summary>
    /// SQL Server with the migrations history in Ref - for the hosts that register the context themselves. The site
    /// passes <paramref name="retryDroppedConnections"/>: see <see cref="KpiRetryStrategy"/>.
    /// </summary>
    public static DbContextOptionsBuilder UseKpi(this DbContextOptionsBuilder builder, string connectionString,
        bool retryDroppedConnections = false) =>
        builder.UseSqlServer(connectionString, sql =>
        {
            sql.MigrationsHistoryTable("MigrationsHistory", "Ref").CommandTimeout(600);
            if (retryDroppedConnections) sql.ExecutionStrategy(d => new KpiRetryStrategy(d));
        });

    public static DbContextOptionsBuilder<KpiDbContext> UseKpi(this DbContextOptionsBuilder<KpiDbContext> builder, string connectionString)
    {
        ((DbContextOptionsBuilder)builder).UseKpi(connectionString);
        return builder;
    }
}

/// <summary>
/// AZURE SQL DROPS A CONNECTION WHOSE FIRST HANDSHAKE IS SLOW (Oct 2026). In App Service on Linux the site's first secure
/// connection after a start took 8-32 s (measured inside the container: TLS and the managed identity token, each slow
/// once and fast after), and Azure SQL's gateway resets a connection left waiting that long - "Connection reset by
/// peer" / "error: 35", the site's "Something went wrong" on sign-in. Not the firewall, the encryption mode or the
/// driver (all ruled out). The retry is fast: the slow part is done by then. EF's own transient list does not include a
/// reset socket, so this adds it to SQL Server's usual transient errors.
/// </summary>
public sealed class KpiRetryStrategy(ExecutionStrategyDependencies dependencies)
    : Microsoft.EntityFrameworkCore.SqlServerRetryingExecutionStrategy(dependencies, maxRetryCount: 3)
{
    protected override bool ShouldRetryOn(Exception exception) => base.ShouldRetryOn(exception) || IsDroppedConnection(exception);

    /// <summary>A connection the server cut off (a reset socket under the SqlException) - worth one more try.</summary>
    public static bool IsDroppedConnection(Exception exception) =>
        exception is Microsoft.Data.SqlClient.SqlException { InnerException: System.IO.IOException or System.Net.Sockets.SocketException };
}

/// <summary>
/// THE EF TOOLS' CONTEXT - Add-Migration, Update-Database, Remove-Migration in Visual Studio's Package Manager Console,
/// and their `dotnet ef` forms - as DavidsonHR: always the developer's OWN local CMS_KPI, and nothing else. Opening any
/// other database is refused, even one handed to the command (-Connection / --connection), so a stray Update-Database
/// can never reach Azure: that is migrated by Kpi.Tools migrate --remote, run deliberately.
/// The server can be changed for a developer without the default SQL Express instance: set KPI_DEV_SERVER (e.g.
/// "(localdb)\MSSQLLocalDB"). The DATABASE name is fixed, so the guard still holds.
/// </summary>
public sealed class KpiDbContextDesignTimeFactory : IDesignTimeDbContextFactory<KpiDbContext>
{
    public const string DevDatabase = "CMS_KPI";

    public KpiDbContext CreateDbContext(string[] args)
    {
        var server = Environment.GetEnvironmentVariable("KPI_DEV_SERVER") ?? @".\SQLEXPRESS";
        var options = new DbContextOptionsBuilder<KpiDbContext>()
            .UseKpi($"Server={server};Database={DevDatabase};Trusted_Connection=True;TrustServerCertificate=True")
            .AddInterceptors(new LocalOnly())
            .Options;
        return new KpiDbContext(options);
    }

    // Refuses to open anything but a local CMS_KPI - however the connection was given.
    private sealed class LocalOnly : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
    {
        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult ConnectionOpening(
            System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result)
        {
            Check(connection);
            return result;
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> ConnectionOpeningAsync(
            System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Check(connection);
            return ValueTask.FromResult(result);
        }

        private static void Check(System.Data.Common.DbConnection connection)
        {
            var server = connection.DataSource ?? "";
            var local = server.StartsWith('.') || server.StartsWith("(local", StringComparison.OrdinalIgnoreCase)
                     || server.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
                     || server.StartsWith(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
            if (!local || !string.Equals(connection.Database, DevDatabase, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"The EF tools only work on your local {DevDatabase} (this is '{server}' / '{connection.Database}'). " +
                    "Azure is migrated with: dotnet run --project Kpi.Tools -- migrate --remote");
        }
    }
}
