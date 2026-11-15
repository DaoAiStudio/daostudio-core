using DaoStudio.Contracts;

namespace DaoStudio.Patcher.Abstractions;

public sealed record DetectedGameInstall(
    string GameId,
    string SteamAppId,
    string InstallDirectory,
    string ExecutablePath,
    bool IsExecutablePresent);

public interface ISteamLocator
{
    Task<DetectedGameInstall?> FindGameAsync(GameProfileManifest manifest, CancellationToken ct = default);
}

public interface IManifestDownloader
{
    Task<string> DownloadVerifiedAssetAsync(
        string url,
        string expectedSha256Hex,
        string destinationDirectory,
        CancellationToken ct = default);

    void ExtractZipSafely(string zipFilePath, string destinationDirectory);
}

public sealed record PatchTransactionRecord(
    string GameId,
    string GameDirectory,
    DateTimeOffset AppliedAtUtc,
    IReadOnlyList<string> AddedRelativePaths,
    IReadOnlyDictionary<string, string> BackedUpRelativePaths);

public interface IPatchTransactionManager
{
    Task<PatchTransactionRecord> ApplyPatchAsync(
        string gameDirectory,
        string stagedFilesDirectory,
        string gameId = "guigubahuang",
        CancellationToken ct = default);

    Task<bool> RollbackAsync(string gameDirectory, CancellationToken ct = default);
    bool IsPatchApplied(string gameDirectory);
}
