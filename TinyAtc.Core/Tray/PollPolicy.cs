namespace TinyAtc.Tray;

/// <summary>
/// How often to ask OpenSky for data, sized to its daily credit budget: 400 credits a day
/// anonymously and 4,000 with a free account, at 1 credit per request.
/// </summary>
public static class PollPolicy
{
    /// <summary>While the map is open. Anonymous: ~2 hours of open map a day; signed in: ~11.</summary>
    public static TimeSpan Flyout(bool authenticated) =>
        authenticated ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(20);

    /// <summary>
    /// The tray marquee runs all day, so it gets a small, fixed slice of the budget:
    /// 144 credits a day anonymously, 1,440 signed in.
    /// </summary>
    public static TimeSpan Marquee(bool authenticated) =>
        authenticated ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(10);

    /// <summary>
    /// Starred flights are all fetched in one request. Anonymous: 144 credits a day on top of the
    /// marquee's 144, leaving the rest for the map; signed in: 720 of 4,000.
    /// </summary>
    public static TimeSpan Favorites(bool authenticated) =>
        authenticated ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether the marquee can reuse the flyout's latest data instead of making its own request:
    /// it must be recent enough and must have covered the whole overhead circle.
    /// </summary>
    public static bool CanReuse(DateTimeOffset fetchedAt, bool coversHome, DateTimeOffset now, bool authenticated) =>
        coversHome && now - fetchedAt < Marquee(authenticated);
}
