using System.Net.Sockets;
using System.Security.Authentication;

namespace BetterDns.Core.Routing;

public static class ResolverFailure
{
    // Stable, localizable codes. Do not expose endpoint/profile URLs from exception messages.
    public static string Classify(Exception error)
    {
        if (error is OperationCanceledException or TimeoutException) return "timeout";
        for (Exception? inner = error; inner is not null; inner = inner.InnerException)
            if (inner is AuthenticationException) return "tls";
        if (error is InvalidDataException or FormatException) return "invalid-response";
        if (error is HttpRequestException { StatusCode: { } status }) return "http:" + (int)status;
        if (error is SocketException or IOException or HttpRequestException) return "network";
        return "unknown";
    }
}
