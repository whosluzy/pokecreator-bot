using System.Diagnostics;
using System.Text.Json;

namespace pokecreator_setup;

/// <summary>
/// Self-updater: checks the latest GitHub Release, and if a newer version is
/// published, downloads its .exe asset and swaps this running exe with it.
/// </summary>
public static class Updater
{
    // Bumped on every published build. Compared against the latest release tag.
    public const string AppVersion = "1.0.7";

    // Filled in once the repo exists (owner/repo).
    public const string RepoOwner = "whosluzy";
    public const string RepoName = "pokecreator-bot";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient();
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PokeCreatorSetup");
        c.Timeout = TimeSpan.FromMinutes(10);
        return c;
    }

    public sealed record Release(string Tag, string ExeUrl);

    /// <summary>Returns the latest release if it is newer than the running version, else null.</summary>
    public static async Task<Release?> CheckAsync()
    {
        var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
        var json = await Http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        string? exeUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    exeUrl = a.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        }
        if (exeUrl is null) return null;

        return IsNewer(tag, AppVersion) ? new Release(tag, exeUrl) : null;
    }

    /// <summary>Downloads the new exe and launches a swapper that replaces this exe and restarts it.</summary>
    public static async Task DownloadAndApplyAsync(Release rel, Action<string> log)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the running exe.");
        var dir = Path.GetDirectoryName(exePath)!;
        var newExe = Path.Combine(dir, "update.exe");

        log($"Downloading {rel.Tag}…");
        var bytes = await Http.GetByteArrayAsync(rel.ExeUrl);
        await File.WriteAllBytesAsync(newExe, bytes);
        log($"Downloaded {bytes.Length / (1024 * 1024)} MB. Applying update…");

        var exeName = Path.GetFileName(exePath);
        var bat = Path.Combine(dir, "apply_update.bat");
        await File.WriteAllTextAsync(bat,
            "@echo off\r\n" +
            "echo Updating PokeCreator…\r\n" +
            ":wait\r\n" +
            $"tasklist /fi \"imagename eq {exeName}\" | find /i \"{exeName}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
            $"move /y \"{newExe}\" \"{exePath}\" >nul\r\n" +
            $"start \"\" \"{exePath}\"\r\n" +
            "del \"%~f0\"\r\n");

        Process.Start(new ProcessStartInfo
        {
            FileName = bat,
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = dir,
        });

        // Give the swapper a moment, then exit so the file unlocks.
        await Task.Delay(500);
        Application.Exit();
    }

    private static bool IsNewer(string remoteTag, string local)
    {
        static Version Parse(string s)
        {
            s = s.TrimStart('v', 'V').Trim();
            return Version.TryParse(s, out var v) ? v : new Version(0, 0, 0);
        }
        return Parse(remoteTag) > Parse(local);
    }
}
