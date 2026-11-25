using Npgsql;
using Testcontainers.PostgreSql;

namespace ShopHub.Api.Tests.Integration.Postgres;

/// <summary>
/// PostgreSQL in the same major version as the CNPG default image on the cluster (as in shop's
/// tests). Each test class gets its own empty database from <see cref="CreateDatabaseAsync"/>.
/// </summary>
public sealed class PostgresContainer : IAsyncLifetime
{
    public const string Image = "postgres:18.7";

    private readonly PostgreSqlContainer _container;

    public PostgresContainer()
    {
        _container = new PostgreSqlBuilder(Image).WithPassword(Password).Build();
    }

    /// <summary>A unique password, so log-leak checks can search for it.</summary>
    public string Password { get; } = "pg-" + Guid.NewGuid().ToString("N");

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>The CNPG-style URI ShopHub gets as <c>DATABASE_URL</c>.</summary>
    public string DatabaseUrl(string database, string? password = null) =>
        $"postgresql://postgres:{password ?? Password}@{_container.Hostname}:{_container.GetMappedPublicPort(5432)}/{database}";

    /// <summary>Creates an empty database and returns its <c>DATABASE_URL</c>.</summary>
    public async Task<string> CreateDatabaseUrlAsync(string name)
    {
        await CreateDatabaseAsync(name);
        return DatabaseUrl(name);
    }

    /// <summary>Creates an empty database and returns an Npgsql connection string for it.</summary>
    public async Task<string> CreateDatabaseAsync(string name)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }
}
