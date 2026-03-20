using System;
using System.Collections.Generic;
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
                    var packagePlan = SelectRecommendedPackagePlan(currentReleaseId, document, latestRelease);
                    var message = updateAvailable
                        ? string.Format("Atualizacao disponivel: {0} ({1}). Plano: {2}.", latestRelease.Version, latestRelease.ReleaseId, packagePlan.Description)
                        : string.Format("Aplicativo atualizado em {0} ({1}).", currentVersion, currentReleaseId);

                    return AppUpdateCheckResult.Success(
                        manifestUrl,
                        currentVersion,
                        currentReleaseId,
                        latestRelease.Version,
                        latestRelease.ReleaseId,
                        updateAvailable,
                        packagePlan.PrimaryUrl,
                        packagePlan.PackageUrls,
                        packagePlan.Description,
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

    private static UpdatePackagePlan SelectRecommendedPackagePlan(string currentReleaseId, UpdateManifestDocument document, UpdateReleaseEntry latestRelease)
    {
        if (string.IsNullOrWhiteSpace(currentReleaseId) || document.Releases is null || document.Releases.Length == 0)
        {
            return UpdatePackagePlan.Full(latestRelease.FullPackageUrl);
        }

        if (string.Equals(currentReleaseId, latestRelease.ReleaseId, StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePackagePlan.None();
        }

        if (!document.Releases.Any(x => string.Equals(x.ReleaseId, currentReleaseId, StringComparison.OrdinalIgnoreCase)))
        {
            return UpdatePackagePlan.Full(latestRelease.FullPackageUrl);
        }

        var patchUrls = new List<string>();
        var pointerReleaseId = currentReleaseId;

        while (!string.Equals(pointerReleaseId, latestRelease.ReleaseId, StringComparison.OrdinalIgnoreCase))
        {
            var nextRelease = document.Releases.FirstOrDefault(x =>
                x.DeltaSupportedFromReleases?.Any(y => string.Equals(y, pointerReleaseId, StringComparison.OrdinalIgnoreCase)) == true);

            if (nextRelease is null || string.IsNullOrWhiteSpace(nextRelease.DeltaPackageUrl))
            {
                return UpdatePackagePlan.Full(latestRelease.FullPackageUrl);
            }

            patchUrls.Add(nextRelease.DeltaPackageUrl);
            pointerReleaseId = nextRelease.ReleaseId;

            if (patchUrls.Count > document.Releases.Length + 1)
            {
                return UpdatePackagePlan.Full(latestRelease.FullPackageUrl);
            }
        }

        return patchUrls.Count == 0
            ? UpdatePackagePlan.Full(latestRelease.FullPackageUrl)
            : UpdatePackagePlan.PatchChain(patchUrls);
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
    public string[] PackageUrls { get; private set; } = Array.Empty<string>();
    public string PackageStrategyDescription { get; private set; } = string.Empty;
    public string StatusMessage { get; private set; } = string.Empty;

    public static AppUpdateCheckResult Success(
        string manifestUrl,
        string currentVersion,
        string currentReleaseId,
        string latestVersion,
        string latestReleaseId,
        bool updateAvailable,
        string recommendedPackageUrl,
        IEnumerable<string>? packageUrls,
        string packageStrategyDescription,
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
            PackageUrls = packageUrls?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() ?? Array.Empty<string>(),
            PackageStrategyDescription = packageStrategyDescription ?? string.Empty,
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
            PackageUrls = Array.Empty<string>(),
            PackageStrategyDescription = string.Empty,
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

    [DataMember(Name = "deltaSupportedFromReleases")]
    public string[] DeltaSupportedFromReleases { get; set; } = Array.Empty<string>();

    [DataMember(Name = "fullPackageRequiredIfCurrentVersionOlderThan")]
    public string FullPackageRequiredIfCurrentVersionOlderThan { get; set; } = string.Empty;

    [DataMember(Name = "fullPackageRequiredIfCurrentReleaseOlderThan")]
    public string FullPackageRequiredIfCurrentReleaseOlderThan { get; set; } = string.Empty;
}

internal sealed class UpdatePackagePlan
{
    private UpdatePackagePlan()
    {
    }

    public string PrimaryUrl { get; private set; } = string.Empty;
    public string[] PackageUrls { get; private set; } = Array.Empty<string>();
    public string Description { get; private set; } = string.Empty;

    public static UpdatePackagePlan None()
    {
        return new UpdatePackagePlan
        {
            PackageUrls = Array.Empty<string>(),
            Description = "sem download"
        };
    }

    public static UpdatePackagePlan Full(string url)
    {
        return new UpdatePackagePlan
        {
            PrimaryUrl = url ?? string.Empty,
            PackageUrls = string.IsNullOrWhiteSpace(url) ? Array.Empty<string>() : new string[] { url ?? string.Empty },
            Description = "pacote completo"
        };
    }

    public static UpdatePackagePlan PatchChain(IEnumerable<string> urls)
    {
        var packageUrls = urls.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        return new UpdatePackagePlan
        {
            PrimaryUrl = packageUrls.FirstOrDefault() ?? string.Empty,
            PackageUrls = packageUrls,
            Description = string.Format("cadeia de {0} patch(es)", packageUrls.Length)
        };
    }
}
