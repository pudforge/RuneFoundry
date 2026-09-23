using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RuneFoundry.Core;

/// <summary>One file attached to a release.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>A published release, as GitHub describes it.</summary>
public sealed record ReleaseInfo(string Version, string Title, string Notes, string PageUrl,
                                 IReadOnlyList<ReleaseAsset> Assets)
{
    public bool IsNewerThanCurrent => AppVersion.IsNewerThanCurrent(Version);
}

/// <summary>
/// How this copy of RuneFoundry was installed, which decides what an update downloads.
///
/// The release zip unpacks both programs into one folder that shares one runtime, so an
/// update replaces the folder's contents. A standalone executable carries everything in
/// itself, so an update replaces just that file.
/// </summary>
public sealed record InstallLayout(string Directory, string ExePath, bool IsStandalone, string Program)
{
    /// <summary>The copy that is running now.</summary>
    public static InstallLayout Current(string program) => new(
        AppContext.BaseDirectory.TrimEnd('\\', '/'),
        Environment.ProcessPath ?? "",
        // A single-file app has no assembly files on disk, so their location is empty.
        IsStandalone: string.IsNullOrEmpty(typeof(InstallLayout).Assembly.Location),
        program);

    /// <summary>
    /// Whether this is a copy a release put here. A build output folder has no README.txt,
    /// and updating it would overwrite a developer's build with a published one.
    /// </summary>
    public bool IsReleaseInstall => IsStandalone || File.Exists(Path.Combine(Directory, "README.txt"));
}

/// <summary>
/// Finds, downloads and stages RuneFoundry updates from the GitHub releases page. Nothing
/// here touches the running install; <see cref="UpdateScript"/> does that once the program
/// has closed, because Windows will not overwrite files a running program has open.
/// </summary>
public static class Updates
{
    public const string Repository = "pudforge/RuneFoundry";
    public const string LatestApi = $"https://api.github.com/repos/{Repository}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub refuses API requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RuneFoundry", AppVersion.Current));
        return client;
    }

    public static async Task<ReleaseInfo> GetLatestAsync(CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await Http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        return ParseRelease(await response.Content.ReadAsStringAsync(cancel));
    }

    /// <summary>Reads GitHub's release JSON. Public so the tests can feed it a fixture.</summary>
    public static ReleaseInfo ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

        var tag = Str(root, "tag_name");
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                // GitHub gives "sha256:<hex>" for assets uploaded since mid-2025.
                var digest = Str(a, "digest");
                var sha = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
                assets.Add(new ReleaseAsset(Str(a, "name"), Str(a, "browser_download_url"),
                    a.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0, sha));
            }
        }

        return new ReleaseInfo(tag.TrimStart('v', 'V'), Str(root, "name"), Str(root, "body"),
                               Str(root, "html_url"), assets);
    }

    /// <summary>
    /// The download that updates this install: the zip for a folder install, the matching
    /// executable for a standalone one. Null when the release does not carry it.
    /// </summary>
    public static ReleaseAsset? PickAsset(ReleaseInfo release, InstallLayout layout)
    {
        if (!layout.IsStandalone)
            return release.Assets.FirstOrDefault(a =>
                a.Name.StartsWith("RuneFoundry-", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        // Uploaded as "RuneFoundry.Editor.0.6.3-alpha.exe": GitHub turns the spaces in the
        // built name into dots.
        var prefix = $"RuneFoundry.{layout.Program}.";
        return release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Downloads an asset into <paramref name="folder"/> and checks it against the hash GitHub
    /// published. A download that does not match is deleted and refused.
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseAsset asset, string folder, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, asset.Name);

        using (var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var target = File.Create(path);
            await source.CopyToAsync(target, cancel);
        }

        if (asset.Size > 0 && new FileInfo(path).Length != asset.Size)
        {
            File.Delete(path);
            throw new InvalidDataException("The download was cut short. Nothing was changed; try again.");
        }

        if (asset.Sha256 is { Length: 64 } expected &&
            !string.Equals(Hashing.Sha256File(path), expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException("The download does not match the release's checksum. Nothing was changed.");
        }

        return path;
    }

    /// <summary>
    /// Unpacks a release zip into a clean staging folder and checks it holds both programs,
    /// so a wrong or broken zip is caught before the install is touched.
    /// </summary>
    public static string StageZip(string zipPath, string stagingFolder)
    {
        if (Directory.Exists(stagingFolder)) Directory.Delete(stagingFolder, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, stagingFolder);

        foreach (var exe in new[] { "RuneFoundry Editor.exe", "RuneFoundry Launcher.exe" })
            if (!File.Exists(Path.Combine(stagingFolder, exe)))
                throw new InvalidDataException($"The update is missing {exe}. Nothing was changed.");

        return stagingFolder;
    }

    /// <summary>
    /// Where a standalone executable goes after the update. A name carrying the old version,
    /// as downloads are named, gets the new one; a name the user chose is kept.
    /// </summary>
    public static string StandaloneTarget(string currentExe, string oldVersion, string newVersion)
    {
        var name = Path.GetFileName(currentExe);
        return name.Contains(oldVersion, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetDirectoryName(currentExe)!, name.Replace(oldVersion, newVersion, StringComparison.OrdinalIgnoreCase))
            : currentExe;
    }

    /// <summary>Whether this process can write into <paramref name="folder"/> without elevation.</summary>
    public static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".rf-write-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// The PowerShell script that finishes an update once the program has exited: it waits for
/// the process, puts the new files in place, and starts the program again. If anything
/// fails it says so and starts the old copy, which it has not removed.
/// </summary>
public static class UpdateScript
{
    public static string ForFolder(int pid, string staging, string installDir, string relaunch, string log) =>
        Build(pid, log, relaunch, $$"""
            # Retried: antivirus and the exiting process can hold a file for a moment.
            $copied = $false
            for ($i = 0; $i -lt 20 -and -not $copied; $i++) {
                try { Copy-Item -Path (Join-Path {{Q(staging)}} '*') -Destination {{Q(installDir)}} -Recurse -Force; $copied = $true }
                catch { Start-Sleep -Milliseconds 500 }
            }
            if (-not $copied) { throw "Could not replace the files in {{E(installDir)}}. Is a RuneFoundry program still open?" }
            """);

    public static string ForStandalone(int pid, string downloaded, string currentExe, string targetExe, string log) =>
        Build(pid, log, targetExe, $$"""
            $moved = $false
            for ($i = 0; $i -lt 20 -and -not $moved; $i++) {
                try { Move-Item -LiteralPath {{Q(downloaded)}} -Destination {{Q(targetExe)}} -Force; $moved = $true }
                catch { Start-Sleep -Milliseconds 500 }
            }
            if (-not $moved) { throw "Could not write {{E(targetExe)}}." }
            if ({{Q(targetExe)}} -ne {{Q(currentExe)}}) { Remove-Item -LiteralPath {{Q(currentExe)}} -Force -ErrorAction SilentlyContinue }
            """, fallback: currentExe);

    private static string Build(int pid, string log, string relaunch, string body, string? fallback = null) => $$"""
        $ErrorActionPreference = 'Stop'
        try {
            Wait-Process -Id {{pid}} -Timeout 60 -ErrorAction SilentlyContinue
        {{Indent(body)}}
            'Updated.' | Out-File -LiteralPath {{Q(log)}} -Encoding utf8
            Start-Relaunch {{Q(relaunch)}}
        }
        catch {
            $_ | Out-String | Out-File -LiteralPath {{Q(log)}} -Encoding utf8
            Add-Type -AssemblyName PresentationFramework
            [System.Windows.MessageBox]::Show("RuneFoundry could not finish updating.`n`n$($_.Exception.Message)`n`nYour copy was left as it was.", 'RuneFoundry update') | Out-Null
            Start-Relaunch {{Q(fallback ?? relaunch)}}
        }
        """.Insert(0, RelaunchFunction);

    // Started through Explorer: this script may be running elevated to write Program Files,
    // and a program it started directly would inherit that. Explorer starts it as the user.
    private const string RelaunchFunction = """
        function Start-Relaunch([string] $exe) {
            if (Test-Path -LiteralPath $exe) { Start-Process -FilePath 'explorer.exe' -ArgumentList ('"' + $exe + '"') }
        }

        """;

    private static string Indent(string text) =>
        string.Join("\n", text.Split('\n').Select(l => "    " + l.TrimEnd('\r')));

    /// <summary>A PowerShell single-quoted literal: nothing inside is expanded.</summary>
    private static string Q(string text) => "'" + text.Replace("'", "''") + "'";

    /// <summary>Text for inside a double-quoted PowerShell string.</summary>
    private static string E(string text) =>
        text.Replace("`", "``").Replace("\"", "`\"").Replace("$", "`$");
}
