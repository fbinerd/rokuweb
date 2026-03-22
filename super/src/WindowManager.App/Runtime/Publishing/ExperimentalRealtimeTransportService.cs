using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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

    public void ConfigureRemoteAudioTarget(Guid windowId, string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0 || !_transports.TryGetValue(windowId, out var reserved))
        {
            return;
        }

        reserved.ConfigureRemoteAudioTarget(host, port);
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
            Protocol = "udp-rtp",
            AudioPort = audioPort,
            VideoPort = videoPort,
            Mode = "continuous-udp-rtp-prototype",
            AudioCodec = "L16",
            AudioPayloadType = 97,
            AudioSsrc = Environment.TickCount ^ audioPort ^ videoPort,
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
        private string _audioTargetHost;
        private int _audioTargetPort;
        private readonly Task _audioLoop;
        private readonly Task _videoLoop;
        private bool _audioStreamAnnounced;
        private bool _audioPacketMirroringAnnounced;
        private ushort _audioSequenceNumber;
        private uint _audioTimestamp;

        public ReservedRealtimeTransport(RealtimeTransportCandidate candidate, UdpClient audioSocket, UdpClient videoSocket)
        {
            Candidate = candidate;
            _audioSocket = audioSocket;
            _videoSocket = videoSocket;
            _audioSender = new UdpClient(AddressFamily.InterNetwork);
            _audioTargetHost = IPAddress.Loopback.ToString();
            _audioTargetPort = candidate.AudioPort;
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
                    "Fluxo de audio continuo experimental conectado: sampleRate={0}, channels={1}, port={2}, codec={3}, payloadType={4}",
                    sampleRate,
                    channels,
                    Candidate.AudioPort,
                    Candidate.AudioCodec,
                    Candidate.AudioPayloadType));
        }

        public void SendAudioPacket(byte[] pcmBytes)
        {
            if (pcmBytes.Length == 0)
            {
                return;
            }

            try
            {
                var packet = BuildAudioRtpPacket(pcmBytes);
                _audioSender.Send(packet, packet.Length, _audioTargetHost, _audioTargetPort);
                Candidate.AudioPacketsSent += 1;
                Candidate.AudioBytesSent += packet.Length;
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
                        _audioTargetPort));
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }

        public void ConfigureRemoteAudioTarget(string host, int port)
        {
            _audioTargetHost = host;
            _audioTargetPort = port;
            Candidate.Host = host;
            Candidate.AudioPort = port;
            Candidate.Protocol = "udp-rtp";
            AppLog.Write(
                "ExpWebRtc",
                string.Format(
                    "Destino RTP/UDP experimental configurado: janela={0}, host={1}, audioPort={2}",
                    Candidate.WindowId,
                    host,
                    port));
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

        private byte[] BuildAudioRtpPacket(byte[] pcmBytes)
        {
            var packet = new byte[12 + pcmBytes.Length];
            packet[0] = 0x80;
            packet[1] = (byte)(Candidate.AudioPayloadType & 0x7F);
            packet[2] = (byte)((_audioSequenceNumber >> 8) & 0xFF);
            packet[3] = (byte)(_audioSequenceNumber & 0xFF);
            packet[4] = (byte)((_audioTimestamp >> 24) & 0xFF);
            packet[5] = (byte)((_audioTimestamp >> 16) & 0xFF);
            packet[6] = (byte)((_audioTimestamp >> 8) & 0xFF);
            packet[7] = (byte)(_audioTimestamp & 0xFF);
            packet[8] = (byte)((Candidate.AudioSsrc >> 24) & 0xFF);
            packet[9] = (byte)((Candidate.AudioSsrc >> 16) & 0xFF);
            packet[10] = (byte)((Candidate.AudioSsrc >> 8) & 0xFF);
            packet[11] = (byte)(Candidate.AudioSsrc & 0xFF);
            Buffer.BlockCopy(pcmBytes, 0, packet, 12, pcmBytes.Length);

            _audioSequenceNumber++;
            var bytesPerFrame = Math.Max(2, Candidate.AudioChannels * 2);
            var frameCount = pcmBytes.Length / bytesPerFrame;
            _audioTimestamp += (uint)Math.Max(1, frameCount);
            return packet;
        }
    }
}

public sealed class RealtimeTransportCandidate
{
    public string WindowId { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public string Protocol { get; set; } = "udp-rtp";

    public int AudioPort { get; set; }

    public int VideoPort { get; set; }

    public string Mode { get; set; } = "continuous-udp-rtp-prototype";

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

    public string AudioCodec { get; set; } = "L16";

    public int AudioPayloadType { get; set; } = 97;

    public int AudioSsrc { get; set; }
}
