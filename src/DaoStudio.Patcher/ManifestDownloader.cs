using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DaoStudio.Contracts;
using DaoStudio.Patcher.Abstractions;

namespace DaoStudio.Patcher;

public sealed class ManifestDownloader : IManifestDownloader
{
    private readonly HttpClient client;
    public ManifestDownloader(HttpClient client) => this.client = client;
    public static GameProfileManifest LoadManifest(string json) => JsonSerializer.Deserialize<GameProfileManifest>(json) ?? throw new InvalidDataException("Empty manifest.");
    public static async Task<GameProfileManifest> LoadManifestAsync(string path, CancellationToken ct = default) => LoadManifest(await File.ReadAllTextAsync(path, ct));

    public async Task<string> DownloadVerifiedAssetAsync(string url, string expectedSha256Hex, string destinationDirectory, CancellationToken ct = default)
    {
        if (expectedSha256Hex == null || expectedSha256Hex.Length != 64 || !expectedSha256Hex.All(Uri.IsHexDigit)) throw new ArgumentException("Expected SHA-256 must contain 64 hex digits.", nameof(expectedSha256Hex));
        SafePath.RejectLinks(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var target = SafePath.Resolve(destinationDirectory, expectedSha256Hex.ToLowerInvariant() + ".asset");
        var temp = SafePath.Resolve(destinationDirectory, Guid.NewGuid() + ".tmp");
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await response.Content.CopyToAsync(stream, ct);
            await using (var stream = File.OpenRead(temp))
            {
                var actual = await SHA256.HashDataAsync(stream, ct);
                if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedSha256Hex))) throw new InvalidDataException("SHA-256 checksum mismatch.");
            }
            File.Move(temp, target, true);
            return target;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void ExtractZipSafely(string zipFilePath, string destinationDirectory)
    {
        using var archive = ZipFile.OpenRead(zipFilePath);
        // Validate the entire archive before creating any output.
        var entries = archive.Entries.Select(entry => (Entry: entry, Path: SafePath.Resolve(destinationDirectory, entry.FullName))).ToArray();
        foreach (var entry in entries)
        {
            if (((entry.Entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("ZIP symbolic link rejected.");
        }
        foreach (var entry in entries)
        {
            SafePath.Resolve(destinationDirectory, entry.Entry.FullName);
            if (entry.Entry.FullName.EndsWith('/') || entry.Entry.FullName.EndsWith('\\')) Directory.CreateDirectory(entry.Path);
            else { Directory.CreateDirectory(Path.GetDirectoryName(entry.Path)!); entry.Entry.ExtractToFile(entry.Path, true); }
        }
    }
}
