using System.Net.Sockets;
using Npgsql;

namespace ShopHub.Api.Storage;

/// <summary>Classifies exceptions that mean "the database doesn't answer" (503, not 500), as in shop.</summary>
public static class StoreErrors
{
    public static bool IsUnavailable(Exception? ex) => ex switch
    {
        null => false,
        NpgsqlException { IsTransient: true } => true,
        TimeoutException or SocketException => true,
        _ => IsUnavailable(ex.InnerException),
    };
}
