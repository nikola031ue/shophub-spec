using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;
using ShopHub.Api.Auth;
using ShopHub.Api.Storage.Postgres;

namespace ShopHub.Api.Accounts;

/// <summary>An owner as sign-in needs it (data-model.md <c>owners</c>). The hash never leaves the server.</summary>
public sealed record OwnerRecord(Guid Id, string Email, string PasswordHash);

/// <summary>Owner accounts in <c>owners</c>.</summary>
public interface IOwnerStore
{
    /// <summary>Inserts the owner unless the normalised email exists; true if it was created.</summary>
    Task<bool> CreateIfAbsentAsync(Guid id, string email, string emailNormalized, string passwordHash, DateTimeOffset now, CancellationToken ct);

    Task<OwnerRecord?> FindByEmailAsync(string emailNormalized, CancellationToken ct);

    Task<OwnerRecord?> GetAsync(Guid id, CancellationToken ct);

    Task SetPasswordHashAsync(Guid id, string passwordHash, CancellationToken ct);
}

public sealed class PostgresOwnerStore(NpgsqlDataSource dataSource) : IOwnerStore
{
    public async Task<bool> CreateIfAbsentAsync(
        Guid id, string email, string emailNormalized, string passwordHash, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await Pg.ExecuteAsync(conn, """
            INSERT INTO owners (id, email, email_normalized, password_hash, created_at)
            VALUES (@id, @email, @normalized, @hash, @now)
            ON CONFLICT (email_normalized) DO NOTHING
            """, ct, ("id", id), ("email", email), ("normalized", emailNormalized), ("hash", passwordHash),
            ("now", Pg.Time(now))) == 1;
    }

    public Task<OwnerRecord?> FindByEmailAsync(string emailNormalized, CancellationToken ct) =>
        ReadOneAsync("email_normalized = @key", emailNormalized, ct);

    public Task<OwnerRecord?> GetAsync(Guid id, CancellationToken ct) => ReadOneAsync("id = @key", id, ct);

    public async Task SetPasswordHashAsync(Guid id, string passwordHash, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await Pg.ExecuteAsync(conn, "UPDATE owners SET password_hash = @hash WHERE id = @id", ct, ("hash", passwordHash), ("id", id));
    }

    private async Task<OwnerRecord?> ReadOneAsync(string where, object key, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = Pg.Command(conn, $"SELECT id, email, password_hash FROM owners WHERE {where}", ("key", key));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new OwnerRecord(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)) : null;
    }
}

/// <summary>Sessions in <c>owner_sessions</c> (FR-002): one row per signed-in browser, 8 hours.</summary>
public sealed class PostgresSessionStore(NpgsqlDataSource dataSource) : ISessionStore
{
    /// <summary>Creates a session row and returns its id; the owner's expired rows are removed on the way.</summary>
    public async Task<Guid> CreateAsync(Guid ownerId, DateTimeOffset now, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await Pg.ExecuteAsync(conn, "DELETE FROM owner_sessions WHERE owner_id = @owner AND expires_at <= @now", ct,
            ("owner", ownerId), ("now", Pg.Time(now)));
        await Pg.ExecuteAsync(conn, """
            INSERT INTO owner_sessions (id, owner_id, created_at, expires_at) VALUES (@id, @owner, @now, @expires)
            """, ct, ("id", id), ("owner", ownerId), ("now", Pg.Time(now)), ("expires", Pg.Time(now + Sessions.Lifetime)));
        return id;
    }

    public async Task<bool> IsActiveAsync(Guid ownerId, Guid sessionId, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = Pg.Command(conn,
            "SELECT 1 FROM owner_sessions WHERE id = @id AND owner_id = @owner AND expires_at > @now",
            ("id", sessionId), ("owner", ownerId), ("now", Pg.Time(now)));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task EndAsync(Guid sessionId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await Pg.ExecuteAsync(conn, "DELETE FROM owner_sessions WHERE id = @id", ct, ("id", sessionId));
    }

    public async Task EndOthersAsync(Guid ownerId, Guid keep, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await Pg.ExecuteAsync(conn, "DELETE FROM owner_sessions WHERE owner_id = @owner AND id <> @keep", ct,
            ("owner", ownerId), ("keep", keep));
    }
}

/// <summary>
/// Data-Protection keys in <c>data_protection_keys</c>, so every replica reads every cookie (ported
/// from shop's <c>StoreXmlRepository</c>). The framework calls this rarely and caches the result,
/// so the blocking calls are acceptable.
/// </summary>
public sealed class PostgresXmlRepository(NpgsqlDataSource dataSource) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var conn = dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand("SELECT xml FROM data_protection_keys ORDER BY id", conn);
        using var reader = cmd.ExecuteReader();
        var keys = new List<XElement>();
        while (reader.Read())
        {
            keys.Add(XElement.Parse(reader.GetString(0)));
        }

        return keys;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var conn = dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "INSERT INTO data_protection_keys (id, xml) VALUES (@id, @xml) ON CONFLICT (id) DO UPDATE SET xml = @xml", conn);
        cmd.Parameters.AddWithValue("id", friendlyName);
        cmd.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting));
        cmd.ExecuteNonQuery();
    }
}
