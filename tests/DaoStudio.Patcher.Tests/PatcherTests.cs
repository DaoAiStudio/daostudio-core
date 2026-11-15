using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DaoStudio.Contracts;
using DaoStudio.Patcher;
using DaoStudio.UnityPlugin;
using Xunit;

namespace DaoStudio.Patcher.Tests;

public sealed class PatcherTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "daostudio-tests-" + Guid.NewGuid());
    public PatcherTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private string Dir(string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private static GameProfileManifest Profile => new("guigubahuang", "Game", "1468810", "guigubahuang.exe", "unity", new("loader", "1", "https://example.com", ""), new("font", "license", "https://example.com", ""), "1", 1);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static Dictionary<string, string> Snapshot(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(directory, p), p => Hash(File.ReadAllBytes(p)));

    [Fact]
    public async Task SteamParsesModernAndLegacyLibrariesAndFindsExecutable()
    {
        var steam = Dir("steam"); var library = Dir("library");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        var apps = Directory.CreateDirectory(Path.Combine(library, "steamapps")).FullName;
        var game = Directory.CreateDirectory(Path.Combine(apps, "common", "鬼谷八荒")).FullName;
        File.WriteAllText(Path.Combine(game, "guigubahuang.exe"), "exe");
        var escaped = library.Replace("\\", "\\\\");
        foreach (var body in new[] { $"\"1\" {{ \"path\" \"{escaped}\" \"apps\" {{ \"1468810\" \"123\" }} }}", $"\"1\" \"{escaped}\"" })
        {
            File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "// libraries\n\"libraryfolders\" { " + body + " }");
            var acf = "\"AppState\" { \"appid\" \"1468810\" \"installdir\" \"鬼谷八荒\" }";
            File.WriteAllText(Path.Combine(apps, "appmanifest_1468810.acf"), acf);
            Assert.Equal("鬼谷八荒", VdfParser.Parse(acf).Children["AppState"].GetValue("installdir"));
            var result = await new WindowsSteamLocator(new[] { steam }).FindGameAsync(Profile);
            Assert.NotNull(result); Assert.True(result.IsExecutablePresent); Assert.Equal(game, result.InstallDirectory);
        }
    }

    [Theory]
    [InlineData("\"root\" { \"key\" \"value\"")]
    [InlineData("\"key\" \"unterminated")]
    [InlineData("}")]
    [InlineData("a b a c")]
    public void InvalidVdfIsRejected(string input) => Assert.Throws<InvalidDataException>(() => VdfParser.Parse(input));

    [Fact]
    public async Task DownloadValidatesChecksumAndCleansCorruption()
    {
        var bytes = Encoding.UTF8.GetBytes("trusted asset");
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
        var downloader = new ManifestDownloader(client);
        Assert.Equal(Profile, ManifestDownloader.LoadManifest(JsonSerializer.Serialize(Profile)));
        var valid = Dir("valid");
        var path = await downloader.DownloadVerifiedAssetAsync("https://example.com/file.zip", Hash(bytes).ToLowerInvariant(), valid);
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Single(Directory.GetFiles(valid));
        var invalid = Dir("invalid");
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadVerifiedAssetAsync("https://example.com/file.zip", new string('0', 64), invalid));
        Assert.Empty(Directory.GetFileSystemEntries(invalid));
        await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadVerifiedAssetAsync("https://example.com", "bad", invalid));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("..\\evil.dll")]
    [InlineData("/evil.dll")]
    [InlineData("C:/evil.dll")]
    [InlineData("safe/../../evil.dll")]
    [InlineData(".. /evil.dll")]
    [InlineData("safe./evil.dll")]
    public void ZipSlipIsRejectedBeforeExtraction(string entry)
    {
        var zip = Path.Combine(root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { archive.CreateEntry("safe.dll"); archive.CreateEntry(entry); }
        using var client = new HttpClient();
        var destination = Dir("extract");
        Assert.Throws<InvalidDataException>(() => new ManifestDownloader(client).ExtractZipSafely(zip, destination));
        Assert.Empty(Directory.GetFileSystemEntries(destination)); Assert.False(File.Exists(Path.Combine(root, "evil.dll")));
    }

    [Fact]
    public void SafeZipExtractsNestedFiles()
    {
        var zip = Path.Combine(root, "good.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("folder/");
            using var writer = new StreamWriter(archive.CreateEntry("folder/plugin.dll").Open()); writer.Write("plugin");
        }
        using var client = new HttpClient(); var destination = Dir("extract");
        new ManifestDownloader(client).ExtractZipSafely(zip, destination);
        Assert.Equal("plugin", File.ReadAllText(Path.Combine(destination, "folder", "plugin.dll")));
    }

    [Fact]
    public async Task PatchAndFreshManagerRollbackRestoreExactTreeAndHashes()
    {
        var game = Dir("game"); var stage = Dir("stage");
        Directory.CreateDirectory(Path.Combine(game, "existing", "empty"));
        File.WriteAllText(Path.Combine(game, "guigubahuang.exe"), "original exe");
        File.WriteAllText(Path.Combine(game, "existing", "config.ini"), "original config");
        File.WriteAllText(Path.Combine(game, "untouched.dat"), "untouched");
        var before = Snapshot(game); var directories = Directory.GetDirectories(game, "*", SearchOption.AllDirectories).Order().ToArray();
        File.WriteAllText(Path.Combine(stage, "guigubahuang.exe"), "patched exe");
        Directory.CreateDirectory(Path.Combine(stage, "existing"));
        File.WriteAllText(Path.Combine(stage, "existing", "config.ini"), "patched config");
        Directory.CreateDirectory(Path.Combine(stage, "plugins", "nested", "empty"));
        File.WriteAllText(Path.Combine(stage, "plugins", "nested", "plugin.dll"), "new plugin");
        var manager = new PatchTransactionManager();
        var record = await manager.ApplyPatchAsync(game, stage);
        Assert.True(manager.IsPatchApplied(game)); Assert.Single(record.AddedRelativePaths); Assert.Equal(2, record.BackedUpRelativePaths.Count);
        Assert.Equal("patched exe", File.ReadAllText(Path.Combine(game, "guigubahuang.exe")));
        Assert.Equal("new plugin", File.ReadAllText(Path.Combine(game, "plugins", "nested", "plugin.dll")));
        Assert.Equal("original exe", File.ReadAllText(Path.Combine(game, ".daostudio-backup", "files", "guigubahuang.exe")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ApplyPatchAsync(game, stage));
        Assert.True(await new PatchTransactionManager().RollbackAsync(game));
        Assert.Equal(before.OrderBy(p => p.Key), Snapshot(game).OrderBy(p => p.Key));
        Assert.Equal(directories, Directory.GetDirectories(game, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.False(manager.IsPatchApplied(game)); Assert.False(await manager.RollbackAsync(game));
    }

    [Fact]
    public async Task TamperedRollbackTraversalIsRejectedWithoutMutation()
    {
        var game = Dir("game"); var stage = Dir("stage"); File.WriteAllText(Path.Combine(stage, "new.dll"), "new");
        var manager = new PatchTransactionManager(); await manager.ApplyPatchAsync(game, stage);
        var manifest = Path.Combine(game, ".daostudio-backup", "backup-manifest.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("new.dll", "../outside.dll"));
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.RollbackAsync(game));
        Assert.True(File.Exists(Path.Combine(game, "new.dll")));
    }

    [Fact]
    public async Task CancelledPatchLeavesOriginalTree()
    {
        var game = Dir("game"); var stage = Dir("stage"); File.WriteAllText(Path.Combine(stage, "new.dll"), "new");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PatchTransactionManager().ApplyPatchAsync(game, stage, ct: cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(game));
    }

    [Theory]
    [InlineData(null, false)] [InlineData("", false)] [InlineData(" \t\n", false)]
    [InlineData("123!?。・", false)] [InlineData("Hello", false)]
    [InlineData("你好", true)] [InlineData("ひらがな", true)] [InlineData("カタカナ", true)] [InlineData("한글", true)] [InlineData("𠀀", true)]
    public void FilterRecognizesCjk(string? text, bool expected) => Assert.Equal(expected, TextInterceptorFilter.ShouldTranslate(text));

    [Theory]
    [InlineData("StartButton", UiHint.Button)] [InlineData("NpcDialogue", UiHint.Dialogue)]
    [InlineData("Tooltip", UiHint.Tooltip)] [InlineData("InventoryItem", UiHint.ItemName)]
    public void FilterMapsContext(string context, UiHint expected) => Assert.Equal(expected, TextInterceptorFilter.InferUiHint("你好", context));

    [Fact]
    public async Task BridgeUsesProtocolAndSessionCacheWithoutWritingFiles()
    {
        var sessionFile = Path.Combine(root, "session.json"); File.WriteAllText(sessionFile, "{\"port\":12345,\"token\":\"secret\"}");
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        var count = 0;
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            count++;
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("http://127.0.0.1:12345/bridge/translate", request.RequestUri!.ToString());
            Assert.Equal("secret", request.Headers.GetValues("X-Bridge-Token").Single());
            var payload = await request.Content!.ReadFromJsonAsync<BridgeTranslateRequest>(cancellationToken: ct);
            Assert.Equal("你好", payload!.SourceText); Assert.Equal("button", payload.UiHint);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new BridgeTranslateResponse("你好", "Olá", "hash", "L1", false)) };
        }));
        var bridge = new BridgeLoopbackClient(client, sessionFile);
        Assert.Equal("Olá", await bridge.TranslateAsync("你好")); Assert.Equal("Olá", await bridge.TranslateAsync("你好")); Assert.Equal(1, count); Assert.Equal(1, bridge.CachedCount);
        Assert.Equal("Hello", await bridge.TranslateAsync("Hello")); Assert.Equal(1, count);
        await bridge.TranslateAsync("你好", glossaryVersion: 2); Assert.Equal(2, count);
        await new BridgeLoopbackClient(client, sessionFile).TranslateAsync("你好"); Assert.Equal(3, count);
        Assert.Equal(before, Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(root, "*.txt", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("timeout")] [InlineData("http")] [InlineData("json")] [InlineData("exception")] [InlineData("fallback")]
    public async Task BridgeFallsBackAndDoesNotCacheFailures(string failure)
    {
        using var client = new HttpClient(new Handler(async (_, ct) =>
        {
            if (failure == "timeout") await Task.Delay(5000, ct);
            if (failure == "exception") throw new HttpRequestException("offline");
            return new HttpResponseMessage(failure == "http" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            { Content = failure == "fallback" ? JsonContent.Create(new BridgeTranslateResponse("你好", "你好", "", "", true)) : new StringContent("invalid json") };
        }));
        var bridge = new BridgeLoopbackClient(client, new BridgeSession(12345, "secret"), TimeSpan.FromMilliseconds(30));
        Assert.Equal("你好", await bridge.TranslateAsync("你好")); Assert.Equal(0, bridge.CachedCount);
        Assert.Empty(Directory.GetFiles(root, "*.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public void SessionEnvironmentOverridesFileAndInvalidSessionFailsClosed()
    {
        var oldPort = Environment.GetEnvironmentVariable("DAOSTUDIO_BRIDGE_PORT");
        var oldToken = Environment.GetEnvironmentVariable("DAOSTUDIO_BRIDGE_TOKEN");
        try
        {
            var file = Path.Combine(root, "session.json");
            File.WriteAllText(file, "{\"port\":1111,\"token\":\"file-token\"}");
            Environment.SetEnvironmentVariable("DAOSTUDIO_BRIDGE_PORT", "2222");
            Environment.SetEnvironmentVariable("DAOSTUDIO_BRIDGE_TOKEN", "env-token");
            Assert.Equal(new BridgeSession(2222, "env-token"), BridgeSession.Load(file));
            Environment.SetEnvironmentVariable("DAOSTUDIO_BRIDGE_PORT", "99999");
            Assert.Null(BridgeSession.Load(file));
            Environment.SetEnvironmentVariable("DAOSTUDIO_BRIDGE_PORT", null);
            Environment.SetEnvironmentVariable("DAOSTUDIO_BRIDGE_TOKEN", null);
            Assert.Equal(new BridgeSession(1111, "file-token"), BridgeSession.Load(file));
            File.WriteAllText(file, "broken"); Assert.Null(BridgeSession.Load(file));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DAOSTUDIO_BRIDGE_PORT", oldPort);
            Environment.SetEnvironmentVariable("DAOSTUDIO_BRIDGE_TOKEN", oldToken);
        }
    }

    [Fact]
    public async Task ReservedBackupAndOverlappingStagingAreRejected()
    {
        var game = Dir("game"); var stage = Dir("stage");
        Directory.CreateDirectory(Path.Combine(stage, ".daostudio-backup"));
        var manager = new PatchTransactionManager();
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.ApplyPatchAsync(game, stage));
        Assert.Empty(Directory.GetFileSystemEntries(game));
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.ApplyPatchAsync(game, game));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}

