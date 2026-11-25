using System.Globalization;
using System.Security.Cryptography;
using Npgsql;
using ShopHub.Api.Storage.Postgres;

namespace ShopHub.Api.Shops;

/// <summary>Values of data-model.md <c>shops.availability</c> (standard = 2 copies, high = 3).</summary>
public static class Availability
{
    public const string Standard = "standard";
    public const string High = "high";

    public static bool IsValid(string? value) => value is Standard or High;
}

/// <summary>Values of data-model.md <c>shops.database</c> (standard = PostgreSQL, light = Redis).</summary>
public static class DatabaseKind
{
    public const string Standard = "standard";
    public const string Light = "light";

    public static bool IsValid(string? value) => value is Standard or Light;
}

/// <summary>Values of data-model.md <c>shops.state</c>.</summary>
public static class RecordState
{
    public const string Active = "Active";
    public const string Deleting = "Deleting";
}

/// <summary>A row of <c>shops</c> (data-model.md "Shop record", FR-015).</summary>
public sealed record ShopRecord(
    string Id,
    Guid OwnerId,
    string DisplayName,
    string Availability,
    string Database,
    string WalletAddress,
    int WalletGeneration,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A validated create request: trimmed name, known enum values, EIP-55 wallet address.</summary>
public sealed record NewShop(string DisplayName, string Availability, string Database, string WalletAddress);

/// <summary>Validation of data-model.md <c>shops</c> (FR-006). Field keys match the API's request fields.</summary>
public static class ShopRules
{
    public const int MaxNameLength = 100;
    public const string NameProblem = "Enter a name of 1 to 100 characters, without line breaks or control characters.";
    public const string AvailabilityProblem = "Choose standard or high availability.";
    public const string DatabaseProblem = "Choose the standard or the light database.";

    /// <summary>
    /// <c>display_name</c>: trimmed, 1–100 characters counted as Unicode code points (what
    /// PostgreSQL's <c>length()</c> and the Shop CRD's <c>maxLength</c> count). Narrowed here: no
    /// control characters, since the name is shown in the panel and copied into the Shop.
    /// </summary>
    public static string? Name(string? name, out string? problem)
    {
        var n = name?.Trim() ?? "";
        var length = n.EnumerateRunes().Count();
        if (length is 0 or > MaxNameLength || n.Any(char.IsControl))
        {
            problem = NameProblem;
            return null;
        }

        problem = null;
        return n;
    }

    /// <summary><c>display_name_key</c>: unique per owner, ignoring letter case and surrounding spaces.</summary>
    public static string NameKey(string displayName) => displayName.Trim().ToLowerInvariant();

    /// <summary>Validates a create request; returns the shop, or null and the problems by field.</summary>
    public static NewShop? Validate(
        string? name, string? availability, string? database, string? walletAddress, out Dictionary<string, string[]> problems)
    {
        problems = [];
        var displayName = Name(name, out var nameProblem);
        if (nameProblem is not null)
        {
            problems["name"] = [nameProblem];
        }

        if (!Availability.IsValid(availability))
        {
            problems["availability"] = [AvailabilityProblem];
        }

        if (!DatabaseKind.IsValid(database))
        {
            problems["database"] = [DatabaseProblem];
        }

        var wallet = Eip55.Normalize(walletAddress, out var walletProblem);
        if (walletProblem is not null)
        {
            problems["walletAddress"] = [walletProblem];
        }

        return problems.Count == 0 ? new NewShop(displayName!, availability!, database!, wallet!) : null;
    }
}

/// <summary>Generated shop identifiers (research R9): <c>s</c> + 9 random lowercase base32 characters.</summary>
public static class ShopIds
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string New() => "s" + new string(RandomNumberGenerator.GetItems<char>(Alphabet, 9));
}

public enum CreateOutcome
{
    /// <summary>A new record was inserted.</summary>
    Created,

    /// <summary>The same shop was already created (double submit or retry, SC-010); nothing inserted.</summary>
    Existing,

    /// <summary>The owner has another shop with this name (ignoring case and spaces) and different settings.</summary>
    DuplicateName,

    /// <summary>The owner already has the maximum number of shops, in any state (FR-011).</summary>
    LimitReached,
}

public sealed record CreateResult(CreateOutcome Outcome, ShopRecord? Shop = null);

/// <summary>
/// Shop records. Every method that serves an owner takes the owner id and filters on it (FR-005):
/// another owner's shop and a missing one look the same (null).
/// </summary>
public interface IShopStore
{
    /// <summary>
    /// Creates the record in one transaction under a row lock on the owner, so the limit and the
    /// repeat check hold with any number of concurrent requests and replicas.
    /// </summary>
    Task<CreateResult> CreateAsync(Guid ownerId, NewShop shop, int maxShops, DateTimeOffset now, CancellationToken ct);

    /// <summary>The owner's shops, newest first.</summary>
    Task<IReadOnlyList<ShopRecord>> ListAsync(Guid ownerId, CancellationToken ct);

    Task<ShopRecord?> GetAsync(Guid ownerId, string id, CancellationToken ct);
}

public sealed class PostgresShopStore(NpgsqlDataSource dataSource, Func<string>? newId = null) : IShopStore
{
    private const string Columns =
        "id, owner_id, display_name, availability, database, wallet_address, wallet_generation, state, created_at, updated_at";

    // Collisions are about 1 in 35 billion per pair of shops; a few retries are plenty.
    private const int IdAttempts = 5;

    private readonly Func<string> _newId = newId ?? ShopIds.New;

    public async Task<CreateResult> CreateAsync(
        Guid ownerId, NewShop shop, int maxShops, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Serialises every create of this owner; later statements see what earlier ones committed.
        await using (var lockOwner = Pg.Command(conn, "SELECT 1 FROM owners WHERE id = @owner FOR UPDATE", ("owner", ownerId)))
        {
            if (await lockOwner.ExecuteScalarAsync(ct) is null)
            {
                throw new InvalidOperationException("The owner does not exist.");
            }
        }

        var sameName = await ReadOneAsync(conn, "owner_id = @owner AND display_name_key = @key", ct,
            ("owner", ownerId), ("key", ShopRules.NameKey(shop.DisplayName)));
        if (sameName is not null)
        {
            await tx.CommitAsync(ct);
            return IsRepeat(sameName, shop)
                ? new CreateResult(CreateOutcome.Existing, sameName)
                : new CreateResult(CreateOutcome.DuplicateName);
        }

        await using (var count = Pg.Command(conn, "SELECT count(*) FROM shops WHERE owner_id = @owner", ("owner", ownerId)))
        {
            if ((long)(await count.ExecuteScalarAsync(ct))! >= maxShops)
            {
                await tx.CommitAsync(ct);
                return new CreateResult(CreateOutcome.LimitReached);
            }
        }

        for (var attempt = 0; attempt < IdAttempts; attempt++)
        {
            var inserted = await InsertAsync(conn, ShopNames.Checked(_newId()), ownerId, shop, now, ct);
            if (inserted is not null)
            {
                await tx.CommitAsync(ct);
                return new CreateResult(CreateOutcome.Created, inserted);
            }
        }

        throw new InvalidOperationException("Could not generate an unused shop id.");
    }

    public async Task<IReadOnlyList<ShopRecord>> ListAsync(Guid ownerId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = Pg.Command(conn,
            $"SELECT {Columns} FROM shops WHERE owner_id = @owner ORDER BY created_at DESC, id", ("owner", ownerId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var shops = new List<ShopRecord>();
        while (await reader.ReadAsync(ct))
        {
            shops.Add(Read(reader));
        }

        return shops;
    }

    public async Task<ShopRecord?> GetAsync(Guid ownerId, string id, CancellationToken ct)
    {
        if (!ShopNames.IsValidId(id))
        {
            return null;
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await ReadOneAsync(conn, "owner_id = @owner AND id = @id", ct, ("owner", ownerId), ("id", id));
    }

    /// <summary>
    /// A repeat of the same create: same name as entered (after trimming), same settings, and
    /// not being deleted. Anything else with the same name key is a duplicate name.
    /// </summary>
    private static bool IsRepeat(ShopRecord existing, NewShop shop) =>
        existing.State == RecordState.Active
        && existing.DisplayName == shop.DisplayName
        && existing.Availability == shop.Availability
        && existing.Database == shop.Database
        && existing.WalletAddress == shop.WalletAddress;

    private static Task<ShopRecord?> ReadOneAsync(
        NpgsqlConnection conn, string where, CancellationToken ct, params (string Name, object? Value)[] parameters) =>
        QueryOneAsync(conn, $"SELECT {Columns} FROM shops WHERE {where}", ct, parameters);

    /// <summary>Inserts with the given id; null when that id is already taken.</summary>
    private static Task<ShopRecord?> InsertAsync(
        NpgsqlConnection conn, string id, Guid ownerId, NewShop shop, DateTimeOffset now, CancellationToken ct) =>
        QueryOneAsync(conn, $"""
            INSERT INTO shops (id, owner_id, display_name, display_name_key, availability, database,
                               wallet_address, wallet_generation, state, created_at, updated_at)
            VALUES (@id, @owner, @name, @key, @availability, @database, @wallet, 1, @state, @now, @now)
            ON CONFLICT (id) DO NOTHING
            RETURNING {Columns}
            """, ct, ("id", id), ("owner", ownerId), ("name", shop.DisplayName),
            ("key", ShopRules.NameKey(shop.DisplayName)), ("availability", shop.Availability),
            ("database", shop.Database), ("wallet", shop.WalletAddress), ("state", RecordState.Active),
            ("now", Pg.Time(now)));

    private static async Task<ShopRecord?> QueryOneAsync(
        NpgsqlConnection conn, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = Pg.Command(conn, sql, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    private static ShopRecord Read(NpgsqlDataReader r) => new(
        r.GetString(0),
        r.GetGuid(1),
        r.GetString(2),
        r.GetString(3),
        r.GetString(4),
        r.GetString(5),
        r.GetInt32(6),
        r.GetString(7),
        r.GetFieldValue<DateTimeOffset>(8),
        r.GetFieldValue<DateTimeOffset>(9));
}

/// <summary>Formats used in messages (kept here so the API and tests agree).</summary>
public static class ShopMessages
{
    public static string Limit(int max) =>
        string.Create(CultureInfo.InvariantCulture, $"You already have {max} shops, the most an owner can have. Delete one to create another.");

    public const string DuplicateName = "You already have a shop with this name.";
}
