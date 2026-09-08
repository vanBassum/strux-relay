using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace StruxRelay.Data;

/// <summary>
/// Which database, and getting it ready to use.
///
/// The provider is configuration rather than a compile-time choice, because the
/// model is provider-neutral and a deployment that already runs PostgreSQL beside
/// Traefik has no reason to keep a second kind of database for three tables.
/// Adding one is one arm of the switch below plus its own migrations folder —
/// migrations are provider-specific, since SQLite and Npgsql emit different DDL.
///
/// Note what a second provider does NOT buy: more than one relay instance. The
/// live device sockets are held in memory by the process that accepted them, so
/// two instances behind a load balancer cannot reach each other's devices. The
/// database was never the thing pinning this to a single process.
/// </summary>
internal static class RelayDatabase
{
    public const string DefaultConnectionString = "Data Source=relay.sqlite";

    public static void Configure(DbContextOptionsBuilder options, IConfiguration configuration)
    {
        var provider = configuration["Relay:Database:Provider"] ?? "sqlite";
        var connectionString =
            configuration["Relay:Database:ConnectionString"] ?? DefaultConnectionString;

        switch (provider.ToLowerInvariant())
        {
            case "sqlite":
                EnsureSqliteDirectory(connectionString);
                options.UseSqlite(connectionString);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown database provider '{provider}'. Supported: sqlite.");
        }
    }

    /// <summary>
    /// SQLite creates its file but not the directory holding it, and in a container
    /// the data source is on a mounted volume whose path may not exist yet. Without
    /// this the failure is "unable to open database file", which says nothing about
    /// the actual cause.
    /// </summary>
    private static void EnsureSqliteDirectory(string connectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrEmpty(dataSource) || dataSource == ":memory:")
            return;

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// Applies migrations at startup. Unlike a cache, an approval cannot be
    /// re-derived — throwing the file away would mean re-pairing every device — so
    /// a schema change is a migration and not a fresh file.
    /// </summary>
    public static async Task MigrateAsync(IServiceProvider services, ILogger logger)
    {
        var contexts = services.GetRequiredService<IDbContextFactory<RelayDbContext>>();
        await using var database = await contexts.CreateDbContextAsync();

        await database.Database.MigrateAsync();

        // WAL, so a reader never blocks the connect path: the device pipe writes a
        // last-seen on every connect while a dashboard is reading the same tables.
        // A PRAGMA is provider-specific by nature, so it is asked for by provider
        // rather than through the model — and it is a property of the file, so
        // setting it once here is enough.
        if (database.Database.IsSqlite())
        {
            await database.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL");
            await database.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL");
        }

        var approved = await database.Approved.CountAsync();
        logger.LogInformation(
            "pairing store ready ({Approved} approved, {Pending} waiting)",
            approved, await database.Pending.CountAsync());
    }
}
