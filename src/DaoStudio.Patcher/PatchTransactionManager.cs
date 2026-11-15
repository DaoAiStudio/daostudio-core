using System.Text.Json;
using DaoStudio.Patcher.Abstractions;

namespace DaoStudio.Patcher;

public sealed class PatchTransactionManager : IPatchTransactionManager
{
    private const string BackupName = ".daostudio-backup";
    public sealed record OriginalFile(string RelativePath, DateTime LastWriteUtc, FileAttributes Attributes);
    public sealed record BackupManifest(PatchTransactionRecord Transaction, string[] CreatedDirectories, OriginalFile[] Originals);
    public bool IsPatchApplied(string gameDirectory) => File.Exists(SafePath.Resolve(gameDirectory, BackupName + "/backup-manifest.json"));

    public async Task<PatchTransactionRecord> ApplyPatchAsync(string gameDirectory, string stagedFilesDirectory, string gameId = "guigubahuang", CancellationToken ct = default)
    {
        gameDirectory = Path.GetFullPath(gameDirectory);
        stagedFilesDirectory = Path.GetFullPath(stagedFilesDirectory);
        SafePath.RejectLinks(gameDirectory);
        SafePath.RejectLinks(stagedFilesDirectory);
        if (!Directory.Exists(gameDirectory) || !Directory.Exists(stagedFilesDirectory)) throw new DirectoryNotFoundException();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (gameDirectory.Equals(stagedFilesDirectory, comparison) || stagedFilesDirectory.StartsWith(gameDirectory + Path.DirectorySeparatorChar, comparison) || gameDirectory.StartsWith(stagedFilesDirectory + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Staging and game directories must be disjoint.");
        var backup = SafePath.Resolve(gameDirectory, BackupName);
        if (Directory.Exists(backup) || File.Exists(backup)) throw new InvalidOperationException("Backup already exists.");
        var files = new List<string>();
        var directories = new List<string>();
        void Scan(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                SafePath.RejectLinks(path);
                var relative = Path.GetRelativePath(stagedFilesDirectory, path);
                if (relative.Split(Path.DirectorySeparatorChar)[0].Equals(BackupName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Reserved backup path.");
                SafePath.Resolve(gameDirectory, relative);
                if (Directory.Exists(path)) { directories.Add(relative); Scan(path); } else files.Add(relative);
            }
        }
        Scan(stagedFilesDirectory);
        var added = new List<string>();
        var saved = new Dictionary<string, string>();
        var originals = new List<OriginalFile>();
        foreach (var relative in files)
        {
            var target = SafePath.Resolve(gameDirectory, relative);
            if (Directory.Exists(target)) throw new IOException("File conflicts with directory.");
            if (File.Exists(target))
            {
                saved.Add(relative, "files/" + relative.Replace('\\', '/'));
                originals.Add(new(relative, File.GetLastWriteTimeUtc(target), File.GetAttributes(target)));
            }
            else added.Add(relative);
        }
        foreach (var relative in directories) if (File.Exists(SafePath.Resolve(gameDirectory, relative))) throw new IOException("Directory conflicts with file.");
        var created = directories.Where(p => !Directory.Exists(SafePath.Resolve(gameDirectory, p))).ToArray();
        var record = new PatchTransactionRecord(gameId, gameDirectory, DateTimeOffset.UtcNow, added, saved);
        var manifest = new BackupManifest(record, created, originals.ToArray());
        var preparation = SafePath.Resolve(gameDirectory, ".daostudio-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(preparation, "files"));
            foreach (var pair in saved)
            {
                ct.ThrowIfCancellationRequested();
                var destination = SafePath.Resolve(preparation, pair.Value);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(SafePath.Resolve(gameDirectory, pair.Key), destination);
            }
            await File.WriteAllTextAsync(Path.Combine(preparation, "backup-manifest.json"), JsonSerializer.Serialize(manifest), ct);
            ct.ThrowIfCancellationRequested();
            Directory.Move(preparation, backup); // Publish the complete backup atomically before touching game files.
        }
        finally { if (Directory.Exists(preparation)) Directory.Delete(preparation, true); }
        try
        {
            foreach (var relative in directories) { ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(SafePath.Resolve(gameDirectory, relative)); }
            foreach (var relative in files)
            {
                ct.ThrowIfCancellationRequested();
                File.Copy(SafePath.Resolve(stagedFilesDirectory, relative), SafePath.Resolve(gameDirectory, relative), true);
            }
            return record;
        }
        catch { await RollbackAsync(gameDirectory, CancellationToken.None); throw; }
    }

    public async Task<bool> RollbackAsync(string gameDirectory, CancellationToken ct = default)
    {
        var backup = SafePath.Resolve(gameDirectory, BackupName);
        var manifestPath = SafePath.Resolve(backup, "backup-manifest.json");
        if (!File.Exists(manifestPath)) return false;
        var manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestPath, ct)) ?? throw new InvalidDataException("Invalid backup manifest.");
        string Target(string relative)
        {
            if (relative.Replace('\\', '/').Split('/')[0].Equals(BackupName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Reserved backup path.");
            return SafePath.Resolve(gameDirectory, relative);
        }
        // Preflight all persisted paths and backups before changing anything.
        foreach (var pair in manifest.Transaction.BackedUpRelativePaths)
        {
            Target(pair.Key);
            var source = SafePath.Resolve(backup, pair.Value);
            SafePath.Resolve(Path.Combine(backup, "files"), Path.GetRelativePath(Path.Combine(backup, "files"), source));
            if (!File.Exists(source)) throw new InvalidDataException("Missing original backup.");
        }
        foreach (var path in manifest.Transaction.AddedRelativePaths.Concat(manifest.CreatedDirectories).Concat(manifest.Originals.Select(o => o.RelativePath))) Target(path);
        ct.ThrowIfCancellationRequested();
        // Once restoration starts, finish it even if the caller cancels.
        foreach (var pair in manifest.Transaction.BackedUpRelativePaths)
        {
            var target = Target(pair.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
            File.Copy(SafePath.Resolve(backup, pair.Value), target, true);
        }
        foreach (var path in manifest.Transaction.AddedRelativePaths)
        {
            var target = Target(path);
            if (File.Exists(target)) { File.SetAttributes(target, FileAttributes.Normal); File.Delete(target); }
        }
        foreach (var original in manifest.Originals)
        {
            var target = Target(original.RelativePath);
            File.SetLastWriteTimeUtc(target, original.LastWriteUtc);
            File.SetAttributes(target, original.Attributes);
        }
        foreach (var path in manifest.CreatedDirectories.OrderByDescending(p => p.Length))
            if (Directory.Exists(Target(path))) Directory.Delete(Target(path), false);
        foreach (var file in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories)) { SafePath.RejectLinks(file); File.SetAttributes(file, FileAttributes.Normal); }
        Directory.Delete(backup, true);
        return true;
    }
}
