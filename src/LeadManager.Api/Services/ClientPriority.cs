namespace LeadManager.Api.Services;

/// <summary>
/// Client priority, modelled on the JengoWork priority score: value * urgency / effort^2,
/// each on the Fibonacci scale. Never stored: derived each time, 0 while a sub-score is missing.
/// </summary>
public static class ClientPriority
{
    public static readonly int[] Fibonacci = { 1, 2, 3, 5, 8, 13, 21, 34, 55, 89, 144 };

    public static bool InRange(int? v) => v == null || (v >= 1 && v <= 144);

    /// <summary>Snaps 1..144 to the nearest Fibonacci number (ties go up). Null stays null.</summary>
    public static int? Snap(int? v)
    {
        if (v == null) return null;
        var best = Fibonacci[0];
        foreach (var f in Fibonacci)
            if (Math.Abs(f - v.Value) < Math.Abs(best - v.Value) || (Math.Abs(f - v.Value) == Math.Abs(best - v.Value) && f > best))
                best = f;
        return best;
    }

    public static double Score(int? value, int? urgency, int? effort)
    {
        if (value is null or <= 0 || urgency is null or <= 0 || effort is null or <= 0) return 0;
        return Math.Round((double)value.Value * urgency.Value / ((double)effort.Value * effort.Value), 2);
    }
}
