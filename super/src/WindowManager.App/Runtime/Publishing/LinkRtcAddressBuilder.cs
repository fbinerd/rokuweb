using System;
using System.Net;
using WindowManager.Core.Models;

namespace WindowManager.App.Runtime.Publishing;

public static class LinkRtcAddressBuilder
{
    public static string BuildPublishedUrl(WindowSession session, int serverPort, WebRtcBindMode bindMode, string specificIp)
    {
        var port = serverPort <= 0 ? 8088 : serverPort;
        var slug = NormalizeSlug(session.Title, session.Id.ToString("N"));
        var publicHost = ResolvePublicHost(bindMode, specificIp);
        return string.Format("http://{0}:{1}/{2}", publicHost, port, slug);
    }

    public static string BuildLoopbackUrl(WindowSession session, int serverPort)
    {
        var port = serverPort <= 0 ? 8088 : serverPort;
        var slug = NormalizeSlug(session.Title, session.Id.ToString("N"));
        return string.Format("http://localhost:{0}/{1}", port, slug);
    }
    public static IPEndPoint ResolveListenerEndpoint(WebRtcBindMode bindMode, string specificIp, int port)
    {
        switch (bindMode)
        {
            case WebRtcBindMode.Lan:
                return new IPEndPoint(IPAddress.Any, port);
            case WebRtcBindMode.SpecificIp:
                return new IPEndPoint(ResolveSpecificIpOrLoopback(specificIp), port);
            default:
                return new IPEndPoint(IPAddress.Loopback, port);
        }
    }

    public static string NormalizeRouteSegment(string value, string fallback)
    {
        return NormalizeSlug(value, fallback);
    }

    public static string ResolvePublicHost(WebRtcBindMode bindMode, string specificIp)
    {
        switch (bindMode)
        {
            case WebRtcBindMode.Lan:
                return GetPreferredLanAddress() ?? Environment.MachineName;
            case WebRtcBindMode.SpecificIp:
                return ResolveSpecificIpOrLoopback(specificIp).ToString();
            default:
                return "localhost";
        }
    }

    private static IPAddress ResolveSpecificIpOrLoopback(string specificIp)
    {
        var candidate = specificIp?.Trim() ?? string.Empty;
        return IPAddress.TryParse(candidate, out var address) ? address : IPAddress.Loopback;
    }

    private static string? GetPreferredLanAddress()
    {
        try
        {
            var addresses = Dns.GetHostAddresses(Dns.GetHostName());
            foreach (var address in addresses)
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                {
                    return address.ToString();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string NormalizeSlug(string input, string fallback)
    {
        var baseValue = string.IsNullOrWhiteSpace(input) ? fallback : input;
        var characters = baseValue.Trim().ToLowerInvariant();
        var builder = new System.Text.StringBuilder();

        foreach (var character in characters)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                continue;
            }

            if (character == '-' || character == '_')
            {
                builder.Append(character);
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? fallback : slug;
    }
}

