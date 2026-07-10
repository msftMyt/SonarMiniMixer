using System.Net;

namespace SonarMiniMixer.Core;

public static class EndpointSecurity
{
    public static Uri CreateLoopbackBaseUri(string? address, string defaultScheme)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidDataException("SteelSeries returned an empty service address.");

        var candidate = address.Contains("://", StringComparison.Ordinal)
            ? address
            : $"{defaultScheme}://{address}";

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !IsLoopback(uri))
            throw new InvalidDataException("SteelSeries returned an unsafe service address.");

        return new UriBuilder(uri)
        {
            Path = uri.AbsolutePath.TrimEnd('/') + "/",
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri;
    }

    public static bool IsLoopback(Uri uri)
    {
        if (uri.IsLoopback) return true;
        return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }
}
