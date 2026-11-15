namespace DaoStudio.Patcher;

internal static class SafePath
{
    internal static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("Invalid relative path.");
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is ".." or ".")) throw new InvalidDataException("Path traversal rejected.");
        if (parts.Any(p => p.EndsWith(' ') || p.EndsWith('.') || p.Any(c => c < 32)))
            throw new InvalidDataException("Ambiguous path segment rejected.");
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(parts)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Path outside root.");
        RejectLinks(fullRoot);
        var cursor = fullRoot;
        foreach (var part in parts) { cursor = Path.Combine(cursor, part); RejectLinks(cursor); }
        return full;
    }

    internal static void RejectLinks(string path)
    {
        for (var cursor = Path.GetFullPath(path); cursor != null; cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Symbolic links and reparse points are not allowed.");
    }
}

