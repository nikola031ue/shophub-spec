using System.Globalization;
using System.Reflection;
using Npgsql;

namespace ShopHub.Api.Storage.Postgres;

/// <summary>
/// Applies the embedded <c>Scripts/NNN_*.sql</c> in order, each once, under an advisory lock so
/// several replicas can start at once (ported from shop's <c>PostgresShopStore.InitializeAsync</c>).
/// </summary>
public static class SchemaMigrator
{
    /// <summary>Advisory lock key for schema set-up ("SHUB" + 1).</summary>
    private const long InitLockKey = 0x5348_5542_0001;

    public static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await Pg.ExecuteAsync(conn, "SELECT pg_advisory_lock(@key)", ct, ("key", InitLockKey));
        try
        {
            await Pg.ExecuteAsync(conn, """
                CREATE TABLE IF NOT EXISTS schema_version (
                    version    integer     PRIMARY KEY,
                    applied_at timestamptz NOT NULL DEFAULT now())
                """, ct);
            var applied = new HashSet<int>();
            await using (var cmd = new NpgsqlCommand("SELECT version FROM schema_version", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    applied.Add(reader.GetInt32(0));
                }
            }

            foreach (var (version, sql) in Scripts().Where(s => !applied.Contains(s.Version)))
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                await Pg.ExecuteAsync(conn, sql, ct);
                await Pg.ExecuteAsync(conn, "INSERT INTO schema_version (version) VALUES (@v)", ct, ("v", version));
                await tx.CommitAsync(ct);
            }
        }
        finally
        {
            await Pg.ExecuteAsync(conn, "SELECT pg_advisory_unlock(@key)", CancellationToken.None, ("key", InitLockKey));
        }
    }

    /// <summary>Embedded <c>Scripts/NNN_*.sql</c>, ordered by number.</summary>
    internal static IEnumerable<(int Version, string Sql)> Scripts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("pg/", StringComparison.Ordinal))
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                var version = int.Parse(n.AsSpan(3, 3), CultureInfo.InvariantCulture);
                return (version, reader.ReadToEnd());
            })
            .OrderBy(s => s.version);
    }
}
