using System.Net;
using System.Net.Sockets;
using k8s.Autorest;
using k8s.Exceptions;
using Npgsql;

namespace ShopHub.Api.Storage;

/// <summary>
/// Classifies exceptions that mean "a service ShopHub depends on doesn't answer" (503, not 500;
/// contracts/panel-api.md "Limits and errors"): the database, as in shop, and the Kubernetes API.
/// </summary>
public static class StoreErrors
{
    public static bool IsUnavailable(Exception? ex) => ex switch
    {
        null => false,
        NpgsqlException { IsTransient: true } => true,
        TimeoutException or SocketException => true,
        // Kubernetes API: no connection, no usable configuration, or the server itself failing.
        HttpRequestException => true,
        KubeConfigException => true,
        HttpOperationException { Response.StatusCode: >= HttpStatusCode.InternalServerError or HttpStatusCode.TooManyRequests } => true,
        _ => IsUnavailable(ex.InnerException),
    };
}
