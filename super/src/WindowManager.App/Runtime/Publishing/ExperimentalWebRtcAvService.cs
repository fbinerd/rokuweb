using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace WindowManager.App.Runtime.Publishing;

public sealed class ExperimentalWebRtcAvService
{
    private readonly bool _enabled;
    private readonly string _rootDirectory;
    private readonly ConcurrentDictionary<Guid, ExperimentalWebRtcSessionState> _sessions = new ConcurrentDictionary<Guid, ExperimentalWebRtcSessionState>();

    public ExperimentalWebRtcAvService()
    {
        _enabled = string.Equals(Environment.GetEnvironmentVariable("SUPERPAINEL_EXPERIMENT_WEBRTC_AV"), "1", StringComparison.OrdinalIgnoreCase);
        _rootDirectory = Path.Combine(AppDataPaths.Root, "experimental-webrtc-av");
        Directory.CreateDirectory(_rootDirectory);

        if (_enabled)
        {
            AppLog.Write("ExpWebRtc", "Modo experimental WebRTC A/V habilitado.");
        }
    }

    public bool IsEnabled => _enabled;

    public string BuildSessionUrl(Guid windowId, string publicHost, int port)
    {
        return $"http://{publicHost}:{port}/api/experimental-av/{windowId:N}";
    }

    public string BuildOfferUrl(Guid windowId, string publicHost, int port)
    {
        return $"http://{publicHost}:{port}/api/experimental-av/{windowId:N}/offer";
    }

    public string BuildStateUrl(Guid windowId, string publicHost, int port)
    {
        return $"http://{publicHost}:{port}/api/experimental-av/{windowId:N}/state";
    }

    public string BuildSessionJson(WindowSessionSessionInfo info)
    {
        using var stream = new MemoryStream();
        var serializer = new DataContractJsonSerializer(typeof(WindowSessionSessionInfo));
        serializer.WriteObject(stream, info);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string BuildStateJson(ExperimentalWebRtcSessionState state)
    {
        using var stream = new MemoryStream();
        var serializer = new DataContractJsonSerializer(typeof(ExperimentalWebRtcSessionState));
        serializer.WriteObject(stream, state);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public ExperimentalWebRtcSessionState GetOrCreateSession(Guid windowId, string title, string initialUrl, string signalingUrl)
    {
        var state = _sessions.GetOrAdd(windowId, _ => new ExperimentalWebRtcSessionState
        {
            WindowId = windowId.ToString("N")
        });

        state.Title = title;
        state.InitialUrl = initialUrl;
        state.SignalingUrl = signalingUrl;
        state.LastTouchedUtc = DateTime.UtcNow.ToString("O");
        return state;
    }

    public ExperimentalWebRtcSessionState RegisterOffer(Guid windowId, string title, string initialUrl, string signalingUrl, ExperimentalWebRtcOfferPayload payload)
    {
        var state = GetOrCreateSession(windowId, title, initialUrl, signalingUrl);
        state.LastOfferType = payload.Type ?? string.Empty;
        state.LastOfferSdp = payload.Sdp ?? string.Empty;
        state.LastOfferSource = payload.Source ?? string.Empty;
        state.OfferCount += 1;
        state.Status = "answer-ready";
        state.AnswerType = "answer";
        state.AnswerSdp = BuildPlaceholderAnswerSdp(windowId);
        state.MediaTransportImplemented = false;
        state.MediaReady = false;
        state.LastTouchedUtc = DateTime.UtcNow.ToString("O");
        state.Notes = new List<string>
        {
            "Offer recebida e persistida no super.",
            "Resposta SDP placeholder gerada para a sessao experimental.",
            "Ainda nao existe peer connection WebRTC real; esta branch prepara a trilha de sinalizacao."
        };
        return state;
    }

    public ExperimentalWebRtcSessionState? TryGetSession(Guid windowId)
    {
        _sessions.TryGetValue(windowId, out var state);
        return state;
    }

    private static string BuildPlaceholderAnswerSdp(Guid windowId)
    {
        return
            "v=0\r\n" +
            "o=superpainel 0 0 IN IP4 127.0.0.1\r\n" +
            "s=SuperPainel Experimental AV\r\n" +
            "t=0 0\r\n" +
            "a=msid-semantic: WMS " + windowId.ToString("N") + "\r\n" +
            "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\n" +
            "c=IN IP4 0.0.0.0\r\n" +
            "a=mid:0\r\n" +
            "a=inactive\r\n" +
            "a=rtpmap:111 opus/48000/2\r\n" +
            "m=video 9 UDP/TLS/RTP/SAVPF 102\r\n" +
            "c=IN IP4 0.0.0.0\r\n" +
            "a=mid:1\r\n" +
            "a=inactive\r\n" +
            "a=rtpmap:102 H264/90000\r\n";
    }
}

[DataContract]
public sealed class WindowSessionSessionInfo
{
    [DataMember(Name = "kind", Order = 1)]
    public string Kind { get; set; } = "experimental-webrtc-av";

    [DataMember(Name = "status", Order = 2)]
    public string Status { get; set; } = "foundation-only";

    [DataMember(Name = "windowId", Order = 3)]
    public string WindowId { get; set; } = string.Empty;

    [DataMember(Name = "title", Order = 4)]
    public string Title { get; set; } = string.Empty;

    [DataMember(Name = "initialUrl", Order = 5)]
    public string InitialUrl { get; set; } = string.Empty;

    [DataMember(Name = "signalingUrl", Order = 6)]
    public string SignalingUrl { get; set; } = string.Empty;

    [DataMember(Name = "offerUrl", Order = 7)]
    public string OfferUrl { get; set; } = string.Empty;

    [DataMember(Name = "stateUrl", Order = 8)]
    public string StateUrl { get; set; } = string.Empty;

    [DataMember(Name = "videoCodec", Order = 9)]
    public string VideoCodec { get; set; } = "H264";

    [DataMember(Name = "audioCodec", Order = 10)]
    public string AudioCodec { get; set; } = "Opus";

    [DataMember(Name = "supportedTransports", Order = 11)]
    public List<string> SupportedTransports { get; set; } = new List<string>();

    [DataMember(Name = "mediaTransportImplemented", Order = 12)]
    public bool MediaTransportImplemented { get; set; }

    [DataMember(Name = "notes", Order = 13)]
    public List<string> Notes { get; set; } = new List<string>();
}

[DataContract]
public sealed class ExperimentalWebRtcOfferPayload
{
    [DataMember(Name = "type", Order = 1)]
    public string Type { get; set; } = string.Empty;

    [DataMember(Name = "sdp", Order = 2)]
    public string Sdp { get; set; } = string.Empty;

    [DataMember(Name = "source", Order = 3)]
    public string Source { get; set; } = string.Empty;
}

[DataContract]
public sealed class ExperimentalWebRtcSessionState
{
    [DataMember(Name = "windowId", Order = 1)]
    public string WindowId { get; set; } = string.Empty;

    [DataMember(Name = "title", Order = 2)]
    public string Title { get; set; } = string.Empty;

    [DataMember(Name = "initialUrl", Order = 3)]
    public string InitialUrl { get; set; } = string.Empty;

    [DataMember(Name = "signalingUrl", Order = 4)]
    public string SignalingUrl { get; set; } = string.Empty;

    [DataMember(Name = "status", Order = 5)]
    public string Status { get; set; } = "idle";

    [DataMember(Name = "offerCount", Order = 6)]
    public int OfferCount { get; set; }

    [DataMember(Name = "lastOfferType", Order = 7)]
    public string LastOfferType { get; set; } = string.Empty;

    [DataMember(Name = "lastOfferSource", Order = 8)]
    public string LastOfferSource { get; set; } = string.Empty;

    [DataMember(Name = "lastOfferSdp", Order = 9)]
    public string LastOfferSdp { get; set; } = string.Empty;

    [DataMember(Name = "lastTouchedUtc", Order = 10)]
    public string LastTouchedUtc { get; set; } = string.Empty;

    [DataMember(Name = "answerType", Order = 11)]
    public string AnswerType { get; set; } = "answer";

    [DataMember(Name = "answerSdp", Order = 12)]
    public string AnswerSdp { get; set; } = string.Empty;

    [DataMember(Name = "mediaTransportImplemented", Order = 13)]
    public bool MediaTransportImplemented { get; set; }

    [DataMember(Name = "mediaReady", Order = 14)]
    public bool MediaReady { get; set; }

    [DataMember(Name = "notes", Order = 15)]
    public List<string> Notes { get; set; } = new List<string>();
}

[DataContract]
public sealed class ExperimentalWebRtcOfferAccepted
{
    [DataMember(Name = "ok", Order = 1)]
    public bool Ok { get; set; }

    [DataMember(Name = "status", Order = 2)]
    public string Status { get; set; } = string.Empty;

    [DataMember(Name = "windowId", Order = 3)]
    public string WindowId { get; set; } = string.Empty;

    [DataMember(Name = "offerCount", Order = 4)]
    public int OfferCount { get; set; }

    [DataMember(Name = "stateUrl", Order = 5)]
    public string StateUrl { get; set; } = string.Empty;

    [DataMember(Name = "answerType", Order = 6)]
    public string AnswerType { get; set; } = string.Empty;

    [DataMember(Name = "answerSdp", Order = 7)]
    public string AnswerSdp { get; set; } = string.Empty;

    [DataMember(Name = "notes", Order = 8)]
    public List<string> Notes { get; set; } = new List<string>();
}
