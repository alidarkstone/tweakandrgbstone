using GpuTweak.Core.Models;

namespace GpuTweak.Core.Profiles;

/// <param name="FailedAt">Set on a profile that was kept aside after a problem (driver reset, crash, failed test); it is never auto-applied.</param>
/// <param name="FailedReason">What went wrong and what the replacement changed, e.g. "ekran sürücüsü çökmesi · çekirdek +135 → +105 MHz".</param>
/// <param name="ReplacedBy">The profile that took over this profile's name with safer settings, so an undo can bring this one back.</param>
public sealed record UserProfile(
    string Name,
    string GpuName,
    TuningSettings Settings,
    bool IsStable = false,
    bool AutoApplyOnStart = false,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? FailedAt = null,
    string? FailedReason = null,
    string? ReplacedBy = null)
{
    public bool IsFailed => FailedAt is not null;
}
