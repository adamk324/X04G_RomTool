using System.Diagnostics;
using System.Text;
using X04G.Core.Common;

namespace X04G.Core.ImageProcessing;

public sealed record ToolRunResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// e2fsck/resize2fs provider. Shrinking ext4 (resize2fs -M), as the original scripts do, is the only
/// operation the toolkit does not implement natively, because a filesystem resizer is far too risky to
/// re-implement. It is optional and always runs on a *copy* inside the output folder.
/// </summary>
public interface IExt4Toolchain
{
    string Name { get; }
    /// <summary>Checks availability. Returns null if usable, otherwise a human-readable reason.</summary>
    string? Probe();
    ToolRunResult Run(string tool, IReadOnlyList<string> args, IReadOnlyList<int> pathArgIndexes, CancellationToken ct);
}

public static class Ext4Tools
{
    /// <summary>e2fsck -fy: exit codes 0-3 mean the filesystem is consistent (possibly after fixes).</summary>
    public static void Check(IExt4Toolchain tc, string imagePath, OperationLog log, CancellationToken ct)
    {
        var r = tc.Run("e2fsck", new[] { "-fy", imagePath }, new[] { 1 }, ct);
        log.Debug($"e2fsck output:\n{r.StdOut}{r.StdErr}".TrimEnd());
        if (r.ExitCode >= 4)
            throw new ToolkitException($"e2fsck reported uncorrected errors (exit code {r.ExitCode}) on {Path.GetFileName(imagePath)}. The image is not modified further. Details are in the log.");
        if (r.ExitCode is 1 or 2 or 3) log.Warn($"e2fsck corrected filesystem errors in the working copy {Path.GetFileName(imagePath)} (exit code {r.ExitCode}).");
    }

    /// <summary>Same sequence as the original scripts: e2fsck -yf; resize2fs -M; e2fsck -yf. Operates in place on a working copy.</summary>
    public static void ShrinkToMinimum(IExt4Toolchain tc, string workingCopy, OperationLog log, CancellationToken ct)
    {
        Check(tc, workingCopy, log, ct);
        var r = tc.Run("resize2fs", new[] { "-M", workingCopy }, new[] { 1 }, ct);
        log.Debug($"resize2fs output:\n{r.StdOut}{r.StdErr}".TrimEnd());
        if (r.ExitCode != 0) throw new ToolkitException($"resize2fs -M failed (exit code {r.ExitCode}) on {Path.GetFileName(workingCopy)}: {FirstLine(r.StdErr + r.StdOut)}");
        Check(tc, workingCopy, log, ct);

        // resize2fs truncates regular files; make sure file length equals the filesystem size.
        long fsBytes;
        using (var fs = new FileStream(workingCopy, FileMode.Open, FileAccess.Read, FileShare.Read))
            fsBytes = (long)new Ext4Image(fs).FilesystemBytes;
        var len = new FileInfo(workingCopy).Length;
        if (len > fsBytes)
        {
            using var w = new FileStream(workingCopy, FileMode.Open, FileAccess.Write, FileShare.None);
            w.SetLength(fsBytes);
            log.Info($"Trimmed {Path.GetFileName(workingCopy)} to the filesystem size {fsBytes:N0} bytes.");
        }
        else if (len < fsBytes) throw new ToolkitException($"{Path.GetFileName(workingCopy)} is shorter than its filesystem after resizing.");
    }

    private static string FirstLine(string s) => s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("resize2fs 1.")) ?? "no output";

    public static ToolRunResult RunProcess(string exe, IEnumerable<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        var so = new StringBuilder(); var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (so) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (se) se.AppendLine(e.Data); };
        try { p.Start(); }
        catch (Exception ex) { throw new ToolkitException($"Could not start '{exe}': {ex.Message}", ex); }
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromHours(2));
        while (!p.WaitForExit(200))
        {
            if (ct.IsCancellationRequested || DateTime.UtcNow > deadline)
            {
                try { p.Kill(true); } catch { }
                ct.ThrowIfCancellationRequested();
                throw new ToolkitException($"'{exe}' timed out.");
            }
        }
        p.WaitForExit();
        return new ToolRunResult(p.ExitCode, so.ToString().Replace("\0", ""), se.ToString().Replace("\0", ""));
    }
}

/// <summary>e2fsprogs executables available directly (Windows builds placed in tools\e2fsprogs, or e2fsprogs on Linux).</summary>
public sealed class NativeExt4Toolchain : IExt4Toolchain
{
    private readonly string _dir;
    public NativeExt4Toolchain(string directory) => _dir = directory;
    public string Name => $"e2fsprogs in {_dir}";

    private string Exe(string tool)
    {
        var win = Path.Combine(_dir, tool + ".exe");
        return File.Exists(win) ? win : Path.Combine(_dir, tool);
    }

    public string? Probe()
    {
        foreach (var t in new[] { "e2fsck", "resize2fs" })
            if (!File.Exists(Exe(t))) return $"{t} not found in {_dir}";
        try
        {
            var r = Ext4Tools.RunProcess(Exe("resize2fs"), Array.Empty<string>(), CancellationToken.None, TimeSpan.FromSeconds(20));
            if (!(r.StdOut + r.StdErr).Contains("resize2fs")) return "resize2fs did not respond as expected";
        }
        catch (Exception ex) { return ex.Message; }
        return null;
    }

    public ToolRunResult Run(string tool, IReadOnlyList<string> args, IReadOnlyList<int> pathArgIndexes, CancellationToken ct) =>
        Ext4Tools.RunProcess(Exe(tool), args, ct);
}

/// <summary>Runs e2fsprogs inside the default WSL2 distribution (wsl.exe -e ...). Windows paths are translated with wslpath.</summary>
public sealed class WslExt4Toolchain : IExt4Toolchain
{
    public string Name => "WSL (default distribution)";

    private static string WslExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

    public string? Probe()
    {
        if (!OperatingSystem.IsWindows()) return "WSL is only available on Windows";
        if (!File.Exists(WslExe)) return "wsl.exe not found (WSL is not installed)";
        try
        {
            var r = Ext4Tools.RunProcess(WslExe, new[] { "-e", "sh", "-c", "command -v e2fsck && command -v resize2fs" }, CancellationToken.None, TimeSpan.FromSeconds(60));
            if (r.ExitCode != 0)
                return "WSL is present but e2fsck/resize2fs were not found in the default distribution (install e2fsprogs, e.g. 'sudo apt install e2fsprogs'), or no distribution is installed.";
        }
        catch (Exception ex) { return ex.Message; }
        return null;
    }

    public static string ToWslPath(string windowsPath, CancellationToken ct)
    {
        var r = Ext4Tools.RunProcess(WslExe, new[] { "-e", "wslpath", "-a", Path.GetFullPath(windowsPath) }, ct, TimeSpan.FromSeconds(60));
        var p = r.StdOut.Trim();
        if (r.ExitCode != 0 || !p.StartsWith('/')) throw new ToolkitException($"wslpath could not translate '{windowsPath}': {r.StdErr.Trim()}");
        return p;
    }

    public ToolRunResult Run(string tool, IReadOnlyList<string> args, IReadOnlyList<int> pathArgIndexes, CancellationToken ct)
    {
        var a = args.ToList();
        foreach (var idx in pathArgIndexes) a[idx] = ToWslPath(a[idx], ct);
        var full = new List<string> { "-e", tool };
        full.AddRange(a);
        return Ext4Tools.RunProcess(WslExe, full, ct);
    }
}

public static class Ext4ToolchainLocator
{
    /// <summary>Finds a usable toolchain: bundled tools\e2fsprogs next to the app, then WSL, then (non-Windows) /usr/sbin.</summary>
    public static (IExt4Toolchain? Toolchain, List<string> Notes) Locate(string? appDirectory = null)
    {
        var notes = new List<string>();
        var dirs = new List<string>();
        if (appDirectory != null) dirs.Add(Path.Combine(appDirectory, "tools", "e2fsprogs"));
        if (!OperatingSystem.IsWindows()) dirs.AddRange(new[] { "/usr/sbin", "/sbin", "/usr/bin" });
        foreach (var d in dirs)
        {
            if (!Directory.Exists(d)) continue;
            var tc = new NativeExt4Toolchain(d);
            var why = tc.Probe();
            if (why == null) return (tc, notes);
            notes.Add($"{tc.Name}: {why}");
        }
        if (OperatingSystem.IsWindows())
        {
            var w = new WslExt4Toolchain();
            var why = w.Probe();
            if (why == null) return (w, notes);
            notes.Add($"WSL: {why}");
        }
        return (null, notes);
    }
}
