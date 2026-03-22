using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

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

    public void OnBrowserAudioStreamStarted(Guid windowId, int sampleRate, int channels)
    {
        if (!_transports.TryGetValue(windowId, out var reserved))
        {
            return;
        }

        reserved.OnAudioStreamStarted(sampleRate, channels);
    }

    public void OnBrowserAudioPacketCaptured(Guid windowId, byte[] pcmBytes)
    {
        if (pcmBytes.Length == 0 || !_transports.TryGetValue(windowId, out var reserved))
        {
            return;
        }

        reserved.SendAudioPacket(pcmBytes);
    }

    private static ReservedRealtimeTransport CreateReservedTransport(Guid windowId, string publicHost)
    {
        var audioSocket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var videoSocket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var audioPort = ((IPEndPoint)audioSocket.Client.LocalEndPoint).Port;
        var videoPort = ((IPEndPoint)videoSocket.Client.LocalEndPoint).Port;
        var candidate = new RealtimeTransportCandidate
        {
            WindowId = windowId.ToString("N"),
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
        private readonly UdpClient _audioSender;
        private readonly Task _audioLoop;
        private readonly Task _videoLoop;
        private bool _audioStreamAnnounced;
        private bool _audioPacketMirroringAnnounced;

        public ReservedRealtimeTransport(RealtimeTransportCandidate candidate, UdpClient audioSocket, UdpClient videoSocket)
        {
            Candidate = candidate;
            _audioSocket = audioSocket;
            _videoSocket = videoSocket;
            _audioSender = new UdpClient(AddressFamily.InterNetwork);
            _audioLoop = Task.Run(() => ReceiveLoopAsync(_audioSocket, true));
            _videoLoop = Task.Run(() => ReceiveLoopAsync(_videoSocket, false));
        }

        public RealtimeTransportCandidate Candidate { get; }

        public void OnAudioStreamStarted(int sampleRate, int channels)
        {
            Candidate.AudioSampleRate = sampleRate;
            Candidate.AudioChannels = channels;

            if (_audioStreamAnnounced)
            {
                return;
            }

            _audioStreamAnnounced = true;
            AppLog.Write(
                "ExpWebRtc",
                string.Format(
                    "Fluxo de audio continuo experimental conectado: sampleRate={0}, channels={1}, port={2}",
                    sampleRate,
                    channels,
                    Candidate.AudioPort));
        }

        public void SendAudioPacket(byte[] pcmBytes)
        {
            if (pcmBytes.Length == 0)
            {
                return;
            }

            try
            {
                var header = Encoding.ASCII.GetBytes("SPAUD1");
                var payload = new byte[header.Length + pcmBytes.Length];
                Buffer.BlockCopy(header, 0, payload, 0, header.Length);
                Buffer.BlockCopy(pcmBytes, 0, payload, header.Length, pcmBytes.Length);
                _audioSender.Send(payload, payload.Length, IPAddress.Loopback.ToString(), Candidate.AudioPort);
                Candidate.AudioPacketsSent += 1;
                Candidate.AudioBytesSent += payload.Length;
                if (Candidate.AudioPacketsSent == 1 || Candidate.AudioPacketsSent % 200 == 0)
                {
                    AppLog.Write(
                        "ExpWebRtc",
                        string.Format(
                            "UDP audio experimental espelhado: janela={0}, packetsSent={1}, packetsReceived={2}, bytesSent={3}, bytesReceived={4}",
                            Candidate.WindowId,
                            Candidate.AudioPacketsSent,
                            Candidate.AudioPacketsReceived,
                            Candidate.AudioBytesSent,
                            Candidate.AudioBytesReceived));
                }

                if (_audioPacketMirroringAnnounced)
                {
                    return;
                }

                _audioPacketMirroringAnnounced = true;
                AppLog.Write(
                    "ExpWebRtc",
                    string.Format(
                        "Espelhamento de audio continuo experimental ativo: janela={0}, audioPort={1}",
                        Candidate.WindowId,
                        Candidate.AudioPort));
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }

        private async Task ReceiveLoopAsync(UdpClient socket, bool audio)
        {
            while (true)
            {
                try
                {
                    var result = await socket.ReceiveAsync().ConfigureAwait(false);
                    Candidate.Ready = true;
                    Candidate.LastPacketUtc = DateTime.UtcNow.ToString("O");

                    if (audio)
                    {
                        Candidate.AudioPacketsReceived += 1;
                        Candidate.AudioBytesReceived += result.Buffer?.Length ?? 0;
                    }
                    else
                    {
                        Candidate.VideoPacketsReceived += 1;
                        Candidate.VideoBytesReceived += result.Buffer?.Length ?? 0;
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }
                catch
                {
                    return;
                }
            }
        }

        public void Dispose()
        {
            _audioSender.Dispose();
            _audioSocket.Dispose();
            _videoSocket.Dispose();
        }
    }
}

public sealed class RealtimeTransportCandidate
{
    public string WindowId { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public string Protocol { get; set; } = "udp";

    public int AudioPort { get; set; }

    public int VideoPort { get; set; }

    public string Mode { get; set; } = "continuous-udp-prototype";

    public bool Ready { get; set; }

    public long AudioPacketsReceived { get; set; }

    public long AudioBytesReceived { get; set; }

    public long VideoPacketsReceived { get; set; }

    public long VideoBytesReceived { get; set; }

    public string LastPacketUtc { get; set; } = string.Empty;

    public int AudioSampleRate { get; set; }

    public int AudioChannels { get; set; }

    public long AudioPacketsSent { get; set; }

    public long AudioBytesSent { get; set; }
}
