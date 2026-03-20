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
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using WindowManager.App.Runtime;

namespace WindowManager.App.Runtime.Publishing;

public sealed class RokuDevDeploymentService
{
    private static readonly HttpClient ManifestClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

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

    public Task<string> DeployNowAsync(RegisteredDisplaySnapshot display, string expectedVersion)
    {
        if (display is null || string.IsNullOrWhiteSpace(expectedVersion))
        {
            return Task.FromResult("parametros_invalidos");
        }

        return DeployAsync(display, expectedVersion);
    }

    private async Task<string> DeployAsync(RegisteredDisplaySnapshot display, string expectedVersion)
    {
        try
        {
            var package = await ResolvePackageAsync(expectedVersion).ConfigureAwait(false);
            var configPath = Path.Combine(monorepoRoot, "super", "roku-devices.json");
            var config = File.Exists(configPath) ? LoadConfig(configPath) : new RokuDevDeploymentConfig();
            var deviceConfig = FindDeviceConfig(config, display) ?? BuildDefaultDeviceConfig(display);
            if (!deviceConfig.Enabled)
            {
                return "tv_sem_credenciais_ou_desabilitada";
            }

            var host = ResolveHost(display, deviceConfig);
            if (string.IsNullOrWhiteSpace(host))
            {
                return "ip_da_tv_ausente";
            }

            using (var handler = new HttpClientHandler())
            using (var client = new HttpClient(handler))
            using (var form = new MultipartFormDataContent())
            using (var fileStream = File.OpenRead(package.PackagePath))
            using (var fileContent = new StreamContent(fileStream))
            {
                var username = string.IsNullOrWhiteSpace(deviceConfig.Username) ? "rokudev" : deviceConfig.Username;
                var password = string.IsNullOrWhiteSpace(deviceConfig.Password) ? "1234" : deviceConfig.Password;

                handler.Credentials = new NetworkCredential(username, password);
                handler.PreAuthenticate = true;
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue(
                        "Basic",
                        Convert.ToBase64String(Encoding.ASCII.GetBytes(username + ":" + password)));

                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(new StringContent("Install"), "mysubmit");
                form.Add(new StringContent(password), "passwd");
                form.Add(fileContent, "archive", Path.GetFileName(package.PackagePath));

                AppLog.Write(
                    "RokuDeploy",
                    string.Format(
                        "Iniciando sideload para TV id={0}, host={1}, usuario={2}, pacote={3}, origem={4}",
                        display.DeviceId,
                        host,
                        username,
                        package.PackagePath,
                        package.Source));

                var pluginInstallUri = new Uri(string.Format("http://{0}/plugin_install", host));
                var response = await client.PostAsync(pluginInstallUri, form).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Write(
                        "RokuDeploy",
                        string.Format(
                            "Falha no upload do pacote Roku: host={0}, status={1}",
                            host,
                            (int)response.StatusCode));
                    return "upload_falhou_" + (int)response.StatusCode;
                }

                try
                {
                    var launchUri = new Uri(string.Format("http://{0}:8060/launch/dev", host));
                    var launchResponse = await client.PostAsync(launchUri, new StringContent(string.Empty)).ConfigureAwait(false);
                    AppLog.Write(
                        "RokuDeploy",
                        string.Format(
                            "Canal Roku relancado apos sideload: host={0}, status={1}",
                            host,
                            (int)launchResponse.StatusCode));
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

    private static string monorepoRoot => FindMonorepoRoot();

    private async Task<RokuResolvedPackage> ResolvePackageAsync(string expectedVersion)
    {
        try
        {
            return await DownloadLatestRokuPackageAsync(expectedVersion).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var fallback = ResolveLocalPackagePath();
            if (string.IsNullOrWhiteSpace(fallback) || !File.Exists(fallback))
            {
                AppLog.Write(
                    "RokuDeploy",
                    string.Format(
                        "Falha ao baixar pacote Roku remoto e nao ha fallback local. erro={0}",
                        ex.Message));
                throw;
            }

            AppLog.Write(
                "RokuDeploy",
                string.Format(
                    "Falha ao baixar pacote Roku remoto; usando fallback local. erro={0}, arquivo={1}",
                    ex.Message,
                    fallback));

            return new RokuResolvedPackage
            {
                PackagePath = fallback,
                Source = "local"
            };
        }
    }

    private async Task<RokuResolvedPackage> DownloadLatestRokuPackageAsync(string expectedVersion)
    {
        var manifestUrl = BuildLatestRokuManifestUrl();
        using (var response = await ManifestClient.GetAsync(manifestUrl).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var body = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim().TrimStart('\uFEFF');
            var manifest = JsonConvert.DeserializeObject<RokuLatestManifest>(body);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.CurrentRelease))
            {
                throw new InvalidOperationException("manifesto_roku_invalido");
            }

            var release = manifest.Releases?.FirstOrDefault(x => string.Equals(x.ReleaseId, manifest.CurrentRelease, StringComparison.OrdinalIgnoreCase));
            var packageUrl = release?.FullPackageUrl;
            if (string.IsNullOrWhiteSpace(packageUrl))
            {
                throw new InvalidOperationException("pacote_roku_remoto_ausente");
            }

            var tempRoot = Path.Combine(Path.GetTempPath(), "WindowManagerBroadcast", "roku-sideload", manifest.CurrentRelease);
            Directory.CreateDirectory(tempRoot);

            var packagePath = Path.Combine(tempRoot, Path.GetFileName(new Uri(packageUrl).AbsolutePath));
            if (!File.Exists(packagePath))
            {
                using (var packageResponse = await ManifestClient.GetAsync(packageUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    packageResponse.EnsureSuccessStatusCode();
                    using (var sourceStream = await packageResponse.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var destinationStream = File.Create(packagePath))
                    {
                        await sourceStream.CopyToAsync(destinationStream).ConfigureAwait(false);
                    }
                }
            }

            AppLog.Write(
                "RokuDeploy",
                string.Format(
                    "Pacote Roku baixado para sideload: release={0}, esperado={1}, arquivo={2}",
                    manifest.CurrentRelease,
                    expectedVersion,
                    packagePath));

            return new RokuResolvedPackage
            {
                PackagePath = packagePath,
                Source = "remote"
            };
        }
    }

    private static string ResolveLocalPackagePath()
    {
        var root = monorepoRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            return string.Empty;
        }

        return Path.Combine(root, "hello-roku.zip");
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

    private static RokuDevDeviceConfig BuildDefaultDeviceConfig(RegisteredDisplaySnapshot display)
    {
        return new RokuDevDeviceConfig
        {
            DeviceId = display.DeviceId,
            Host = display.NetworkAddress,
            Username = "rokudev",
            Password = "1234",
            Enabled = true
        };
    }

    private static string ResolveHost(RegisteredDisplaySnapshot display, RokuDevDeviceConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Host))
        {
            return config.Host;
        }

        return display.NetworkAddress;
    }

    private static string BuildLatestRokuManifestUrl()
    {
        var latestSuperManifest = BuildVersionInfo.LatestManifestUrl ?? string.Empty;
        if (latestSuperManifest.IndexOf("latest-super.json", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return latestSuperManifest.Replace("latest-super.json", "latest-rokuweb.json");
        }

        return "https://fbinerd.github.io/rokuweb/updates/latest-rokuweb.json";
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

public sealed class RokuLatestManifest
{
    public string CurrentRelease { get; set; } = string.Empty;
    public RokuReleaseEntry[] Releases { get; set; } = Array.Empty<RokuReleaseEntry>();
}

public sealed class RokuReleaseEntry
{
    public string ReleaseId { get; set; } = string.Empty;
    public string FullPackageUrl { get; set; } = string.Empty;
}

public sealed class RokuResolvedPackage
{
    public string PackagePath { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}
