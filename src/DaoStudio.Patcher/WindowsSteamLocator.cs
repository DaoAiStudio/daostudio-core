using DaoStudio.Contracts;
using DaoStudio.Patcher.Abstractions;
using Microsoft.Win32;

namespace DaoStudio.Patcher;

public sealed class WindowsSteamLocator : ISteamLocator
{
    private readonly string[] roots;
    public WindowsSteamLocator() : this(DefaultRoots()) { }
    public WindowsSteamLocator(IEnumerable<string> steamRoots) => roots = steamRoots.ToArray();

    private static IEnumerable<string> DefaultRoots()
    {
        var result = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam" })
            {
                try { if (Registry.GetValue(key, key.Contains("CURRENT_USER") ? "SteamPath" : "InstallPath", null) is string path) result.Add(path); }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
            }
            result.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            result.Add(@"C:\Program Files\Steam");
        }
        return result;
    }

    public Task<DetectedGameInstall?> FindGameAsync(GameProfileManifest manifest, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(manifest.SteamAppId) || !manifest.SteamAppId.All(char.IsAsciiDigit)) throw new InvalidDataException("Invalid app id.");
        var libraries = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(path)) continue;
            var document = VdfParser.Parse(File.ReadAllText(path));
            if (document.Children.TryGetValue("libraryfolders", out var folders))
                foreach (var entry in folders.Children.Where(e => e.Key.All(char.IsAsciiDigit)))
                    if ((entry.Value.Value ?? entry.Value.GetValue("path")) is { } library) libraries.Add(library);
        }
        foreach (var library in libraries)
        {
            ct.ThrowIfCancellationRequested();
            var acf = Path.Combine(library, "steamapps", $"appmanifest_{manifest.SteamAppId}.acf");
            if (!File.Exists(acf)) continue;
            var doc = VdfParser.Parse(File.ReadAllText(acf));
            if (!doc.Children.TryGetValue("AppState", out var state) || state.GetValue("appid") != manifest.SteamAppId || state.GetValue("installdir") is not { } install) continue;
            var directory = SafePath.Resolve(Path.Combine(library, "steamapps", "common"), install);
            var executable = SafePath.Resolve(directory, manifest.ExecutableName);
            if (Directory.Exists(directory)) return Task.FromResult<DetectedGameInstall?>(new(manifest.GameId, manifest.SteamAppId, directory, executable, File.Exists(executable)));
        }
        return Task.FromResult<DetectedGameInstall?>(null);
    }
}
