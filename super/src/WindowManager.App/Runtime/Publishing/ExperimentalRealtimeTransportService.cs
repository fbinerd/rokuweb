using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace WindowManager.App.Runtime.Publishing;

public sealed class ExperimentalRealtimeTransportService : IDisposable
{
    private readonly bool _enabled;
    private readonly ConcurrentDictionary<Guid, ReservedRealtimeTransport> _transports = new ConcurrentDictionary<Guid, ReservedRealtimeTransport>();

    public ExperimentalRealtimeTransportService()
    {
        _enabled = string.Equals(Environment.GetEnvironmentVariable("SUPERPAINEL_EXPERIMENT_WEBRTC_AV"), "1", StringComparison.OrdinalIgnoreCase);
        if (_enabled)
        {
            AppLog.Write("ExpWebRtc", "Fundacao de transporte continuo/UDP experimental habilitada.");
        }
    }

    public bool IsEnabled => _enabled;

    public RealtimeTransportCandidate? GetOrCreate(Guid windowId, string publicHost)
    {
        if (!_enabled)
        {
            return null;
        }

        var reserved = _transports.GetOrAdd(windowId, _ => CreateReservedTransport(windowId, publicHost));
        return reserved.Candidate;
    }

    public void Invalidate(Guid windowId)
    {
        if (_transports.TryRemove(windowId, out var reserved))
        {
            reserved.Dispose();
            AppLog.Write("ExpWebRtc", $"Candidatos UDP experimentais liberados: janela={windowId:N}");
        }
    }

    public void Dispose()
    {
        foreach (var pair in _transports)
        {
            pair.Value.Dispose();
        }

        _transports.Clear();
    }

    private static ReservedRealtimeTransport CreateReservedTransport(Guid windowId, string publicHost)
    {
        var audioSocket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var videoSocket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var audioPort = ((IPEndPoint)audioSocket.Client.LocalEndPoint).Port;
        var videoPort = ((IPEndPoint)videoSocket.Client.LocalEndPoint).Port;
        var candidate = new RealtimeTransportCandidate
        {
            Host = publicHost,
            Protocol = "udp",
            AudioPort = audioPort,
            VideoPort = videoPort,
            Mode = "continuous-udp-prototype",
            Ready = false
        };

        AppLog.Write(
            "ExpWebRtc",
            string.Format(
                "Candidatos UDP experimentais reservados: janela={0}, host={1}, audioPort={2}, videoPort={3}",
                windowId.ToString("N"),
                publicHost,
                audioPort,
                videoPort));

        return new ReservedRealtimeTransport(candidate, audioSocket, videoSocket);
    }

    private sealed class ReservedRealtimeTransport : IDisposable
    {
        private readonly UdpClient _audioSocket;
        private readonly UdpClient _videoSocket;

        public ReservedRealtimeTransport(RealtimeTransportCandidate candidate, UdpClient audioSocket, UdpClient videoSocket)
        {
            Candidate = candidate;
            _audioSocket = audioSocket;
            _videoSocket = videoSocket;
        }

        public RealtimeTransportCandidate Candidate { get; }

        public void Dispose()
        {
            _audioSocket.Dispose();
            _videoSocket.Dispose();
        }
    }
}

public sealed class RealtimeTransportCandidate
{
    public string Host { get; set; } = string.Empty;

    public string Protocol { get; set; } = "udp";

    public int AudioPort { get; set; }

    public int VideoPort { get; set; }

    public string Mode { get; set; } = "continuous-udp-prototype";

    public bool Ready { get; set; }
}
