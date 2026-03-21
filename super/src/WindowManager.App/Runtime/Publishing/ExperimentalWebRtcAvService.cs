using System;
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

    public string BuildSessionJson(WindowSessionSessionInfo info)
    {
        using var stream = new MemoryStream();
        var serializer = new DataContractJsonSerializer(typeof(WindowSessionSessionInfo));
        serializer.WriteObject(stream, info);
        return Encoding.UTF8.GetString(stream.ToArray());
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

    [DataMember(Name = "videoCodec", Order = 7)]
    public string VideoCodec { get; set; } = "H264";

    [DataMember(Name = "audioCodec", Order = 8)]
    public string AudioCodec { get; set; } = "Opus";

    [DataMember(Name = "notes", Order = 9)]
    public List<string> Notes { get; set; } = new List<string>();
}
