using System.Net.Http;
using TinyAtc.Geo;
using TinyAtc.OpenSky;

namespace TinyAtc.Services;

/// <summary>
/// The app's one <see cref="OpenSkyClient"/>, signed in with the saved account if there is one,
/// and the latest snapshot any part of the app fetched, so the tray marquee can reuse the map's
/// data instead of spending another credit.
/// </summary>
internal static class OpenSkyService
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly Lazy<OpenSkyClient> s_client = new(() => new OpenSkyClient(Http) { Credentials = OpenSkyAccount.Load() });

    /// <summary>Raised (on the publishing thread, the UI thread in practice) after any successful states fetch.</summary>
    public static event EventHandler? SnapshotPublished;

    /// <summary>Raised on the UI thread after the account is saved or removed.</summary>
    public static event EventHandler? AccountChanged;

    public static OpenSkyClient Client => s_client.Value;

    /// <summary>The most recent states fetch, the box it covered, and when.</summary>
    public static (StatesSnapshot Snapshot, BoundingBox Box, DateTimeOffset FetchedAt)? Latest { get; private set; }

    public static void Publish(StatesSnapshot snapshot, BoundingBox box)
    {
        Latest = (snapshot, box, DateTimeOffset.UtcNow);
        SnapshotPublished?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Verifies and saves an API client; throws <see cref="OpenSkyAuthException"/> when OpenSky rejects it.</summary>
    public static async Task SignInAsync(OpenSkyCredentials credentials, CancellationToken cancellationToken)
    {
        OpenSkyClient probe = new(Http) { Credentials = credentials };
        await probe.VerifyCredentialsAsync(cancellationToken);
        OpenSkyAccount.Save(credentials);
        Client.Credentials = credentials;
        AccountChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void SignOut()
    {
        OpenSkyAccount.Remove();
        Client.Credentials = null;
        AccountChanged?.Invoke(null, EventArgs.Empty);
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TinyATC/1.0");
        return http;
    }
}
