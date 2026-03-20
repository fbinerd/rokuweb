using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime;

public sealed class AppUpdateManifestService
{
    private static readonly HttpClient HttpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public async Task<AppUpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        var manifestUrl = BuildVersionInfo.LatestManifestUrl;

        try
        {
            using (var response = await HttpClient.GetAsync(manifestUrl, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    var serializer = new DataContractJsonSerializer(typeof(UpdateManifestDocument));
                    var document = serializer.ReadObject(stream) as UpdateManifestDocument;

                    if (document is null || string.IsNullOrWhiteSpace(document.CurrentRelease))
                    {
                        return AppUpdateCheckResult.Failure(
                            manifestUrl,
                            "Manifesto de atualizacao invalido ou vazio.");
                    }

                    var currentReleaseId = BuildVersionInfo.ReleaseId;
                    var currentVersion = BuildVersionInfo.Version;
                    var latestRelease = document.Releases?.FirstOrDefault(x => string.Equals(x.ReleaseId, document.CurrentRelease, StringComparison.OrdinalIgnoreCase));
                    if (latestRelease is null)
                    {
                        latestRelease = new UpdateReleaseEntry
                        {
                            ReleaseId = document.CurrentRelease,
                            Version = document.CurrentVersion
                        };
                    }

                    var updateAvailable = !string.Equals(currentReleaseId, document.CurrentRelease, StringComparison.OrdinalIgnoreCase);
                    var recommendedUrl = SelectRecommendedPackageUrl(currentReleaseId, currentVersion, latestRelease);
                    var message = updateAvailable
                        ? string.Format("Atualizacao disponivel: {0} ({1}).", latestRelease.Version, latestRelease.ReleaseId)
                        : string.Format("Aplicativo atualizado em {0} ({1}).", currentVersion, currentReleaseId);

                    return AppUpdateCheckResult.Success(
                        manifestUrl,
                        currentVersion,
                        currentReleaseId,
                        latestRelease.Version,
                        latestRelease.ReleaseId,
                        updateAvailable,
                        recommendedUrl,
                        message);
                }
            }
        }
        catch (Exception ex)
        {
            return AppUpdateCheckResult.Failure(
                manifestUrl,
                string.Format("Falha ao consultar atualizacoes: {0}", ex.Message));
        }
    }

    private static string SelectRecommendedPackageUrl(string currentReleaseId, string currentVersion, UpdateReleaseEntry latestRelease)
    {
        if (!string.IsNullOrWhiteSpace(latestRelease.DeltaPackageUrl))
        {
            if (!string.IsNullOrWhiteSpace(latestRelease.FullPackageRequiredIfCurrentReleaseOlderThan) &&
                string.Equals(latestRelease.FullPackageRequiredIfCurrentReleaseOlderThan, currentReleaseId, StringComparison.OrdinalIgnoreCase))
            {
                return latestRelease.DeltaPackageUrl;
            }

            if (string.IsNullOrWhiteSpace(currentReleaseId) &&
                !string.IsNullOrWhiteSpace(latestRelease.FullPackageRequiredIfCurrentVersionOlderThan) &&
                string.Equals(latestRelease.FullPackageRequiredIfCurrentVersionOlderThan, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                return latestRelease.DeltaPackageUrl;
            }
        }

        return latestRelease.FullPackageUrl ?? string.Empty;
    }
}

public sealed class AppUpdateCheckResult
{
    private AppUpdateCheckResult()
    {
    }

    public bool Succeeded { get; private set; }
    public string ManifestUrl { get; private set; } = string.Empty;
    public string CurrentVersion { get; private set; } = string.Empty;
    public string CurrentReleaseId { get; private set; } = string.Empty;
    public string LatestVersion { get; private set; } = string.Empty;
    public string LatestReleaseId { get; private set; } = string.Empty;
    public bool UpdateAvailable { get; private set; }
    public string RecommendedPackageUrl { get; private set; } = string.Empty;
    public string StatusMessage { get; private set; } = string.Empty;

    public static AppUpdateCheckResult Success(
        string manifestUrl,
        string currentVersion,
        string currentReleaseId,
        string latestVersion,
        string latestReleaseId,
        bool updateAvailable,
        string recommendedPackageUrl,
        string statusMessage)
    {
        return new AppUpdateCheckResult
        {
            Succeeded = true,
            ManifestUrl = manifestUrl,
            CurrentVersion = currentVersion,
            CurrentReleaseId = currentReleaseId,
            LatestVersion = latestVersion,
            LatestReleaseId = latestReleaseId,
            UpdateAvailable = updateAvailable,
            RecommendedPackageUrl = recommendedPackageUrl ?? string.Empty,
            StatusMessage = statusMessage ?? string.Empty
        };
    }

    public static AppUpdateCheckResult Failure(string manifestUrl, string statusMessage)
    {
        return new AppUpdateCheckResult
        {
            Succeeded = false,
            ManifestUrl = manifestUrl,
            CurrentVersion = BuildVersionInfo.Version,
            CurrentReleaseId = BuildVersionInfo.ReleaseId,
            LatestVersion = string.Empty,
            LatestReleaseId = string.Empty,
            UpdateAvailable = false,
            RecommendedPackageUrl = string.Empty,
            StatusMessage = statusMessage ?? string.Empty
        };
    }
}

[DataContract]
public sealed class UpdateManifestDocument
{
    [DataMember(Name = "currentRelease")]
    public string CurrentRelease { get; set; } = string.Empty;

    [DataMember(Name = "currentVersion")]
    public string CurrentVersion { get; set; } = string.Empty;

    [DataMember(Name = "releases")]
    public UpdateReleaseEntry[] Releases { get; set; } = Array.Empty<UpdateReleaseEntry>();
}

[DataContract]
public sealed class UpdateReleaseEntry
{
    [DataMember(Name = "releaseId")]
    public string ReleaseId { get; set; } = string.Empty;

    [DataMember(Name = "version")]
    public string Version { get; set; } = string.Empty;

    [DataMember(Name = "fullPackageUrl")]
    public string FullPackageUrl { get; set; } = string.Empty;

    [DataMember(Name = "deltaPackageUrl")]
    public string DeltaPackageUrl { get; set; } = string.Empty;
    [DataMember(Name = "fullPackageRequiredIfCurrentVersionOlderThan")]
    public string FullPackageRequiredIfCurrentVersionOlderThan { get; set; } = string.Empty;

    [DataMember(Name = "fullPackageRequiredIfCurrentReleaseOlderThan")]
    public string FullPackageRequiredIfCurrentReleaseOlderThan { get; set; } = string.Empty;
}
