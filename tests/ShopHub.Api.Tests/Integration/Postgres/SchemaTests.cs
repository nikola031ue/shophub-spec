using Npgsql;
using ShopHub.Api.Storage.Postgres;

namespace ShopHub.Api.Tests.Integration.Postgres;

/// <summary>T013 schema (data-model.md "ShopHub database"): tables, keys and the rules the database enforces.</summary>
public sealed class SchemaTests(PostgresContainer db) : IClassFixture<PostgresContainer>, IAsyncLifetime
{
    private static readonly Guid Owner = Guid.Parse("0193a5e0-0000-7000-8000-000000000001");
    private static readonly Guid OtherOwner = Guid.Parse("0193a5e0-0000-7000-8000-000000000002");
    private const string Wallet = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed";

    private NpgsqlDataSource _source = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _source = NpgsqlDataSource.Create(await db.CreateDatabaseAsync("schema_" + Guid.NewGuid().ToString("N")));
        await SchemaMigrator.MigrateAsync(_source, Ct);
        await ExecAsync("INSERT INTO owners (id, email, email_normalized, password_hash, created_at) VALUES (@o, 'A@Example.test', 'a@example.test', 'h', now())", ("o", Owner));
        await ExecAsync("INSERT INTO owners (id, email, email_normalized, password_hash, created_at) VALUES (@o, 'b@example.test', 'b@example.test', 'h', now())", ("o", OtherOwner));
    }

    public async ValueTask DisposeAsync() => await _source.DisposeAsync();

    private async Task<int> ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = _source.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return await cmd.ExecuteNonQueryAsync(Ct);
    }

    private Task<int> InsertShopAsync(string id, Guid owner, string name = "My shop", string availability = "standard",
        string database = "standard", string wallet = Wallet, string state = "Active") => ExecAsync("""
        INSERT INTO shops (id, owner_id, display_name, display_name_key, availability, database, wallet_address, state, created_at, updated_at)
        VALUES (@id, @owner, @name, lower(trim(@name)), @availability, @database, @wallet, @state, now(), now())
        """, ("id", id), ("owner", owner), ("name", name), ("availability", availability), ("database", database),
        ("wallet", wallet), ("state", state));

    private static async Task<PostgresException> RejectedAsync(Func<Task> action) =>
        await Assert.ThrowsAsync<PostgresException>(action);

    [Fact]
    public async Task EveryTableExists()
    {
        await using var cmd = _source.CreateCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' ORDER BY table_name");
        var tables = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                tables.Add(reader.GetString(0));
            }
        }

        Assert.Equal(["data_protection_keys", "owner_sessions", "owners", "schema_version", "shops", "signin_lockouts"], tables);
    }

    [Fact]
    public async Task EmailsAreUniqueRegardlessOfCase()
    {
        var ex = await RejectedAsync(() => ExecAsync(
            "INSERT INTO owners (id, email, email_normalized, password_hash, created_at) VALUES (gen_random_uuid(), 'a@EXAMPLE.test', 'a@example.test', 'h', now())"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);

        // The normalised form must match the email.
        ex = await RejectedAsync(() => ExecAsync(
            "INSERT INTO owners (id, email, email_normalized, password_hash, created_at) VALUES (gen_random_uuid(), 'C@example.test', 'C@example.test', 'h', now())"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task SessionsBelongToAnOwnerAndGoWithIt()
    {
        await ExecAsync("INSERT INTO owner_sessions (id, owner_id, created_at, expires_at) VALUES (gen_random_uuid(), @o, now(), now() + interval '8 hours')", ("o", Owner));

        var ex = await RejectedAsync(() => ExecAsync(
            "INSERT INTO owner_sessions (id, owner_id, created_at, expires_at) VALUES (gen_random_uuid(), gen_random_uuid(), now(), now() + interval '8 hours')"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);

        await ExecAsync("DELETE FROM owners WHERE id = @o", ("o", Owner));
        await using var count = _source.CreateCommand("SELECT count(*) FROM owner_sessions");
        Assert.Equal(0L, await count.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task DisplayNamesAreUniquePerOwnerRegardlessOfCase()
    {
        await InsertShopAsync("sabcdefgh2", Owner, "My shop");

        var ex = await RejectedAsync(() => InsertShopAsync("sabcdefgh3", Owner, " MY SHOP "));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);

        // Another owner may use the same name.
        Assert.Equal(1, await InsertShopAsync("sabcdefgh4", OtherOwner, "My shop"));
    }

    [Theory]
    [InlineData("SABCDEFGH2", "standard", "standard", Wallet, "Active")]   // id format
    [InlineData("sabcdefgh1", "standard", "standard", Wallet, "Active")]   // 1 is not base32
    [InlineData("sabcdefgh2", "premium", "standard", Wallet, "Active")]
    [InlineData("sabcdefgh2", "standard", "mongo", Wallet, "Active")]
    [InlineData("sabcdefgh2", "standard", "standard", "0x123", "Active")]
    [InlineData("sabcdefgh2", "standard", "standard", Wallet, "Gone")]
    public async Task ShopColumnsAreChecked(string id, string availability, string database, string wallet, string state)
    {
        var ex = await RejectedAsync(() => InsertShopAsync(id, Owner, "Shop", availability, database, wallet, state));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task DisplayNamesAreOneTo100Characters()
    {
        Assert.Equal(1, await InsertShopAsync("sabcdefgh2", Owner, new string('x', 100)));
        var ex = await RejectedAsync(() => InsertShopAsync("sabcdefgh3", Owner, new string('y', 101)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task NewShopsStartAtWalletGenerationOne()
    {
        await InsertShopAsync("sabcdefgh2", Owner);
        await using var cmd = _source.CreateCommand("SELECT wallet_generation FROM shops WHERE id = 'sabcdefgh2'");
        Assert.Equal(1, await cmd.ExecuteScalarAsync(Ct));
    }
}
