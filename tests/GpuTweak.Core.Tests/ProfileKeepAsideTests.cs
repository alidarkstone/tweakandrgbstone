using GpuTweak.Core.Models;
using GpuTweak.Core.Profiles;

namespace GpuTweak.Core.Tests;

public sealed class ProfileKeepAsideTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gputweak-prof").FullName;
    private static readonly DateTimeOffset When = new(2026, 10, 31, 21, 5, 0, TimeSpan.Zero);
    private static readonly TuningSettings Stable = new(CoreOffsetMHz: 135, MemoryOffsetMHz: 500, MaxCoreClockMHz: 2790);
    private static readonly TuningSettings Safer = Stable with { CoreOffsetMHz = 105 };
    private const string Gpu = "NVIDIA GeForce RTX 5080";

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ProfileStore StoreWithStable()
    {
        var store = new ProfileStore(_dir);
        store.Save(new UserProfile("Otomatik UV", Gpu, Stable, IsStable: true, AutoApplyOnStart: true));
        return store;
    }

    [Fact]
    public void ProblemKeepsTheOldProfileUnderANewNameAndCreatesASaferOne()
    {
        var store = StoreWithStable();
        var kept = store.KeepAsideAfterProblem(Gpu, Stable, Safer, "ekran sürücüsü çökmesi", When);

        var aside = Assert.Single(kept);
        Assert.Equal(Stable, aside.Settings);
        Assert.True(aside.IsFailed);
        Assert.False(aside.AutoApplyOnStart);
        Assert.Equal("Otomatik UV", aside.ReplacedBy);
        Assert.StartsWith("Otomatik UV · ", aside.Name);

        var current = store.Profiles.Single(p => p.Name == "Otomatik UV");
        Assert.Equal(Safer, current.Settings);
        Assert.True(current.AutoApplyOnStart);
        Assert.Equal(2, store.Profiles.Count);
        Assert.Equal("Otomatik UV", store.GetStartupProfile(Gpu, null)!.Name);

        // Survives a restart.
        var reloaded = new ProfileStore(_dir);
        Assert.Contains(reloaded.Profiles, p => p.IsFailed && p.Settings == Stable);
    }

    [Fact]
    public void RepeatedProblemsKeepEveryStep()
    {
        var store = StoreWithStable();
        store.KeepAsideAfterProblem(Gpu, Stable, Safer, "1", When);
        var safest = Safer with { CoreOffsetMHz = 75 };
        store.KeepAsideAfterProblem(Gpu, Safer, safest, "2", When);
        Assert.Equal(3, store.Profiles.Count);
        Assert.Equal(2, store.Profiles.Count(p => p.IsFailed));
        Assert.Equal(store.Profiles.Count, store.Profiles.Select(p => p.Name).Distinct().Count());
        Assert.Equal(safest, store.Profiles.Single(p => p.Name == "Otomatik UV").Settings);
    }

    [Fact]
    public void UndoBringsTheOldProfileBack()
    {
        var store = StoreWithStable();
        store.KeepAsideAfterProblem(Gpu, Stable, Safer, "x", When);
        store.RestoreAfterUndo(Gpu, Safer, Stable);
        var only = Assert.Single(store.Profiles);
        Assert.Equal("Otomatik UV", only.Name);
        Assert.Equal(Stable, only.Settings);
        Assert.True(only.IsStable && only.AutoApplyOnStart);
    }

    [Fact]
    public void UnsavedSettingsAreKeptAsAProfile()
    {
        var store = new ProfileStore(_dir);
        var kept = store.KeepAsideAfterProblem(Gpu, Stable, Safer, "x", When);
        Assert.Equal(Stable, Assert.Single(kept).Settings);
        Assert.Null(store.GetStartupProfile(Gpu, null));
        store.RestoreAfterUndo(Gpu, Safer, Stable);
        Assert.Empty(store.Profiles);
    }

    [Fact]
    public void KeptAsideProfileIsNeverAutoApplied()
    {
        var store = new ProfileStore(_dir);
        store.Save(new UserProfile("Eski", Gpu, Stable, IsStable: true, AutoApplyOnStart: true, FailedAt: When));
        Assert.Null(store.GetStartupProfile(Gpu, null));
    }
}

public sealed class ProfileImportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gputweak-import").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void NewCopyFindsTheProfilesOfTheCopyOnTheDesktop()
    {
        var desktop = Path.Combine(_root, "Desktop");
        var old = Path.Combine(desktop, "tweakandrgbstone", DataPaths.FolderName);
        new ProfileStore(old).Save(new UserProfile("Otomatik UV", "NVIDIA GeForce RTX 5080", new TuningSettings(CoreOffsetMHz: 135)));
        var fresh = Path.Combine(_root, "Downloads", DataPaths.FolderName);

        Assert.NotNull(DataPaths.ImportProfilesFromOtherCopy(fresh, [desktop, Path.Combine(_root, "Downloads")]));
        Assert.Equal("Otomatik UV", Assert.Single(new ProfileStore(fresh).Profiles).Name);
        // Once it has profiles, nothing is copied again.
        Assert.Null(DataPaths.ImportProfilesFromOtherCopy(fresh, [desktop]));
    }

    [Fact]
    public void ImportSkipsProfilesThatAreAlreadyThereAndMatchesGpuNamesLoosely()
    {
        var a = new ProfileStore(Path.Combine(_root, "a"));
        a.Save(new UserProfile("UV", "NVIDIA GeForce RTX 5080", new TuningSettings(CoreOffsetMHz: 135), IsStable: true, AutoApplyOnStart: true));
        a.Save(new UserProfile("OC", "NVIDIA GeForce RTX 5080", new TuningSettings(CoreOffsetMHz: 200)));
        var b = new ProfileStore(Path.Combine(_root, "b"));
        b.Save(new UserProfile("UV", "nvidia geforce  rtx 5080", new TuningSettings(CoreOffsetMHz: 135)));

        Assert.Equal(1, b.ImportFrom(Path.Combine(_root, "a", "profiles.json")));
        Assert.Equal(2, b.Profiles.Count);
        Assert.True(ProfileStore.SameGpu("NVIDIA GeForce RTX 5080", "nvidia geforce  rtx 5080 "));
    }
}
