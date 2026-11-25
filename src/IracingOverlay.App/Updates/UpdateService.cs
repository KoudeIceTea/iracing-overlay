using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;

namespace IracingOverlay.App.Updates;

/// <summary>A published GitHub release ("v1.2.0") and its ZIP download.</summary>
internal sealed record ReleaseInfo(Version Version, string Tag, string Name, string PageUrl, ReleaseAsset? Asset);

/// <summary>The release's ZIP; <see cref="Sha256"/> is GitHub's digest of the upload, when it has one.</summary>
internal sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string? Sha256);

/// <summary>What the installer reported after the last update attempt.</summary>
internal sealed record InstallResult(bool Succeeded, string Detail);

internal enum UpdateStatus { Idle, Checking, UpToDate, Available, NoReleases, Failed }

/// <summary>
/// Checks the public GitHub repository for a newer release on start and every hour, and installs it on request:
/// the ZIP is downloaded, verified and extracted while the app runs, then install-update.ps1 swaps the files
/// after the app exits (rolling back on failure) and starts it again.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    public const string Repository = "KoudeIceTea/iracing-overlay";
    public const string AssetName = "IracingOverlay-win-x64.zip";
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    public static Version Current { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    private static readonly HttpClient Api = CreateClient(TimeSpan.FromSeconds(30), "application/vnd.github+json");
    // Downloads have no overall limit (slow connections); a stalled transfer is cancelled instead.
    private static readonly HttpClient Downloads = CreateClient(Timeout.InfiniteTimeSpan, "application/octet-stream");
    private static readonly string WorkFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IracingOverlay", "Updates");
    private static readonly string ResultFile = Path.Combine(WorkFolder, "last-install.txt");

    private readonly DispatcherTimer _timer;
    private string? _notifiedTag;

    public UpdateService()
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
    public bool IsInstalling { get; private set; }

    /// <summary>
    /// False for development builds (bin\ output with separate DLLs): installing the single-file release there
    /// would mix it with the build output, so those builds link to the release page instead.
    /// </summary>
    public static bool CanInstall { get; } = Environment.ProcessPath is { } exe &&
        !File.Exists(Path.Combine(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(exe) + ".dll"));

    public void Start()
    {
        _timer.Start();
        _ = CheckAsync();
    }

    public void Dispose() => _timer.Stop();

    public async Task CheckAsync()
    {
        if (Status == UpdateStatus.Checking || IsInstalling) return;
        Set(UpdateStatus.Checking);
        try
        {
            using var response = await Api.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest");
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Finish(UpdateStatus.NoReleases);
                return;
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                Error = "GitHub's rate limit was reached, trying again later";
                Finish(UpdateStatus.Failed);
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

            Latest = new ReleaseInfo(version, tag,
                release.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } n ? n : tag,
                release.GetProperty("html_url").GetString() ?? $"https://github.com/{Repository}/releases",
                FindAsset(release));

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

    /// <summary>
    /// Downloads, verifies and extracts the latest release, then starts the installer, which waits for this
    /// process to exit. The caller must shut the app down once this returns.
    /// </summary>
    public async Task PrepareInstallAsync(IProgress<double>? progress = null)
    {
        if (!CanInstall) throw new InvalidOperationException("Development builds can't install updates.");
        var release = Latest ?? throw new InvalidOperationException("No release to install.");
        var asset = release.Asset ?? throw new InvalidOperationException("The release has no ZIP attached.");
        if (IsInstalling) throw new InvalidOperationException("An update is already being installed.");

        IsInstalling = true;
        Changed?.Invoke();
        try
        {
            CleanWorkFolder();
            Directory.CreateDirectory(WorkFolder);
            string archive = Path.Combine(WorkFolder, $"{release.Tag}.zip");
            await DownloadAsync(asset, archive, progress);

            string stage = Path.Combine(WorkFolder, release.Tag);
            string executable = Path.GetFileName(Environment.ProcessPath!);
            await Task.Run(() => Extract(archive, stage, executable, release.Version));
            File.Delete(archive);

            StartInstaller(stage, executable, release.Tag);
        }
        catch
        {
            IsInstalling = false;
            Changed?.Invoke();
            throw;
        }
    }

    /// <summary>The outcome of an update installed before this start, reported once.</summary>
    public static InstallResult? TakeInstallResult()
    {
        try
        {
            if (!File.Exists(ResultFile)) return null;
            string[] lines = File.ReadAllLines(ResultFile);
            File.Delete(ResultFile);
            if (lines.Length == 0) return null;
            return new InstallResult(lines[0].Trim() == "ok", string.Join(" ", lines.Skip(1)).Trim());
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static ReleaseAsset? FindAsset(JsonElement release)
    {
        // Prefer the ZIP scripts/release.ps1 uploads; fall back to any ZIP.
        JsonElement? pick = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase)) { pick = asset; break; }
            if (pick == null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) pick = asset;
        }
        if (pick is not { } a || a.GetProperty("browser_download_url").GetString() is not { Length: > 0 } url) return null;

        string? sha256 = a.TryGetProperty("digest", out var digest) && digest.GetString() is { } d &&
            d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d["sha256:".Length..] : null;
        return new ReleaseAsset(a.GetProperty("name").GetString()!, url, a.GetProperty("size").GetInt64(), sha256);
    }

    private static async Task DownloadAsync(ReleaseAsset asset, string path, IProgress<double>? progress)
    {
        using var stall = new CancellationTokenSource(StallTimeout);
        try
        {
            using var response = await Downloads.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? asset.Size;
            string partial = path + ".part";
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(stall.Token))
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, stall.Token)) > 0)
                {
                    stall.CancelAfter(StallTimeout);
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), stall.Token);
                    done += read;
                    if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
                }
            }

            if (done != asset.Size)
                throw new InvalidDataException($"the download is incomplete ({done:N0} of {asset.Size:N0} bytes)");
            if (asset.Sha256 is { } expected &&
                !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the download is corrupt (checksum mismatch)");
            File.Move(partial, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("the download stalled");
        }
        catch (HttpRequestException)
        {
            throw new HttpRequestException("GitHub could not be reached");
        }
    }

    private static void Extract(string archive, string stage, string executable, Version expected)
    {
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        try
        {
            // ExtractToDirectory rejects entries that would land outside the folder.
            ZipFile.ExtractToDirectory(archive, stage);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("the download is not a valid ZIP");
        }

        string exe = Path.Combine(stage, executable);
        if (!File.Exists(exe)) throw new InvalidDataException($"the release doesn't contain {executable}");
        var info = FileVersionInfo.GetVersionInfo(exe);
        var found = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        if (found != new Version(0, 0, 0) && found != expected)
            throw new InvalidDataException($"the release contains version {found} instead of {expected}");
    }

    private void StartInstaller(string stage, string executable, string tag)
    {
        string script = Path.Combine(WorkFolder, "install-update.ps1");
        using (var resource = typeof(UpdateService).Assembly.GetManifestResourceStream("IracingOverlay.App.Updates.install-update.ps1")
            ?? throw new InvalidOperationException("The update installer is missing from this build."))
        using (var file = File.Create(script))
            resource.CopyTo(file);

        string target = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        bool elevate = !IsWritable(target);
        var start = new ProcessStartInfo("powershell.exe")
        {
            // Elevation (the "runas" verb) needs the shell; a hidden window keeps the console out of sight either way.
            UseShellExecute = elevate,
            Verb = elevate ? "runas" : "",
            CreateNoWindow = !elevate,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = WorkFolder,
        };
        foreach (string argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
            "-Source", stage, "-Target", target, "-Executable", executable,
            "-ProcessId", Environment.ProcessId.ToString(), "-Version", tag, "-ResultFile", ResultFile,
        })
            start.ArgumentList.Add(argument);
        if (elevate) start.ArgumentList.Add("-Elevated");

        try
        {
            if (Process.Start(start) is null) throw new InvalidOperationException("the update installer could not be started");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: the UAC prompt was declined
        {
            throw new InvalidOperationException($"administrator permission is needed to update the app in {target}");
        }
    }

    private static bool IsWritable(string folder)
    {
        try
        {
            string probe = Path.Combine(folder, $".update-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>Removes leftovers of earlier downloads; keeps the result file until it's been reported.</summary>
    private static void CleanWorkFolder()
    {
        if (!Directory.Exists(WorkFolder)) return;
        foreach (string entry in Directory.EnumerateFileSystemEntries(WorkFolder))
        {
            if (string.Equals(entry, ResultFile, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static HttpClient CreateClient(TimeSpan timeout, string accept)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("IracingOverlay", Current.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
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
