using SetSett.Model;

namespace SetSett.Engine;

/// <summary>Whole-PC CPU use from two GetSystemTimes readings, and the rolling sample window.</summary>
public static class CpuMath
{
    /// <summary>How much history the sampler keeps: enough for the longest "quiet for" choice.</summary>
    public static readonly TimeSpan History = TimeSpan.FromMinutes(65);

    /// <summary>
    /// Busy share of the interval, 0–100. GetSystemTimes' kernel time includes idle time, so busy is
    /// kernel + user - idle over kernel + user.
    /// </summary>
    public static double Percent(long idleDelta, long kernelDelta, long userDelta)
    {
        long total = kernelDelta + userDelta;
        if (total <= 0)
        {
            return 0;
        }

        return Math.Clamp((total - idleDelta) * 100.0 / total, 0, 100);
    }

    /// <summary>Appends a sample and drops the ones older than <see cref="History"/>.</summary>
    public static IReadOnlyList<CpuSample> Append(IReadOnlyList<CpuSample> samples, CpuSample sample) =>
        [.. samples.Where(s => sample.AtUtc - s.AtUtc <= History), sample];
}
