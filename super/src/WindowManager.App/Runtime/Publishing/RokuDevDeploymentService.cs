using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;
using WindowManager.App.Runtime;

namespace WindowManager.App.Runtime.Publishing;

public sealed class RokuDevDeploymentService
{
    private readonly ConcurrentDictionary<string, string> _scheduledVersions = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public void TryScheduleUpdate(RegisteredDisplaySnapshot display, string expectedVersion)
    {
        if (display is null || string.IsNullOrWhiteSpace(display.DeviceId) || string.IsNullOrWhiteSpace(expectedVersion))
        {
            return;
        }

        if (string.Equals(display.ChannelVersion, expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var scheduleKey = display.DeviceId;
        if (_scheduledVersions.TryGetValue(scheduleKey, out var previousVersion) &&
            string.Equals(previousVersion, expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _scheduledVersions[scheduleKey] = expectedVersion;

        _ = Task.Run(async () =>
        {
            var result = await DeployAsync(display, expectedVersion).ConfigureAwait(false);
            AppLog.Write(
                "RokuDeploy",
                string.Format(
                    "Deploy automatico para TV id={0}, alvo={1}, resultado={2}",
                    display.DeviceId,
                    expectedVersion,
                    result));
        });
    }

    private async Task<string> DeployAsync(RegisteredDisplaySnapshot display, string expectedVersion)
    {
        try
        {
            var monorepoRoot = FindMonorepoRoot();
            if (string.IsNullOrWhiteSpace(monorepoRoot))
            {
                return "monorepo_nao_encontrado";
            }

            var packagePath = Path.Combine(monorepoRoot, "hello-roku.zip");
            if (!File.Exists(packagePath))
            {
                return "pacote_roku_nao_encontrado";
            }

            var configPath = Path.Combine(monorepoRoot, "super", "roku-devices.json");
            if (!File.Exists(configPath))
            {
                return "config_roku_dev_inexistente";
            }

            var config = LoadConfig(configPath);
            var deviceConfig = FindDeviceConfig(config, display);
            if (deviceConfig is null || !deviceConfig.Enabled)
            {
                return "tv_sem_credenciais_ou_desabilitada";
            }

            using (var handler = new HttpClientHandler())
            using (var client = new HttpClient(handler))
            using (var form = new MultipartFormDataContent())
            using (var fileStream = File.OpenRead(packagePath))
            using (var fileContent = new StreamContent(fileStream))
            {
                handler.Credentials = new NetworkCredential(
                    string.IsNullOrWhiteSpace(deviceConfig.Username) ? "rokudev" : deviceConfig.Username,
                    deviceConfig.Password ?? string.Empty);
                handler.PreAuthenticate = true;

                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(new StringContent("Install"), "mysubmit");
                form.Add(new StringContent(deviceConfig.Password ?? string.Empty), "passwd");
                form.Add(fileContent, "archive", "hello-roku.zip");

                var pluginInstallUri = new Uri(string.Format("http://{0}/plugin_install", ResolveHost(display, deviceConfig)));
                var response = await client.PostAsync(pluginInstallUri, form).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return "upload_falhou_" + (int)response.StatusCode;
                }

                try
                {
                    var launchUri = new Uri(string.Format("http://{0}:8060/launch/dev", ResolveHost(display, deviceConfig)));
                    await client.PostAsync(launchUri, new StringContent(string.Empty)).ConfigureAwait(false);
                }
                catch
                {
                }

                return "ok";
            }
        }
        catch (Exception ex)
        {
            return "erro_" + ex.GetType().Name;
        }
    }

    private static RokuDevDeploymentConfig LoadConfig(string configPath)
    {
        using (var stream = File.OpenRead(configPath))
        {
            var serializer = new DataContractJsonSerializer(typeof(RokuDevDeploymentConfig));
            return serializer.ReadObject(stream) as RokuDevDeploymentConfig ?? new RokuDevDeploymentConfig();
        }
    }

    private static RokuDevDeviceConfig? FindDeviceConfig(RokuDevDeploymentConfig config, RegisteredDisplaySnapshot display)
    {
        return config.Devices.FirstOrDefault(device =>
            (!string.IsNullOrWhiteSpace(device.DeviceId) && string.Equals(device.DeviceId, display.DeviceId, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(device.Host) && string.Equals(device.Host, display.NetworkAddress, StringComparison.OrdinalIgnoreCase)));
    }

    private static string ResolveHost(RegisteredDisplaySnapshot display, RokuDevDeviceConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Host))
        {
            return config.Host;
        }

        return display.NetworkAddress;
    }

    private static string FindMonorepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var manifestPath = Path.Combine(current.FullName, "manifest");
            var superPath = Path.Combine(current.FullName, "super");
            if (File.Exists(manifestPath) && Directory.Exists(superPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return string.Empty;
    }
}

[DataContract]
public sealed class RokuDevDeploymentConfig
{
    [DataMember(Name = "devices", Order = 1)]
    public List<RokuDevDeviceConfig> Devices { get; set; } = new List<RokuDevDeviceConfig>();
}

[DataContract]
public sealed class RokuDevDeviceConfig
{
    [DataMember(Name = "deviceId", Order = 1)]
    public string DeviceId { get; set; } = string.Empty;

    [DataMember(Name = "host", Order = 2)]
    public string Host { get; set; } = string.Empty;

    [DataMember(Name = "username", Order = 3)]
    public string Username { get; set; } = "rokudev";

    [DataMember(Name = "password", Order = 4)]
    public string Password { get; set; } = string.Empty;

    [DataMember(Name = "enabled", Order = 5)]
    public bool Enabled { get; set; } = true;
}
