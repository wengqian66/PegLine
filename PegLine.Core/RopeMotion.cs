namespace PegLine.Core;

/// <summary>
/// Wall-clock timing for the rope show/hide slide. The old steps (0.14 / 0.17 per 16ms
/// tick, about 120ms show and 100ms hide) assumed every tick was cheap. A full-width
/// layered repaint often took longer than 16ms, so the same steps stretched into a
/// stutter. These durations are real time: a slow frame skips ahead instead of adding lag.
/// </summary>
public static class RopeMotion
{
    /// <summary>Was ~120ms of 0.14 steps.</summary>
    public const int ShowDurationMs = 64;

    /// <summary>Was ~100ms of 0.17 steps. Hide is shorter than show.</summary>
    public const int HideDurationMs = 40;

    /// <summary>Timer while a show or hide is in progress. Idle stays at <see cref="IdleTickMs"/>.</summary>
    public const int AnimTickMs = 8;

    public const int IdleTickMs = 16;

    /// <summary>
    /// Ease-out progress from <paramref name="from"/> toward shown (direction &gt; 0) or hidden.
    /// Elapsed 0 stays at <paramref name="from"/>. At the duration, show is 1 and hide is 0.
    /// </summary>
    public static float RevealAt(int direction, float from, long elapsedMs)
    {
        var duration = direction > 0 ? ShowDurationMs : HideDurationMs;
        if (duration <= 0 || elapsedMs >= duration)
        {
            return direction > 0 ? 1f : 0f;
        }

        if (elapsedMs <= 0)
        {
            return from;
        }

        var t = elapsedMs / (float)duration;
        var eased = 1f - (1f - t) * (1f - t);
        if (direction > 0)
        {
            return from + (1f - from) * eased;
        }

        return from * (1f - eased);
    }
}
