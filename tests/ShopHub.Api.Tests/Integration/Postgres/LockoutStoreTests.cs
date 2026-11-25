using Npgsql;
using ShopHub.Api.Auth;
using ShopHub.Api.Storage.Postgres;

namespace ShopHub.Api.Tests.Integration.Postgres;

/// <summary>
/// The lockout store on PostgreSQL, ported from shop 0.2.0's store contract and adapted to many
/// accounts (FR-003): per (account, address), plus a per-account ceiling across addresses.
/// Each test gets its own empty, migrated database.
/// </summary>
public sealed class LockoutStoreTests(PostgresContainer db) : IClassFixture<PostgresContainer>, IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, 123, TimeSpan.Zero);
    private const string X = "owner-x@example.com";
    private const string Y = "owner-y@example.com";
    private const string AddrA = "203.0.113.7";
    private const string AddrB = "198.51.100.9";

    private readonly List<NpgsqlDataSource> _sources = [];
    private string _connectionString = "";

    private PostgresLockoutStore Store { get; set; } = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await db.CreateDatabaseAsync("lock_" + Guid.NewGuid().ToString("N"));
        var source = NewSource();
        await SchemaMigrator.MigrateAsync(source, Ct);
        Store = new PostgresLockoutStore(source);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sources)
        {
            await s.DisposeAsync();
        }
    }

    /// <summary>Another data source on the same database, like another replica.</summary>
    private NpgsqlDataSource NewSource()
    {
        var source = NpgsqlDataSource.Create(_connectionString);
        _sources.Add(source);
        return source;
    }

    private static LockoutRules Rules(int perAddress, int perAccount = 1000) => new(
        new LockoutPolicy(perAddress, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15)),
        new LockoutPolicy(perAccount, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15)));

    [Fact]
    public async Task MigrationsAreIdempotentAndSafeFromThreeReplicas()
    {
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => SchemaMigrator.MigrateAsync(NewSource(), Ct)));

        await using var conn = await NewSource().OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM schema_version", conn);
        Assert.Equal((long)SchemaMigrator.Scripts().Count(), await cmd.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task LockoutStartsEmpty() =>
        Assert.Equal(LockoutState.Empty, await Store.GetAsync(X, AddrA, Ct));

    [Fact]
    public async Task CountsFailuresAndLocksOnTheFifth()
    {
        var rules = LockoutRules.Default;
        for (var i = 1; i <= 4; i++)
        {
            var state = await Store.RecordFailureAsync(rules, X, AddrA, T0.AddMinutes(i), Ct);
            Assert.Equal(new LockoutScope(i, T0.AddMinutes(1), null), state.Scope);
            Assert.Equal(new LockoutScope(i, T0.AddMinutes(1), null), state.Account);
        }

        var locked = await Store.RecordFailureAsync(rules, X, AddrA, T0.AddMinutes(5), Ct);

        Assert.Equal(new LockoutScope(0, null, T0.AddMinutes(20)), locked.Scope);
        Assert.Equal(5, locked.Account.FailedCount);
        Assert.Equal(locked, await new PostgresLockoutStore(NewSource()).GetAsync(X, AddrA, Ct));

        // Failures while locked change nothing, not even the account-wide count.
        Assert.Equal(locked, await Store.RecordFailureAsync(rules, X, AddrA, T0.AddMinutes(6), Ct));
    }

    [Fact]
    public async Task LockedAddressDoesNotLockTheSameAccountElsewhere()
    {
        for (var i = 0; i < 5; i++)
        {
            await Store.RecordFailureAsync(LockoutRules.Default, X, AddrA, T0, Ct);
        }

        Assert.True((await Store.GetAsync(X, AddrA, Ct)).IsLocked(T0, out _));
        Assert.False((await Store.GetAsync(X, AddrB, Ct)).IsLocked(T0, out _));
    }

    /// <summary>FR-003: 5 failures for account X from address A don't lock account Y from address A.</summary>
    [Fact]
    public async Task FailuresForOneAccountDoNotLockAnotherAccountFromTheSameAddress()
    {
        for (var i = 0; i < 5; i++)
        {
            await Store.RecordFailureAsync(LockoutRules.Default, X, AddrA, T0, Ct);
        }

        Assert.True((await Store.GetAsync(X, AddrA, Ct)).IsLocked(T0, out _));
        Assert.Equal(LockoutState.Empty, await Store.GetAsync(Y, AddrA, Ct));
    }

    /// <summary>The per-account ceiling: 50 failures for X across addresses lock X everywhere, not Y.</summary>
    [Fact]
    public async Task FiftyFailuresAcrossAddressesLockThatAccountOnly()
    {
        for (var i = 0; i < 50; i++)
        {
            await Store.RecordFailureAsync(LockoutRules.Default, X, $"192.0.2.{i / 2}", T0.AddSeconds(i), Ct);
        }

        var x = await Store.GetAsync(X, AddrB, Ct);
        Assert.Equal(LockoutScope.Empty, x.Scope);
        Assert.Equal(new LockoutScope(0, null, T0.AddSeconds(49).AddMinutes(15)), x.Account);
        Assert.True(x.IsLocked(T0.AddMinutes(10), out _));
        Assert.Equal(LockoutState.Empty, await Store.GetAsync(Y, AddrB, Ct));
    }

    /// <summary>
    /// One address alone can't trigger the account-wide lock: attempts that passed the lock check
    /// concurrently (60 at once from three replicas) count only until the pair locks.
    /// </summary>
    [Fact]
    public async Task OneAddressAloneCannotLockTheAccountEverywhere()
    {
        var replicas = Enumerable.Range(0, 3).Select(_ => new PostgresLockoutStore(NewSource())).ToList();

        await Task.WhenAll(Enumerable.Range(0, 60).Select(i =>
            Task.Run(() => replicas[i % 3].RecordFailureAsync(LockoutRules.Default, X, AddrA, T0, Ct), Ct)));

        var state = await Store.GetAsync(X, AddrA, Ct);
        Assert.True(state.Scope.IsLocked(T0, out _));
        Assert.Equal(new LockoutScope(5, T0, null), state.Account);
        Assert.False((await Store.GetAsync(X, AddrB, Ct)).IsLocked(T0, out _));
    }

    [Fact]
    public async Task FailuresDuringTheAccountLockCountNowhere()
    {
        var rules = Rules(perAddress: 5, perAccount: 2);
        await Store.RecordFailureAsync(rules, X, AddrA, T0, Ct);
        await Store.RecordFailureAsync(rules, X, AddrB, T0, Ct);

        var state = await Store.RecordFailureAsync(rules, X, AddrB, T0.AddMinutes(1), Ct);

        Assert.True(state.Account.IsLocked(T0.AddMinutes(1), out _));
        Assert.Equal(new LockoutScope(1, T0, null), state.Scope);
    }

    [Fact]
    public async Task WindowStartsAtTheFirstFailure()
    {
        await Store.RecordFailureAsync(LockoutRules.Default, X, AddrA, T0, Ct);
        await Store.RecordFailureAsync(LockoutRules.Default, X, AddrA, T0.AddMinutes(14), Ct);

        var state = await Store.RecordFailureAsync(LockoutRules.Default, X, AddrA, T0.AddMinutes(15), Ct);

        Assert.Equal(new LockoutScope(1, T0.AddMinutes(15), null), state.Scope);
        Assert.Equal(new LockoutScope(1, T0.AddMinutes(15), null), state.Account);
    }

    [Fact]
    public async Task ConcurrentFailuresAreAllCounted()
    {
        var rules = Rules(100);
        var replicas = Enumerable.Range(0, 3).Select(_ => new PostgresLockoutStore(NewSource())).ToList();

        await Task.WhenAll(Enumerable.Range(0, 30).Select(i =>
            Task.Run(() => replicas[i % 3].RecordFailureAsync(rules, X, i % 2 == 0 ? AddrA : AddrB, T0, Ct), Ct)));

        var a = await Store.GetAsync(X, AddrA, Ct);
        Assert.Equal(15, a.Scope.FailedCount);
        Assert.Equal(15, (await Store.GetAsync(X, AddrB, Ct)).Scope.FailedCount);
        Assert.Equal(30, a.Account.FailedCount);
    }

    [Fact]
    public async Task ResetClearsOnlyItsOwnScope()
    {
        var rules = Rules(1);
        await Store.RecordFailureAsync(rules, X, AddrA, T0, Ct);
        await Store.RecordFailureAsync(rules, X, AddrB, T0, Ct);

        await Store.ResetAsync(X, AddrA, Ct);

        var a = await Store.GetAsync(X, AddrA, Ct);
        Assert.Equal(LockoutScope.Empty, a.Scope);
        Assert.Equal(2, a.Account.FailedCount);
        Assert.True((await Store.GetAsync(X, AddrB, Ct)).Scope.IsLocked(T0, out _));
    }

    [Fact]
    public async Task ExpiredScopesStartOverAndStaleRowsArePruned()
    {
        for (var i = 0; i < 5; i++)
        {
            await Store.RecordFailureAsync(LockoutRules.Default, X, AddrA, T0, Ct);
        }

        var later = await Store.RecordFailureAsync(LockoutRules.Default, X, AddrA, T0.AddMinutes(16), Ct);
        Assert.Equal(new LockoutScope(1, T0.AddMinutes(16), null), later.Scope);

        // A failure for another account prunes X's rows once their window and lock are over.
        await Store.RecordFailureAsync(LockoutRules.Default, Y, AddrA, T0.AddMinutes(40), Ct);
        await using var conn = await NewSource().OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM signin_lockouts WHERE account = @x", conn);
        cmd.Parameters.AddWithValue("x", X);
        Assert.Equal(0L, await cmd.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task KeysAreValidated()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store.GetAsync(X, LockoutKeys.AnyAddress, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.RecordFailureAsync(LockoutRules.Default, "", AddrA, T0, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.ResetAsync(X, "", Ct));
    }
}
