using Npgsql;
using ShopHub.Api.Accounts;
using ShopHub.Api.Shops;
using ShopHub.Api.Storage.Postgres;

namespace ShopHub.Api.Tests.Integration.Postgres;

/// <summary>
/// Shop records on PostgreSQL (T017): one transaction per create under a row lock on the owner,
/// the per-owner limit (FR-011), repeat-safe create (SC-010) and owner filtering (FR-005). Each test
/// class gets its own empty, migrated database; "replicas" are separate data sources.
/// </summary>
public sealed class ShopStoreTests(PostgresContainer db) : IClassFixture<PostgresContainer>, IAsyncLifetime
{
    private const int Max = 5;
    private const string Wallet = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed";
    private const string OtherWallet = "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359";
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    private readonly List<NpgsqlDataSource> _sources = [];
    private string _connectionString = "";
    private Guid _ownerA;
    private Guid _ownerB;

    private PostgresShopStore Store { get; set; } = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await db.CreateDatabaseAsync("shops_" + Guid.NewGuid().ToString("N"));
        var source = NewSource();
        await SchemaMigrator.MigrateAsync(source, Ct);
        Store = new PostgresShopStore(source);
        _ownerA = await NewOwnerAsync("a@example.test");
        _ownerB = await NewOwnerAsync("b@example.test");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sources)
        {
            await s.DisposeAsync();
        }
    }

    private NpgsqlDataSource NewSource()
    {
        var source = NpgsqlDataSource.Create(_connectionString);
        _sources.Add(source);
        return source;
    }

    private async Task<Guid> NewOwnerAsync(string email)
    {
        var id = Guid.CreateVersion7();
        await new PostgresOwnerStore(_sources[0]).CreateIfAbsentAsync(id, email, email, "hash", T0, Ct);
        return id;
    }

    private static NewShop Shop(string name, string availability = Availability.Standard, string database = DatabaseKind.Standard,
        string wallet = Wallet) => new(name, availability, database, wallet);

    private async Task<long> CountAsync(Guid? owner = null)
    {
        await using var conn = await _sources[0].OpenConnectionAsync(Ct);
        await using var cmd = owner is { } o
            ? new NpgsqlCommand("SELECT count(*) FROM shops WHERE owner_id = @o", conn) { Parameters = { new("o", o) } }
            : new NpgsqlCommand("SELECT count(*) FROM shops", conn);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task CreateInsertsAnActiveRecordAtGenerationOne()
    {
        var result = await Store.CreateAsync(_ownerA, Shop("Acme", Availability.High, DatabaseKind.Light), Max, T0, Ct);

        Assert.Equal(CreateOutcome.Created, result.Outcome);
        var shop = result.Shop!;
        Assert.True(ShopNames.IsValidId(shop.Id));
        Assert.Equal(
            new ShopRecord(shop.Id, _ownerA, "Acme", Availability.High, DatabaseKind.Light, Wallet, 1, RecordState.Active, T0, T0),
            shop);
        Assert.Equal(shop, await Store.GetAsync(_ownerA, shop.Id, Ct));
    }

    [Fact]
    public async Task ListIsTheOwnersShopsNewestFirst()
    {
        await Store.CreateAsync(_ownerA, Shop("First"), Max, T0, Ct);
        await Store.CreateAsync(_ownerA, Shop("Second"), Max, T0.AddMinutes(1), Ct);
        await Store.CreateAsync(_ownerB, Shop("Other owner"), Max, T0.AddMinutes(2), Ct);

        Assert.Equal(["Second", "First"], (await Store.ListAsync(_ownerA, Ct)).Select(s => s.DisplayName));
        Assert.Equal(["Other owner"], (await Store.ListAsync(_ownerB, Ct)).Select(s => s.DisplayName));
    }

    [Fact]
    public async Task AnotherOwnersShopLooksLikeAMissingOne()
    {
        var a = (await Store.CreateAsync(_ownerA, Shop("Acme"), Max, T0, Ct)).Shop!;

        Assert.Null(await Store.GetAsync(_ownerB, a.Id, Ct));
        Assert.Null(await Store.GetAsync(_ownerA, "s222222222", Ct));
        Assert.Null(await Store.GetAsync(_ownerA, "not-an-id'; --", Ct));
    }

    [Fact]
    public async Task TheSameNameIsFreeForAnotherOwner()
    {
        await Store.CreateAsync(_ownerA, Shop("Acme"), Max, T0, Ct);

        Assert.Equal(CreateOutcome.Created, (await Store.CreateAsync(_ownerB, Shop("Acme"), Max, T0, Ct)).Outcome);
    }

    [Theory]
    [InlineData("acme")]
    [InlineData("ACME")]
    [InlineData("Acme", Availability.High)]
    [InlineData("Acme", Availability.Standard, DatabaseKind.Light)]
    [InlineData("Acme", Availability.Standard, DatabaseKind.Standard, OtherWallet)]
    public async Task TheSameNameWithOtherCaseOrSettingsIsADuplicate(
        string name, string availability = Availability.Standard, string database = DatabaseKind.Standard, string wallet = Wallet)
    {
        await Store.CreateAsync(_ownerA, Shop("Acme"), Max, T0, Ct);

        var result = await Store.CreateAsync(_ownerA, Shop(name, availability, database, wallet), Max, T0, Ct);

        Assert.Equal(new CreateResult(CreateOutcome.DuplicateName), result);
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task ARepeatedCreateReturnsTheExistingShop()
    {
        var first = (await Store.CreateAsync(_ownerA, Shop("Acme"), Max, T0, Ct)).Shop!;

        var again = await Store.CreateAsync(_ownerA, Shop("Acme"), Max, T0.AddSeconds(3), Ct);

        Assert.Equal(new CreateResult(CreateOutcome.Existing, first), again);
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task ARepeatOfTheFifthShopIsNotRefusedByTheLimit()
    {
        for (var i = 1; i <= Max; i++)
        {
            await Store.CreateAsync(_ownerA, Shop($"Shop {i}"), Max, T0, Ct);
        }

        Assert.Equal(CreateOutcome.Existing, (await Store.CreateAsync(_ownerA, Shop($"Shop {Max}"), Max, T0, Ct)).Outcome);
    }

    /// <summary>SC-010: a double submit from three replicas at once still makes one shop.</summary>
    [Fact]
    public async Task ConcurrentRepeatsMakeOneShop()
    {
        var stores = Enumerable.Range(0, 3).Select(_ => new PostgresShopStore(NewSource())).ToList();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            stores[i % 3].CreateAsync(_ownerA, Shop("Acme"), Max, T0, Ct)));

        Assert.Single(results, r => r.Outcome == CreateOutcome.Created);
        Assert.All(results, r => Assert.Equal(results.Single(x => x.Outcome == CreateOutcome.Created).Shop, r.Shop));
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task TheSixthShopIsRefused()
    {
        for (var i = 1; i <= Max; i++)
        {
            Assert.Equal(CreateOutcome.Created, (await Store.CreateAsync(_ownerA, Shop($"Shop {i}"), Max, T0, Ct)).Outcome);
        }

        Assert.Equal(new CreateResult(CreateOutcome.LimitReached), await Store.CreateAsync(_ownerA, Shop("Sixth"), Max, T0, Ct));
        Assert.Equal(CreateOutcome.Created, (await Store.CreateAsync(_ownerB, Shop("Sixth"), Max, T0, Ct)).Outcome);
    }

    [Fact]
    public async Task ShopsBeingDeletedCountTowardsTheLimitAndAreNotRepeats()
    {
        for (var i = 1; i <= Max; i++)
        {
            await Store.CreateAsync(_ownerA, Shop($"Shop {i}"), Max, T0, Ct);
        }

        await using (var conn = await _sources[0].OpenConnectionAsync(Ct))
        {
            await Pg.ExecuteAsync(conn, "UPDATE shops SET state = 'Deleting'", Ct);
        }

        Assert.Equal(CreateOutcome.LimitReached, (await Store.CreateAsync(_ownerA, Shop("New"), Max, T0, Ct)).Outcome);
        Assert.Equal(CreateOutcome.DuplicateName, (await Store.CreateAsync(_ownerA, Shop("Shop 1"), Max, T0, Ct)).Outcome);
    }

    /// <summary>FR-011 under contention: 12 different shops at once from three replicas, exactly 5 made.</summary>
    [Fact]
    public async Task ConcurrentCreatesNeverPassTheLimit()
    {
        var stores = Enumerable.Range(0, 3).Select(_ => new PostgresShopStore(NewSource())).ToList();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            stores[i % 3].CreateAsync(_ownerA, Shop($"Shop {i}"), Max, T0, Ct)));

        Assert.Equal(Max, results.Count(r => r.Outcome == CreateOutcome.Created));
        Assert.Equal(12 - Max, results.Count(r => r.Outcome == CreateOutcome.LimitReached));
        Assert.Equal(Max, await CountAsync(_ownerA));
    }

    [Fact]
    public async Task AnIdCollisionIsRetriedWithANewId()
    {
        var taken = (await Store.CreateAsync(_ownerA, Shop("Acme"), Max, T0, Ct)).Shop!.Id;
        var ids = new Queue<string>([taken, taken, "s7k2m4qxab"]);
        var store = new PostgresShopStore(_sources[0], ids.Dequeue);

        var result = await store.CreateAsync(_ownerB, Shop("Other"), Max, T0, Ct);

        Assert.Equal("s7k2m4qxab", result.Shop!.Id);
        Assert.Empty(ids);
        Assert.Equal(_ownerA, (await Store.GetAsync(_ownerA, taken, Ct))!.OwnerId);
    }

    [Fact]
    public async Task AFailedCreateLeavesNothingBehind()
    {
        var store = new PostgresShopStore(_sources[0], () => "S-not-valid");

        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(_ownerA, Shop("Acme"), Max, T0, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.CreateAsync(Guid.CreateVersion7(), Shop("Acme"), Max, T0, Ct));
        Assert.Equal(0, await CountAsync());
    }

    [Theory]
    [InlineData("UPDATE shops SET availability = 'medium'")]
    [InlineData("UPDATE shops SET database = 'mysql'")]
    [InlineData("UPDATE shops SET wallet_address = '0x12'")]
    [InlineData("UPDATE shops SET display_name = ''")]
    [InlineData("UPDATE shops SET display_name_key = 'other'")]
    [InlineData("UPDATE shops SET id = 'S1'")]
    public async Task TheSchemaRefusesInvalidRows(string sql)
    {
        await Store.CreateAsync(_ownerA, Shop("Acme"), Max, T0, Ct);
        await using var conn = await _sources[0].OpenConnectionAsync(Ct);

        await Assert.ThrowsAsync<PostgresException>(() => Pg.ExecuteAsync(conn, sql, Ct));
    }
}
