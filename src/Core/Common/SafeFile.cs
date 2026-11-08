namespace X04G.Core.Common;

/// <summary>
/// File helpers that enforce the toolkit's safety rules:
/// inputs are only ever opened read-only, outputs are only ever created new (never overwritten).
/// </summary>
public static class SafeFile
{
    public static FileStream OpenReadOnly(string path)
    {
        if (!File.Exists(path)) throw new ToolkitException($"Input file not found: {path}");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
    }

    /// <summary>Creates a brand-new file. Throws <see cref="OutputExistsException"/> if anything already exists at the path.</summary>
    public static FileStream CreateNew(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            throw new OutputExistsException(path);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        try
        {
            return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
        }
        catch (IOException) when (File.Exists(path))
        {
            throw new OutputExistsException(path);
        }
    }

    /// <summary>Returns a path in <paramref name="directory"/> that does not exist yet: name.ext, name_1.ext, name_2.ext ...</summary>
    public static string UniquePath(string directory, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = Path.Combine(directory, fileName);
        for (int i = 1; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(directory, $"{stem}_{i}{ext}");
        return candidate;
    }

    /// <summary>Returns a new, not-yet-existing directory path such as X04G_GSI_20261108_221500.</summary>
    public static string UniqueDirectory(string parent, string prefix, DateTime? now = null)
    {
        var stamp = (now ?? DateTime.Now).ToString("yyyyMMdd_HHmmss");
        var candidate = Path.Combine(parent, $"{prefix}_{stamp}");
        for (int i = 1; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(parent, $"{prefix}_{stamp}_{i}");
        return candidate;
    }

    public static bool IsSameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                      Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                      OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>True when <paramref name="path"/> is <paramref name="directory"/> or inside it.</summary>
    public static bool IsInside(string path, string directory)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var p = Path.GetFullPath(path);
        var d = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return p.StartsWith(d, cmp) || IsSameFile(path, directory);
    }

    /// <summary>Free space on the volume holding <paramref name="path"/> (walks up to the first existing directory).</summary>
    public static long? AvailableFreeSpace(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dir = full;
            while (!Directory.Exists(dir))
            {
                var parent = Path.GetDirectoryName(dir);
                if (parent == null || parent == dir) return null;
                dir = parent;
            }
            // DriveInfo picks the mount point by root on Windows; on Unix we find the longest matching mount.
            if (OperatingSystem.IsWindows())
                return new DriveInfo(Path.GetPathRoot(dir)!).AvailableFreeSpace;
            DriveInfo? best = null;
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    var root = d.RootDirectory.FullName;
                    if (IsInside(dir, root) && (best == null || root.Length > best.RootDirectory.FullName.Length)) best = d;
                }
                catch { /* ignore inaccessible mounts */ }
            }
            return best?.AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }
}

public class ToolkitException : Exception
{
    public ToolkitException(string message) : base(message) { }
    public ToolkitException(string message, Exception inner) : base(message, inner) { }
}

public sealed class OutputExistsException : ToolkitException
{
    public string PathThatExists { get; }
    public OutputExistsException(string path)
        : base($"Refusing to overwrite existing file: {path}") => PathThatExists = path;
}

public sealed class InvalidImageException : ToolkitException
{
    public InvalidImageException(string message) : base(message) { }
}

public static class ByteSize
{
    public static string Format(long bytes) => Format((ulong)Math.Max(0, bytes), bytes < 0);
    public static string Format(ulong bytes) => Format(bytes, false);

    private static string Format(ulong bytes, bool negative)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        var s = u == 0 ? $"{bytes} B" : $"{v:0.##} {units[u]}";
        return (negative ? "-" : "") + s;
    }

    public static string WithExact(ulong bytes) => $"{Format(bytes)} ({bytes:N0} bytes)";
    public static string WithExact(long bytes) => $"{Format(bytes)} ({bytes:N0} bytes)";

    public static ulong AlignUp(ulong value, ulong alignment) =>
        alignment == 0 ? value : (value + alignment - 1) / alignment * alignment;
}
