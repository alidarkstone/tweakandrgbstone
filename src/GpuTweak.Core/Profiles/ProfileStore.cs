using System.Text.Json;
using GpuTweak.Core.Localization;
using GpuTweak.Core.Models;
using GpuTweak.Core.Safety;

namespace GpuTweak.Core.Profiles;

/// <summary>User profiles, stored as profiles.json next to the portable exe.</summary>
public sealed class ProfileStore
{
    private readonly string _path;
    private readonly List<UserProfile> _profiles = [];

    public ProfileStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "profiles.json");
        if (!File.Exists(_path)) return;
        try
        {
            _profiles.AddRange(JsonSerializer.Deserialize<List<UserProfile>>(File.ReadAllText(_path), JsonDefaults.Options) ?? []);
        }
        catch (JsonException)
        {
            File.Copy(_path, _path + ".broken", overwrite: true);
        }
    }

    public IReadOnlyList<UserProfile> Profiles => _profiles;

    public void Save(UserProfile profile)
    {
        var index = _profiles.FindIndex(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _profiles[index] = profile;
        else _profiles.Add(profile);

        // Only one profile can auto-apply.
        if (profile.AutoApplyOnStart)
        {
            for (var i = 0; i < _profiles.Count; i++)
            {
                if (_profiles[i] != profile && _profiles[i].AutoApplyOnStart)
                    _profiles[i] = _profiles[i] with { AutoApplyOnStart = false };
            }
        }
        Persist();
    }

    public void Delete(string name)
    {
        _profiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Persist();
    }

    /// <summary>
    /// The profile to apply at startup: it must be marked stable, belong to this GPU,
    /// and the previous session must not have crashed.
    /// </summary>
    public UserProfile? GetStartupProfile(string gpuName, CrashReport? crash)
    {
        if (crash is not null) return null;
        return _profiles.FirstOrDefault(p =>
            p.AutoApplyOnStart && p.IsStable && !p.IsFailed && SameGpu(p.GpuName, gpuName));
    }

    /// <summary>
    /// A problem was found with <paramref name="from"/> and <paramref name="to"/> is the safer step. Every profile that
    /// held <paramref name="from"/> is kept: it is renamed to "&lt;name&gt; · hata &lt;time&gt;" (not auto-applied any more) and a
    /// new profile with the old name, the old flags and the safer settings takes its place, so auto-apply picks the safer one
    /// while the last settings that worked stay in the list. When no profile held them (unsaved custom settings),
    /// they are saved as "Hata öncesi ayar · &lt;time&gt;". Returns the renamed (kept-aside) profiles.
    /// </summary>
    public IReadOnlyList<UserProfile> KeepAsideAfterProblem(string gpuName, TuningSettings from, TuningSettings to, string reason, DateTimeOffset when)
    {
        var kept = new List<UserProfile>();
        if (from == to || from.IsStock) return kept;
        var targets = _profiles.Where(p => !p.IsFailed && p.Settings == from && SameGpu(p.GpuName, gpuName)).ToList();
        foreach (var target in targets)
        {
            var aside = target with
            {
                Name = UniqueName(L.F("{0} · hata {1}", target.Name, when.ToString("dd.MM HH:mm"))),
                AutoApplyOnStart = false,
                FailedAt = when,
                FailedReason = reason,
                ReplacedBy = target.Name,
            };
            var index = _profiles.IndexOf(target);
            _profiles[index] = target with { Settings = to, CreatedAt = when };
            _profiles.Insert(index + 1, aside);
            kept.Add(aside);
        }
        if (targets.Count == 0)
        {
            var aside = new UserProfile(UniqueName(L.F("Hata öncesi ayar · {0}", when.ToString("dd.MM HH:mm"))), gpuName, from,
                CreatedAt: when, FailedAt: when, FailedReason: reason);
            _profiles.Add(aside);
            kept.Add(aside);
        }
        Persist();
        return kept;
    }

    /// <summary>
    /// Takes back <see cref="KeepAsideAfterProblem"/> (the user says the problem was not the card's): each profile now holding
    /// <paramref name="stepped"/> gets <paramref name="original"/> back, and the kept-aside copy it replaced is removed. Profiles
    /// without a kept-aside copy (older versions) are simply set back.
    /// </summary>
    public void RestoreAfterUndo(string gpuName, TuningSettings stepped, TuningSettings original)
    {
        var changed = false;
        foreach (var current in _profiles.Where(p => !p.IsFailed && p.Settings == stepped && SameGpu(p.GpuName, gpuName)).ToList())
        {
            var aside = _profiles.LastOrDefault(p => p.IsFailed && p.Settings == original && p.ReplacedBy == current.Name
                && SameGpu(p.GpuName, gpuName));
            var index = _profiles.IndexOf(current);
            _profiles[index] = current with { Settings = original, IsStable = aside?.IsStable ?? current.IsStable, CreatedAt = aside?.CreatedAt ?? current.CreatedAt };
            if (aside is not null) _profiles.Remove(aside);
            changed = true;
        }
        // Unsaved settings that were kept aside: the settings are back on the card, the copy is not needed.
        changed |= _profiles.RemoveAll(p => p.IsFailed && p.ReplacedBy is null && p.Settings == original
            && SameGpu(p.GpuName, gpuName)) > 0;
        if (changed) Persist();
    }

    /// <summary>GPU names are compared without case and extra spaces; a driver or backend may report them slightly differently.</summary>
    public static bool SameGpu(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string name) => string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Adds the profiles of another profiles.json (e.g. the data folder of a copy of the app somewhere else). Profiles
    /// whose name is already taken are only added when their settings differ, under a "(2)" name. Returns how many were added.
    /// </summary>
    public int ImportFrom(string profilesJson)
    {
        List<UserProfile> other;
        try { other = JsonSerializer.Deserialize<List<UserProfile>>(File.ReadAllText(profilesJson), JsonDefaults.Options) ?? []; }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return 0; }
        var added = 0;
        foreach (var profile in other)
        {
            if (_profiles.Any(p => p.Settings == profile.Settings && SameGpu(p.GpuName, profile.GpuName) && p.IsFailed == profile.IsFailed)) continue;
            // Only one profile may auto-apply; the one already here wins.
            _profiles.Add(profile with { Name = UniqueName(profile.Name), AutoApplyOnStart = profile.AutoApplyOnStart && !_profiles.Any(p => p.AutoApplyOnStart) });
            added++;
        }
        if (added > 0) Persist();
        return added;
    }

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var i = 2; _profiles.Any(p => p.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); i++) candidate = $"{name} ({i})";
        return candidate;
    }

    private void Persist()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_profiles, JsonDefaults.Options));
        File.Move(temp, _path, overwrite: true);
    }
}
