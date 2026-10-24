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
