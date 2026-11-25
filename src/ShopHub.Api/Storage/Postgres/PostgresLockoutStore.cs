using Npgsql;
using ShopHub.Api.Auth;

namespace ShopHub.Api.Storage.Postgres;

/// <summary>
/// Lockout state in <c>signin_lockouts</c>, ported from shop 0.2.0's PostgreSQL store: one row per
/// (account, address) and one per (account, '*') for the account-wide ceiling.
/// </summary>
public sealed class PostgresLockoutStore(NpgsqlDataSource dataSource) : ILockoutStore
{
    private const string Columns = "account, address, failed_count, failed_window_start, locked_until";

    public async Task<LockoutState> GetAsync(string account, string address, CancellationToken ct)
    {
        LockoutKeys.Validate(account, address);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await ReadAsync(conn, account, address, forUpdate: false, ct);
    }

    public async Task<LockoutState> RecordFailureAsync(
        LockoutRules rules, string account, string address, DateTimeOffset now, CancellationToken ct)
    {
        LockoutKeys.Validate(account, address);
        now = Pg.Truncate(now);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Both rows exist before they are locked; '*' first, in the same order as the FOR UPDATE
        // below (C collation), so concurrent failures never deadlock.
        await Pg.ExecuteAsync(conn, """
            INSERT INTO signin_lockouts (account, address) VALUES (@account, '*'), (@account, @address)
            ON CONFLICT (account, address) DO NOTHING
            """, ct, ("account", account), ("address", address));
        var current = await ReadAsync(conn, account, address, forUpdate: true, ct);
        var next = current.ApplyFailure(rules, now);
        await WriteAsync(conn, account, address, next.Scope, ct);
        await WriteAsync(conn, account, LockoutKeys.AnyAddress, next.Account, ct);

        // Rows whose window and lock are both over no longer matter.
        await Pg.ExecuteAsync(conn, """
            DELETE FROM signin_lockouts
            WHERE coalesce(locked_until, '-infinity') <= @now
              AND coalesce(failed_window_start, '-infinity') <= @stale
              AND account <> @account
            """, ct, ("now", Pg.Time(now)), ("stale", Pg.Time(now - rules.Retention)), ("account", account));
        await tx.CommitAsync(ct);
        return next;
    }

    public async Task ResetAsync(string account, string address, CancellationToken ct)
    {
        LockoutKeys.Validate(account, address);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await Pg.ExecuteAsync(conn, "DELETE FROM signin_lockouts WHERE account = @account AND address = @address", ct,
            ("account", account), ("address", address));
    }

    private static async Task<LockoutState> ReadAsync(
        NpgsqlConnection conn, string account, string address, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Pg.Command(conn, $"""
            SELECT {Columns} FROM signin_lockouts
            WHERE account = @account AND address IN (@address, '*')
            ORDER BY address COLLATE "C"
            """ + (forUpdate ? " FOR UPDATE" : ""), ("account", account), ("address", address));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var state = LockoutState.Empty;
        while (await reader.ReadAsync(ct))
        {
            var scope = new LockoutScope(reader.GetInt32(2), Pg.NullableTime(reader, 3), Pg.NullableTime(reader, 4));
            state = reader.GetString(1) == LockoutKeys.AnyAddress
                ? state with { Account = scope }
                : state with { Scope = scope };
        }

        return state;
    }

    private static Task<int> WriteAsync(
        NpgsqlConnection conn, string account, string address, LockoutScope scope, CancellationToken ct) =>
        Pg.ExecuteAsync(conn, """
            UPDATE signin_lockouts SET failed_count = @count, failed_window_start = @start, locked_until = @locked
            WHERE account = @account AND address = @address
            """, ct, ("count", scope.FailedCount), ("start", Pg.Time(scope.FailedWindowStart)),
            ("locked", Pg.Time(scope.LockedUntil)), ("account", account), ("address", address));
}
