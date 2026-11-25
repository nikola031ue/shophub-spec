using System.Data.Common;
using Npgsql;
using NpgsqlTypes;

namespace ShopHub.Api.Storage.Postgres;

/// <summary>Small Npgsql helpers shared by the stores (as in shop's <c>PostgresShopStore</c>).</summary>
internal static class Pg
{
    /// <summary>Times are stored with millisecond precision, so values read back compare equal.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset t) =>
        DateTimeOffset.FromUnixTimeMilliseconds(t.ToUnixTimeMilliseconds());

    public static NpgsqlParameter Time(DateTimeOffset? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = value is { } v ? Truncate(v) : DBNull.Value };

    public static DateTimeOffset? NullableTime(DbDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    public static NpgsqlCommand Command(NpgsqlConnection conn, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            if (value is NpgsqlParameter p)
            {
                p.ParameterName = name;
                cmd.Parameters.Add(p);
            }
            else
            {
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
        }

        return cmd;
    }

    public static async Task<int> ExecuteAsync(
        NpgsqlConnection conn, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = Command(conn, sql, parameters);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
