namespace GpuTweak.Core;

public static class DataPaths
{
    public const string FolderName = "tweakandrgbstoneData";

    /// <summary>
    /// Portable mode: a "tweakandrgbstoneData" folder next to the exe. Falls back to %LOCALAPPDATA%
    /// when the exe sits somewhere read-only (e.g. Program Files or a locked USB stick).
    /// </summary>
    public static string Resolve()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, FolderName);
        try
        {
            Directory.CreateDirectory(portable);
            var probe = Path.Combine(portable, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return portable;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tweakandrgbstone");
        }
    }

    /// <summary>Files of a running or interrupted session; copying them would make the new app think it crashed or resume an old test.</summary>
    private static readonly string[] SessionFiles = { "session.lock", "autotune.json" };

    /// <summary>
    /// First run only: copies the settings of the separate Tweakstone and rgbstone apps into the data folder.
    /// The old folders are only read, never changed. Returns what was imported, for the log.
    /// </summary>
    public static List<string> ImportLegacy(string dataDirectory, string exeDirectory, string? localAppData = null, string? roamingAppData = null)
    {
        var imported = new List<string>();
        Directory.CreateDirectory(dataDirectory);
        var marker = Path.Combine(dataDirectory, ".legacy-imported");
        if (File.Exists(marker)) return imported;

        var local = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = roamingAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var gpu = new[] { Path.Combine(exeDirectory, "GpuTweakData"), Path.Combine(local, "GpuTweak") }
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "settings.json")) || File.Exists(Path.Combine(d, "profiles.json")));
        if (gpu is not null && !File.Exists(Path.Combine(dataDirectory, "profiles.json")))
        {
            CopyTree(gpu, dataDirectory);
            imported.Add(gpu);
        }

        var rgb = new[] { Path.Combine(exeDirectory, "rgbstone.json"), Path.Combine(roaming, "rgbstone", "rgbstone.json") }
            .FirstOrDefault(File.Exists);
        var rgbTarget = Path.Combine(dataDirectory, "rgbstone.json");
        if (rgb is not null && !File.Exists(rgbTarget))
        {
            File.Copy(rgb, rgbTarget);
            imported.Add(rgb);
        }

        File.WriteAllText(marker, string.Join(Environment.NewLine, imported));
        return imported;
    }

    /// <summary>
    /// The data folder sits next to the exe, so a new version opened from another folder (Downloads, a new folder on the
    /// desktop) starts with an empty profile list. When this data folder has no profiles yet, the newest profiles.json of
    /// another copy (desktop, downloads, documents and their direct subfolders, %LOCALAPPDATA%) is copied in. The other copy
    /// is only read. Returns the file that was copied, or null.
    /// </summary>
    public static string? ImportProfilesFromOtherCopy(string dataDirectory, IEnumerable<string>? roots = null)
    {
        var target = Path.Combine(dataDirectory, "profiles.json");
        // An empty list ("[]") counts as no profiles.
        if (File.Exists(target) && new FileInfo(target).Length > 2) return null;
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        roots ??= new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), // may be redirected to OneDrive
            Path.Combine(user, "Desktop"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(user, "Downloads"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tweakandrgbstone"),
        }.Where(d => !string.IsNullOrEmpty(d));
        var self = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var newest = Candidates(roots)
            .Where(f => !string.Equals(Path.GetDirectoryName(Path.GetFullPath(f)), self, StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .Where(f => f.Length > 2)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (newest is null) return null;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.Copy(newest.FullName, target, overwrite: true);
            return newest.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static IEnumerable<string> Candidates(IEnumerable<string> roots)
    {
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            // The root itself may be a data folder (%LOCALAPPDATA%\tweakandrgbstone).
            var dirs = new List<string> { root };
            try { dirs.AddRange(Directory.EnumerateDirectories(root)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            foreach (var dir in dirs.ToList())
                foreach (var name in new[] { FolderName, "GpuTweakData" })
                    dirs.Add(Path.Combine(dir, name));
            foreach (var dir in dirs)
            {
                var file = Path.Combine(dir, "profiles.json");
                if (File.Exists(file)) yield return file;
            }
        }
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            if (SessionFiles.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(to, relative);
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
