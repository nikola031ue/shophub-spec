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

        Assert.Equal(new ReconcilePass(true, 2, 0), pass);
        Assert.Equal(
            new[] { a, b }.Select(Reconciler.Resources).OrderBy(r => r.Id, StringComparer.Ordinal),
            gateway.Applied.OrderBy(r => r.Id, StringComparer.Ordinal));
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

        public IReadOnlyList<ShopResources> Applied => [.. _applied];

        public int Resolutions => _resolutions;

        public FakeGateway Resolve()
        {
            Interlocked.Increment(ref _resolutions);
            return this;
        }

        public async Task ApplyAsync(ShopResources shop, CancellationToken ct)
        {
            await Task.Delay(Delay, ct);
            if (shop.Id == FailFor)
            {
                throw new KubeValidationException("refused");
            }

            _applied.Enqueue(shop);
        }

        public Task DeleteWalletAsync(string id, int generation, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteShopAsync(string id, int walletGeneration, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> RemainingAsync(string id, int walletGeneration, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ShopStatus?> GetShopStatusAsync(string id, CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> ReadAdminPasswordAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }
}
