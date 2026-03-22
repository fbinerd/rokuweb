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
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.IO.Compression;
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
    private readonly AppUpdatePreferenceStore _appUpdatePreferenceStore;

    public RokuDevDeploymentService(AppUpdatePreferenceStore appUpdatePreferenceStore)
    {
        _appUpdatePreferenceStore = appUpdatePreferenceStore;
    }

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

    public async Task<string> SendPowerCommandAsync(RegisteredDisplaySnapshot display, bool powerOn)
    {
        if (display is null)
        {
            return "parametros_invalidos";
        }

        try
        {
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
            {
                var username = string.IsNullOrWhiteSpace(deviceConfig.Username) ? "rokudev" : deviceConfig.Username;
                var password = string.IsNullOrWhiteSpace(deviceConfig.Password) ? "1234" : deviceConfig.Password;

                handler.Credentials = new NetworkCredential(username, password);
                handler.PreAuthenticate = true;
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue(
                        "Basic",
                        Convert.ToBase64String(Encoding.ASCII.GetBytes(username + ":" + password)));

                var attemptedKeys = powerOn
                    ? new[] { "PowerOn", "Power" }
                    : new[] { "PowerOff", "Power" };

                foreach (var keyName in attemptedKeys)
                {
                    var commandUri = new Uri(string.Format("http://{0}:8060/keypress/{1}", host, keyName));
                    var response = await client.PostAsync(commandUri, new StringContent(string.Empty)).ConfigureAwait(false);
                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    AppLog.Write(
                        "RokuPower",
                        string.Format(
                            "Comando {0} enviado para TV id={1}, host={2}, status={3}, body={4}",
                            keyName,
                            display.DeviceId,
                            host,
                            (int)response.StatusCode,
                            SummarizeResponseBody(responseBody)));

                    if (response.IsSuccessStatusCode)
                    {
                        return keyName == attemptedKeys[0] ? "ok" : "ok_fallback_" + keyName;
                    }
                }

                return "falha_comandos_energia";
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(
                "RokuPower",
                string.Format(
                    "Falha ao enviar comando de energia para TV id={0}: {1}",
                    display.DeviceId,
                    ex.Message));
            return "erro_" + ex.GetType().Name;
        }
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
            {
                var username = string.IsNullOrWhiteSpace(deviceConfig.Username) ? "rokudev" : deviceConfig.Username;
                var password = string.IsNullOrWhiteSpace(deviceConfig.Password) ? "1234" : deviceConfig.Password;

                handler.Credentials = new NetworkCredential(username, password);
                handler.PreAuthenticate = true;
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue(
                        "Basic",
                        Convert.ToBase64String(Encoding.ASCII.GetBytes(username + ":" + password)));

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
                var requestBody = BuildPluginInstallPayload(package.PackagePath, password);
                using (var requestContent = new ByteArrayContent(requestBody.Body))
                {
                    requestContent.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + requestBody.Boundary);
                    var response = await client.PostAsync(pluginInstallUri, requestContent).ConfigureAwait(false);
                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var responseDumpPath = TryWriteResponseDump("plugin_install", host, responseBody);
                    AppLog.Write(
                        "RokuDeploy",
                        string.Format(
                            "Resposta do plugin_install: host={0}, status={1}, body={2}, dump={3}",
                            host,
                            (int)response.StatusCode,
                            SummarizePluginInstallBody(responseBody),
                            responseDumpPath));
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
                }

                try
                {
                    var homeUri = new Uri(string.Format("http://{0}:8060/keypress/Home", host));
                    await client.PostAsync(homeUri, new StringContent(string.Empty)).ConfigureAwait(false);
                    await Task.Delay(600).ConfigureAwait(false);

                    var launchUri = new Uri(string.Format("http://{0}:8060/launch/dev", host));
                    var launchResponse = await client.PostAsync(launchUri, new StringContent(string.Empty)).ConfigureAwait(false);
                    var launchBody = await launchResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var launchDumpPath = TryWriteResponseDump("launch_dev", host, launchBody);
                    AppLog.Write(
                        "RokuDeploy",
                        string.Format(
                            "Canal Roku relancado apos sideload: host={0}, status={1}, body={2}, dump={3}",
                            host,
                            (int)launchResponse.StatusCode,
                            SummarizeResponseBody(launchBody),
                            launchDumpPath));
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
        if (string.Equals(UpdateChannelNames.Normalize(BuildVersionInfo.CurrentBuildChannel), UpdateChannelNames.Local, StringComparison.OrdinalIgnoreCase))
        {
            var localPackage = ResolveLocalPackagePath();
            var localPackageReleaseId = TryReadPackageReleaseId(localPackage);
            if (!string.IsNullOrWhiteSpace(localPackage) &&
                File.Exists(localPackage) &&
                string.Equals(localPackageReleaseId, expectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Write(
                    "RokuDeploy",
                    string.Format(
                        "Build local detectado; usando pacote Roku local diretamente. esperado={0}, arquivo={1}",
                        expectedVersion,
                        localPackage));

                return new RokuResolvedPackage
                {
                    PackagePath = localPackage,
                    Source = "local"
                };
            }

            AppLog.Write(
                "RokuDeploy",
                string.Format(
                    "Build local detectado, mas pacote local nao corresponde ao alvo remoto. esperado={0}, local={1}",
                    expectedVersion,
                    localPackageReleaseId));
        }

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
        var manifestUrl = await BuildLatestRokuManifestUrlAsync().ConfigureAwait(false);
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

        var channelPackage = Path.Combine(root, UpdateChannelNames.Normalize(BuildVersionInfo.CurrentBuildChannel) + "-roku.zip");
        if (File.Exists(channelPackage))
        {
            return channelPackage;
        }

        var stablePackage = Path.Combine(root, "stable-roku.zip");
        if (File.Exists(stablePackage))
        {
            return stablePackage;
        }

        return Path.Combine(root, "hello-roku.zip");
    }

    private static string TryReadPackageReleaseId(string packagePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            {
                return string.Empty;
            }

            using (var archive = ZipFile.OpenRead(packagePath))
            {
                var entry = archive.GetEntry("source/BuildInfo.brs");
                if (entry is null)
                {
                    return string.Empty;
                }

                using (var stream = entry.Open())
                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                {
                    var content = reader.ReadToEnd();
                    var marker = "return \"";
                    var startIndex = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (startIndex < 0)
                    {
                        return string.Empty;
                    }

                    startIndex += marker.Length;
                    var endIndex = content.IndexOf('"', startIndex);
                    if (endIndex <= startIndex)
                    {
                        return string.Empty;
                    }

                    return content.Substring(startIndex, endIndex - startIndex).Trim();
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string SummarizeResponseBody(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return "(vazio)";
        }

        var compact = responseBody
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ")
            .Trim();

        if (compact.Length > 280)
        {
            return compact.Substring(0, 280) + "...";
        }

        return compact;
    }

    private static string SummarizePluginInstallBody(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return "(vazio)";
        }

        try
        {
            var patterns = new[]
            {
                "Application\\s+Received\\s*:\\s*([^<]+)",
                "Install\\s+Success\\s*:?\\s*([^<]+)",
                "Install\\s+Failure\\s*:?\\s*([^<]+)",
                "Identical\\s+to\\s+previous\\s+version\\s*--\\s*not\\s+replacing\\.?[^<]*"
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(responseBody, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (match.Success)
                {
                    var text = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return CollapseWhitespace(text);
                    }
                }
            }
        }
        catch
        {
        }

        return SummarizeResponseBody(responseBody);
    }

    private static string CollapseWhitespace(string value)
    {
        var compact = Regex.Replace(value ?? string.Empty, "\\s+", " ").Trim();
        if (compact.Length > 280)
        {
            return compact.Substring(0, 280) + "...";
        }

        return compact;
    }

    private static string TryWriteResponseDump(string prefix, string host, string body)
    {
        try
        {
            var safeHost = string.IsNullOrWhiteSpace(host) ? "unknown" : host.Replace(":", "_").Replace(".", "_");
            var root = Path.Combine(Path.GetTempPath(), "WindowManagerBroadcast", "roku-sideload-responses");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, string.Format("{0}-{1}-{2:yyyyMMdd-HHmmssfff}.html", prefix, safeHost, DateTime.Now));
            File.WriteAllText(path, body ?? string.Empty, Encoding.UTF8);
            return path;
        }
        catch
        {
            return "(nao foi possivel salvar dump)";
        }
    }

    private static RokuMultipartPayload BuildPluginInstallPayload(string packagePath, string password)
    {
        var boundary = "---------------------------" + DateTime.UtcNow.Ticks.ToString();
        var newLine = "\r\n";
        var headerBuilder = new StringBuilder();

        headerBuilder.Append("--").Append(boundary).Append(newLine);
        headerBuilder.Append("Content-Disposition: form-data; name=\"mysubmit\"").Append(newLine).Append(newLine);
        headerBuilder.Append("Install").Append(newLine);

        headerBuilder.Append("--").Append(boundary).Append(newLine);
        headerBuilder.Append("Content-Disposition: form-data; name=\"passwd\"").Append(newLine).Append(newLine);
        headerBuilder.Append(password ?? string.Empty).Append(newLine);

        headerBuilder.Append("--").Append(boundary).Append(newLine);
        headerBuilder.Append("Content-Disposition: form-data; name=\"archive\"; filename=\"").Append(Path.GetFileName(packagePath)).Append("\"").Append(newLine);
        headerBuilder.Append("Content-Type: application/octet-stream").Append(newLine).Append(newLine);

        var headerBytes = Encoding.UTF8.GetBytes(headerBuilder.ToString());
        var fileBytes = File.ReadAllBytes(packagePath);
        var footerBytes = Encoding.UTF8.GetBytes(newLine + "--" + boundary + "--" + newLine);
        var body = new byte[headerBytes.Length + fileBytes.Length + footerBytes.Length];

        Buffer.BlockCopy(headerBytes, 0, body, 0, headerBytes.Length);
        Buffer.BlockCopy(fileBytes, 0, body, headerBytes.Length, fileBytes.Length);
        Buffer.BlockCopy(footerBytes, 0, body, headerBytes.Length + fileBytes.Length, footerBytes.Length);

        return new RokuMultipartPayload
        {
            Boundary = boundary,
            Body = body
        };
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

    private async Task<string> BuildLatestRokuManifestUrlAsync()
    {
        var preferences = await _appUpdatePreferenceStore.LoadAsync(default).ConfigureAwait(false);
        var channel = UpdateChannelNames.Normalize(preferences.UpdateChannel);
        return string.Format("https://fbinerd.github.io/rokuweb/updates/{0}/latest-rokuweb.json", channel);
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

public sealed class RokuMultipartPayload
{
    public string Boundary { get; set; } = string.Empty;
    public byte[] Body { get; set; } = Array.Empty<byte>();
}
