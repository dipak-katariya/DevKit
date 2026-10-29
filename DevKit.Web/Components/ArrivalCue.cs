namespace DevKit.Web.Components;

/// <summary>
/// Lets data that a load just brought fade in, once. A tab panel is rebuilt on every tab switch, so an entrance tied
/// to the element alone would replay each time; this one plays only on renders that follow a load straight away,
/// through the class <see cref="Class"/> hands out for a short while after <see cref="Mark"/>.
/// </summary>
internal sealed class ArrivalCue
{
    /// <summary>The class the entrance in components.css hangs on.</summary>
    public const string ArriveClass = "arrive";

    // Longer than the entrance itself. A load ends with two renders in quick succession; keeping the class through
    // both lets the fade run its course instead of being cut off when the second render drops it.
    private const long HoldMilliseconds = 400;

    private readonly Func<long> _now;
    private long? _markedAt;

    public ArrivalCue()
        : this(() => Environment.TickCount64)
    {
    }

    /// <summary>With a clock of the caller's own, in milliseconds, as tests use it.</summary>
    internal ArrivalCue(Func<long> now)
    {
        _now = now;
    }

    /// <summary>Call when a load has brought new data to show.</summary>
    public void Mark() => _markedAt = _now();

    /// <summary><see cref="ArriveClass"/> on the renders right after <see cref="Mark"/>, otherwise empty.</summary>
    public string Class => _markedAt is { } at && _now() - at < HoldMilliseconds ? ArriveClass : "";
}
