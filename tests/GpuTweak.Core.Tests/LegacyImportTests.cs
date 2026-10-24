namespace GpuTweak.Core.Tests;

public sealed class LegacyImportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("legacyimport").FullName;
    private string Exe => Path.Combine(_root, "exe");
    private string Data => Path.Combine(_root, "exe", DataPaths.FolderName);
    private string Local => Path.Combine(_root, "local");
    private string Roaming => Path.Combine(_root, "roaming");

    public LegacyImportTests()
    {
        Directory.CreateDirectory(Exe);
        Directory.CreateDirectory(Local);
        Directory.CreateDirectory(Roaming);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void CopiesTweakstoneAndRgbstoneSettingsButNotSessionFiles()
    {
        var gpu = Path.Combine(Exe, "GpuTweakData");
        Write(Path.Combine(gpu, "profiles.json"), "p");
        Write(Path.Combine(gpu, "settings.json"), "s");
        Write(Path.Combine(gpu, "logos", "nvidia.png"), "l");
        Write(Path.Combine(gpu, "session.lock"), "x");
        Write(Path.Combine(gpu, "autotune.json"), "x");
        Write(Path.Combine(Roaming, "rgbstone", "rgbstone.json"), "r");

        var imported = DataPaths.ImportLegacy(Data, Exe, Local, Roaming);

        Assert.Equal(2, imported.Count);
        Assert.Equal("p", File.ReadAllText(Path.Combine(Data, "profiles.json")));
        Assert.Equal("s", File.ReadAllText(Path.Combine(Data, "settings.json")));
        Assert.Equal("l", File.ReadAllText(Path.Combine(Data, "logos", "nvidia.png")));
        Assert.Equal("r", File.ReadAllText(Path.Combine(Data, "rgbstone.json")));
        Assert.False(File.Exists(Path.Combine(Data, "session.lock")));
        Assert.False(File.Exists(Path.Combine(Data, "autotune.json")));
        // the old folders are left as they were
        Assert.True(File.Exists(Path.Combine(gpu, "session.lock")));
    }

    [Fact]
    public void PrefersPortableFolderOverAppData()
    {
        Write(Path.Combine(Exe, "rgbstone.json"), "portable");
        Write(Path.Combine(Roaming, "rgbstone", "rgbstone.json"), "appdata");
        Write(Path.Combine(Local, "GpuTweak", "profiles.json"), "local");

        DataPaths.ImportLegacy(Data, Exe, Local, Roaming);

        Assert.Equal("portable", File.ReadAllText(Path.Combine(Data, "rgbstone.json")));
        Assert.Equal("local", File.ReadAllText(Path.Combine(Data, "profiles.json")));
    }

    [Fact]
    public void RunsOnlyOnceAndNeverOverwrites()
    {
        Write(Path.Combine(Data, "rgbstone.json"), "new");
        Write(Path.Combine(Exe, "rgbstone.json"), "old");
        Assert.Empty(DataPaths.ImportLegacy(Data, Exe, Local, Roaming));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Data, "rgbstone.json")));

        File.Delete(Path.Combine(Data, "rgbstone.json"));
        Assert.Empty(DataPaths.ImportLegacy(Data, Exe, Local, Roaming));
        Assert.False(File.Exists(Path.Combine(Data, "rgbstone.json")));
    }
}
