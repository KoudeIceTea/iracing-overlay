using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using IracingOverlay.App.Settings;

namespace IracingOverlay.App.Updates;

/// <summary>A published GitHub release ("v1.2.0") and its ZIP download.</summary>
internal sealed record ReleaseInfo(Version Version, string Tag, string Name, string PageUrl, long? AssetId, string? AssetName);

internal enum UpdateStatus { Idle, Checking, UpToDate, Available, NoReleases, Failed }

/// <summary>
/// Checks the public GitHub repository for a newer release on start and every hour.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    public const string Repository = "KoudeIceTea/iracing-overlay";
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    public static Version Current { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));
    private static readonly HttpClient Http = CreateClient();

    private readonly DispatcherTimer _timer;
    private string? _notifiedTag;

    public UpdateService(AppSettings settings)
    {
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    /// <summary>Raised (on the UI thread) whenever status, latest release or check time changes.</summary>
    public event Action? Changed;
    /// <summary>Raised once per newer version, for the tray notification.</summary>
    public event Action<ReleaseInfo>? NewVersionFound;

    public UpdateStatus Status { get; private set; } = UpdateStatus.Idle;
    public ReleaseInfo? Latest { get; private set; }
    public DateTime? LastChecked { get; private set; }
    public string? Error { get; private set; }

    public void Start()
    {
        _timer.Start();
        _ = CheckAsync();
    }

    public void Dispose() => _timer.Stop();

    public async Task CheckAsync()
    {
        if (Status == UpdateStatus.Checking) return;
        Set(UpdateStatus.Checking);
        try
        {
            using var response = await Http.GetAsync($"repos/{Repository}/releases/latest");
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Finish(UpdateStatus.NoReleases);
                return;
            }
            response.EnsureSuccessStatusCode();

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var release = json.RootElement;
            string tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!TryParseVersion(tag, out var version))
            {
                Error = $"the latest release has an unrecognised tag \"{tag}\"";
                Finish(UpdateStatus.Failed);
                return;
            }

            long? assetId = null;
            string? assetName = null;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                assetId = asset.GetProperty("id").GetInt64();
                assetName = name;
                break;
            }
            Latest = new ReleaseInfo(version, tag, release.GetProperty("name").GetString() is { Length: > 0 } n ? n : tag,
                release.GetProperty("html_url").GetString() ?? $"https://github.com/{Repository}/releases", assetId, assetName);

            bool newer = version > Current;
            Finish(newer ? UpdateStatus.Available : UpdateStatus.UpToDate);
            if (newer && _notifiedTag != tag)
            {
                _notifiedTag = tag;
                NewVersionFound?.Invoke(Latest);
            }
        }
        catch (Exception ex)
        {
            Error = ex is HttpRequestException or TaskCanceledException ? "GitHub could not be reached" : ex.Message;
            Finish(UpdateStatus.Failed);
        }
    }

    /// <summary>Downloads the release ZIP to the user's Downloads folder and returns its path.</summary>
    public async Task<string> DownloadAsync(IProgress<double>? progress = null)
    {
        var release = Latest ?? throw new InvalidOperationException("No release to download.");
        if (release.AssetId is not { } assetId) throw new InvalidOperationException("The release has no ZIP attached.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/assets/{assetId}");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, release.AssetName ?? $"IracingOverlay-{release.Tag}.zip");
        string partial = path + ".part";
        long? total = response.Content.Headers.ContentLength;
        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var target = File.Create(partial))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0) progress?.Report((double)done / total.Value);
            }
        }
        File.Move(partial, path, overwrite: true);
        return path;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.BaseAddress = new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("IracingOverlay", Current.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        bool ok = Version.TryParse(tag.TrimStart('v', 'V'), out var parsed);
        version = ok ? Normalize(parsed!) : new Version(0, 0, 0);
        return ok;
    }

    /// <summary>Compare as major.minor.patch: "1.0.0.0" (assembly) and "1.0.0" (tag) are the same version.</summary>
    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    private void Set(UpdateStatus status)
    {
        Status = status;
        if (status != UpdateStatus.Failed) Error = null;
        Changed?.Invoke();
    }

    private void Finish(UpdateStatus status)
    {
        LastChecked = DateTime.Now;
        Set(status);
    }
}
