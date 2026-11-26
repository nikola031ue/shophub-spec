using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ShopHub.Api.Accounts;
using ShopHub.Api.Shops;
using ShopHub.Api.Storage.Postgres;

namespace ShopHub.Api.Tests.Integration.Postgres;

/// <summary>
/// The reconciler's pass logic on PostgreSQL with a fake gateway (T018): which records it applies,
/// one replica at a time, and that one failing shop doesn't stop the others. The real API server
/// is in <c>K3s/ReconcilerTests</c>.
/// </summary>
public sealed class ReconcilerPassTests(PostgresContainer db) : IClassFixture<PostgresContainer>, IAsyncLifetime
{
    private const string Wallet = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed";
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    private readonly List<NpgsqlDataSource> _sources = [];
    private string _connectionString = "";
    private Guid _owner;

    private PostgresShopStore Store { get; set; } = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await db.CreateDatabaseAsync("rec_" + Guid.NewGuid().ToString("N"));
        await SchemaMigrator.MigrateAsync(Source(), Ct);
        Store = new PostgresShopStore(Source());
        _owner = Guid.CreateVersion7();
        await new PostgresOwnerStore(Source()).CreateIfAbsentAsync(_owner, "o@example.test", "o@example.test", "hash", T0, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sources)
        {
            await s.DisposeAsync();
        }
    }

    private NpgsqlDataSource Source()
    {
        var source = NpgsqlDataSource.Create(_connectionString);
        _sources.Add(source);
        return source;
    }

    private Reconciler NewReconciler(FakeGateway gateway) =>
        new(Source(), Store, () => gateway.Resolve(), new ReconcileTrigger(), NullLogger<Reconciler>.Instance);

    private async Task<ShopRecord> CreateAsync(string name) =>
        (await Store.CreateAsync(_owner, new NewShop(name, Availability.Standard, DatabaseKind.Standard, Wallet), 5, T0, Ct)).Shop!;

    [Fact]
    public async Task AppliesEveryActiveRecordAndSkipsDeletingOnes()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        var deleting = await CreateAsync("C");
        await using (var conn = await Source().OpenConnectionAsync(Ct))
        {
            await Pg.ExecuteAsync(conn, "UPDATE shops SET state = 'Deleting' WHERE id = @id", Ct, ("id", deleting.Id));
        }

        var gateway = new FakeGateway();
        var pass = await NewReconciler(gateway).RunOnceAsync(Ct);

        Assert.Equal(new ReconcilePass(true, 2, 0, Removed: 1), pass);
        Assert.Equal(
            new[] { a, b }.Select(Reconciler.Resources).OrderBy(r => r.Id, StringComparer.Ordinal),
            gateway.Applied.OrderBy(r => r.Id, StringComparer.Ordinal));
        Assert.DoesNotContain(gateway.Calls, c => c.StartsWith($"apply {deleting.Id}", StringComparison.Ordinal));
    }

    private Task<UpdateResult> ChangeWalletAsync(ShopRecord shop, string wallet) =>
        Store.UpdateAsync(_owner, shop.Id, new ShopChange(null, wallet), T0, Ct);

    /// <summary>FR-009: new Wallet and Shop first (one apply), then the old Wallet, once.</summary>
    [Fact]
    public async Task AfterAWalletChangeTheOldWalletIsDeletedAfterTheApplyAndOnlyOnce()
    {
        var shop = await CreateAsync("A");
        var gateway = new FakeGateway();
        await NewReconciler(gateway).RunOnceAsync(Ct);
        await ChangeWalletAsync(shop, "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359");

        Assert.Equal(new ReconcilePass(true, 1, 0), await NewReconciler(gateway).RunOnceAsync(Ct));
        await NewReconciler(gateway).RunOnceAsync(Ct);

        Assert.Equal(
            [$"apply {shop.Id}-w1", $"apply {shop.Id}-w2", $"delete {shop.Id}-w1", $"apply {shop.Id}-w2"],
            gateway.Calls);
        Assert.Equal(2, (await Store.GetAsync(_owner, shop.Id, Ct))!.OldestWalletGeneration);
    }

    [Fact]
    public async Task SeveralWalletChangesBetweenPassesDeleteEveryOlderGeneration()
    {
        var shop = await CreateAsync("A");
        await ChangeWalletAsync(shop, "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359");
        await ChangeWalletAsync(shop, "0xdbF03B407c01E7cD3CBea99509d93f8DDDC8C6FB");
        var gateway = new FakeGateway();

        await NewReconciler(gateway).RunOnceAsync(Ct);

        Assert.Equal([$"apply {shop.Id}-w3", $"delete {shop.Id}-w1", $"delete {shop.Id}-w2"], gateway.Calls);
        Assert.Equal(3, (await Store.GetAsync(_owner, shop.Id, Ct))!.OldestWalletGeneration);
    }

    [Fact]
    public async Task AFailedWalletDeleteIsRetriedOnTheNextPass()
    {
        var shop = await CreateAsync("A");
        await ChangeWalletAsync(shop, "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359");
        var gateway = new FakeGateway { FailWalletDeletes = 1 };

        Assert.Equal(new ReconcilePass(true, 0, 1), await NewReconciler(gateway).RunOnceAsync(Ct));
        Assert.Equal(1, (await Store.GetAsync(_owner, shop.Id, Ct))!.OldestWalletGeneration);

        Assert.Equal(new ReconcilePass(true, 1, 0), await NewReconciler(gateway).RunOnceAsync(Ct));
        Assert.Equal([$"apply {shop.Id}-w2", $"apply {shop.Id}-w2", $"delete {shop.Id}-w1"], gateway.Calls);
        Assert.Equal(2, (await Store.GetAsync(_owner, shop.Id, Ct))!.OldestWalletGeneration);
    }

    private async Task<bool> ExistsAsync(string id) =>
        (await Store.ListAllForReconcilerAsync(Ct)).Any(r => r.Id == id);

    private async Task MarkDeletingAsync(ShopRecord shop) =>
        Assert.Equal(DeleteOutcome.Marked, (await Store.MarkDeletingAsync(_owner, shop.Id, shop.DisplayName, T0, Ct)).Outcome);

    /// <summary>FR-013: a deleted shop's objects are deleted by name, and its row goes once none is left.</summary>
    [Fact]
    public async Task ADeletedShopsObjectsAreDeletedAndThenItsRowIsRemoved()
    {
        var shop = await CreateAsync("A");
        await ChangeWalletAsync(shop, "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359");
        await MarkDeletingAsync(shop);
        var gateway = new FakeGateway();

        Assert.Equal(new ReconcilePass(true, 0, 0, Removed: 1), await NewReconciler(gateway).RunOnceAsync(Ct));

        Assert.Equal([$"delete shop {shop.Id} w1..w2"], gateway.Calls);
        Assert.False(await ExistsAsync(shop.Id));
    }

    [Fact]
    public async Task WhileFinalizersHoldObjectsTheRowStaysAndPassesComeSooner()
    {
        var shop = await CreateAsync("A");
        await MarkDeletingAsync(shop);
        var gateway = new FakeGateway();
        gateway.RemainingAnswers.Enqueue([$"discordchannels/{shop.Id}"]);

        Assert.Equal(new ReconcilePass(true, 0, 0, Removed: 0, Waiting: 1), await NewReconciler(gateway).RunOnceAsync(Ct));
        Assert.True(await ExistsAsync(shop.Id));
        Assert.True(Reconciler.DeletingInterval < Reconciler.Interval);

        Assert.Equal(new ReconcilePass(true, 0, 0, Removed: 1), await NewReconciler(gateway).RunOnceAsync(Ct));
        Assert.False(await ExistsAsync(shop.Id));
        // The delete is repeated each pass (by name; already-deleting objects are left as they are).
        Assert.Equal(2, gateway.Calls.Count(c => c.StartsWith("delete shop", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AFailedDeleteKeepsTheRowAndDoesNotStopOtherShops()
    {
        var gone = await CreateAsync("Gone");
        var active = await CreateAsync("Active");
        await MarkDeletingAsync(gone);
        var gateway = new FakeGateway { FailShopDeletes = true };

        Assert.Equal(new ReconcilePass(true, 1, 1), await NewReconciler(gateway).RunOnceAsync(Ct));
        Assert.True(await ExistsAsync(gone.Id));
        Assert.Contains($"apply {active.Id}-w1", gateway.Calls);

        gateway.FailShopDeletes = false;
        Assert.Equal(new ReconcilePass(true, 1, 0, Removed: 1), await NewReconciler(gateway).RunOnceAsync(Ct));
        Assert.False(await ExistsAsync(gone.Id));
    }

    [Fact]
    public async Task WithoutRecordsTheClusterIsNotEvenContacted()
    {
        var gateway = new FakeGateway();

        Assert.Equal(new ReconcilePass(true, 0, 0), await NewReconciler(gateway).RunOnceAsync(Ct));
        Assert.Equal(0, gateway.Resolutions);
    }

    [Fact]
    public async Task OneFailingShopDoesNotStopTheOthers()
    {
        var bad = await CreateAsync("Bad");
        var good = await CreateAsync("Good");
        var gateway = new FakeGateway { FailFor = bad.Id };

        var pass = await NewReconciler(gateway).RunOnceAsync(Ct);

        Assert.Equal(new ReconcilePass(true, 1, 1), pass);
        Assert.Equal([Reconciler.Resources(good)], gateway.Applied);
    }

    [Fact]
    public async Task OnlyOneReplicaReconcilesAtATime()
    {
        await CreateAsync("A");
        var gateway = new FakeGateway();

        // Another replica is mid-pass: it holds the lock in its open transaction.
        await using var other = await Source().OpenConnectionAsync(Ct);
        await using var tx = await other.BeginTransactionAsync(Ct);
        await Pg.ExecuteAsync(other, "SELECT pg_advisory_xact_lock(@key)", Ct, ("key", Reconciler.LockKey));

        Assert.Equal(new ReconcilePass(false, 0, 0), await NewReconciler(gateway).RunOnceAsync(Ct));
        Assert.Empty(gateway.Applied);

        // Once that pass ends the lock is free again, without any explicit unlock.
        await tx.RollbackAsync(Ct);
        Assert.Equal(new ReconcilePass(true, 1, 0), await NewReconciler(gateway).RunOnceAsync(Ct));
    }

    [Fact]
    public async Task ConcurrentPassesFromThreeReplicasApplyEachShopOnce()
    {
        await CreateAsync("A");
        var gateway = new FakeGateway { Delay = TimeSpan.FromSeconds(1) };

        var passes = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => NewReconciler(gateway).RunOnceAsync(Ct)));

        Assert.Single(passes, p => p.Ran);
        Assert.Single(gateway.Applied);
    }

    /// <summary>
    /// One pooled connection only, so the pass after a crash reuses the very connection the crashed
    /// pass held: a lock that outlived its pass (a session lock without unlock) would show here.
    /// </summary>
    private NpgsqlDataSource SingleConnectionSource(string applicationName)
    {
        var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_connectionString)
        {
            MaxPoolSize = 1,
            ApplicationName = applicationName,
        }.ConnectionString);
        _sources.Add(source);
        return source;
    }

    private async Task<long> AdvisoryLocksHeldAsync()
    {
        await using var conn = await Source().OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory'", conn);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task APassAfterACancelledPassStillRuns()
    {
        await CreateAsync("A");
        var gateway = new FakeGateway();
        var reconciler = new Reconciler(SingleConnectionSource("rec-cancel"), Store, () => gateway.Resolve(),
            new ReconcileTrigger(), NullLogger<Reconciler>.Instance);

        // The pass is stopped mid-apply, holding the lock (host shutdown, request aborted).
        using var crash = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        gateway.BeforeApply = async ct =>
        {
            Assert.Equal(1, await AdvisoryLocksHeldAsync());
            await crash.CancelAsync();
            ct.ThrowIfCancellationRequested();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconciler.RunOnceAsync(crash.Token));
        gateway.BeforeApply = null;

        Assert.Equal(0, await AdvisoryLocksHeldAsync());
        Assert.Equal(new ReconcilePass(true, 1, 0), await reconciler.RunOnceAsync(Ct));
    }

    [Fact]
    public async Task APassAfterAPassWhoseConnectionDiedStillRuns()
    {
        await CreateAsync("A");
        var gateway = new FakeGateway();
        var reconciler = new Reconciler(SingleConnectionSource("rec-killed"), Store, () => gateway.Resolve(),
            new ReconcileTrigger(), NullLogger<Reconciler>.Instance);

        // Mid-pass, the database side of the pass's connection goes away (failover, network cut).
        gateway.BeforeApply = async _ =>
        {
            await using var admin = await Source().OpenConnectionAsync(Ct);
            await using var kill = new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = 'rec-killed'", admin);
            await kill.ExecuteNonQueryAsync(Ct);
        };
        await Assert.ThrowsAnyAsync<Exception>(() => reconciler.RunOnceAsync(Ct));
        gateway.BeforeApply = null;

        Assert.Equal(0, await AdvisoryLocksHeldAsync());
        Assert.Equal(new ReconcilePass(true, 1, 0), await reconciler.RunOnceAsync(Ct));
    }

    [Fact]
    public async Task SeveralWakesBeforeAPassGiveOnePass()
    {
        var trigger = new ReconcileTrigger();
        trigger.Wake();
        trigger.Wake();
        trigger.Wake();

        Assert.True(await trigger.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.False(await trigger.WaitAsync(TimeSpan.FromMilliseconds(50), Ct));
    }

    [Fact]
    public async Task TheHostedLoopRunsAPassAtStartAndOnEachWake()
    {
        await CreateAsync("A");
        var gateway = new FakeGateway();
        var trigger = new ReconcileTrigger();
        using var reconciler = new Reconciler(Source(), Store, () => gateway.Resolve(), trigger, NullLogger<Reconciler>.Instance);

        await reconciler.StartAsync(Ct);
        await WaitUntilAsync(() => gateway.Applied.Count == 1);
        trigger.Wake();
        await WaitUntilAsync(() => gateway.Applied.Count == 2);
        await reconciler.StopAsync(Ct);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(condition());
    }

    private sealed class FakeGateway : IKubeGateway
    {
        private readonly ConcurrentQueue<ShopResources> _applied = new();
        private int _resolutions;

        public string? FailFor { get; init; }

        public TimeSpan Delay { get; init; }

        /// <summary>Runs before each apply; lets a test crash the pass at that point.</summary>
        public Func<CancellationToken, Task>? BeforeApply { get; set; }

        public IReadOnlyList<ShopResources> Applied => [.. _applied];

        private readonly ConcurrentQueue<string> _calls = new();

        /// <summary>Applies and Wallet deletes in order, as "apply id-wN" and "delete id-wN".</summary>
        public IReadOnlyList<string> Calls => [.. _calls];

        /// <summary>Deleting this Wallet generation fails (once per entry in the count).</summary>
        public int FailWalletDeletes { get; set; }

        public int Resolutions => _resolutions;

        public FakeGateway Resolve()
        {
            Interlocked.Increment(ref _resolutions);
            return this;
        }

        public async Task ApplyAsync(ShopResources shop, CancellationToken ct)
        {
            if (BeforeApply is { } hook)
            {
                await hook(ct);
            }

            await Task.Delay(Delay, ct);
            if (shop.Id == FailFor)
            {
                throw new KubeValidationException("refused");
            }

            _applied.Enqueue(shop);
            _calls.Enqueue($"apply {shop.Id}-w{shop.WalletGeneration}");
        }

        public Task DeleteWalletAsync(string id, int generation, CancellationToken ct)
        {
            if (FailWalletDeletes > 0)
            {
                FailWalletDeletes--;
                throw new HttpRequestException("API server unreachable");
            }

            _calls.Enqueue($"delete {id}-w{generation}");
            return Task.CompletedTask;
        }

        /// <summary>Objects reported as still existing after a shop delete, per pass, in order.</summary>
        public Queue<string[]> RemainingAnswers { get; } = new();

        public bool FailShopDeletes { get; set; }

        public Task DeleteShopAsync(string id, int walletGeneration, CancellationToken ct)
        {
            if (FailShopDeletes)
            {
                throw new HttpRequestException("API server unreachable");
            }

            _calls.Enqueue($"delete shop {id} w1..w{walletGeneration}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> RemainingAsync(string id, int walletGeneration, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(RemainingAnswers.TryDequeue(out var names) ? names : []);

        public Task<ShopStatus?> GetShopStatusAsync(string id, CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> ReadAdminPasswordAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }
}
