using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;

namespace Aria2Fast.Web.Infrastructure;

public static class ListenPorts
{
    public static bool InUse(Exception error) => error is AddressInUseException
        || error is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
        || error.InnerException is { } inner && InUse(inner);

    public static string Next(string urls) => string.Join(';', urls.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(address =>
    {
        // Keep wildcard bindings intact; Uri normalizes them only for parsing.
        var normalized = address.Replace("://*:", "://0.0.0.0:").Replace("://+:", "://0.0.0.0:");
        var uri = new Uri(normalized);
        if (uri.Scheme is not ("http" or "https") || uri.Port is <= 0 or >= 65535)
            throw new InvalidOperationException("监听端口无法继续递增：" + address);
        var next = new UriBuilder(uri) { Port = uri.Port + 1 }.Uri.GetLeftPart(UriPartial.Authority);
        if (address.Contains("://*:")) next = next.Replace("://0.0.0.0:", "://*:");
        if (address.Contains("://+:")) next = next.Replace("://0.0.0.0:", "://+:");
        return next;
    }));
}
