using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TinyAtc.Geo;

namespace TinyAtc.OpenSky;

/// <summary>A user's OpenSky API client (Account page → API client). Not the app's: every user brings their own.</summary>
public sealed record OpenSkyCredentials(string ClientId, string ClientSecret);

/// <summary>OpenSky refused the request because the credit bucket is empty.</summary>
public sealed class OpenSkyRateLimitedException(TimeSpan retryAfter)
    : Exception($"OpenSky's request limit was reached. Try again in {retryAfter.TotalMinutes:0} min.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>The saved OpenSky API client was rejected.</summary>
public sealed class OpenSkyAuthException(string message) : Exception(message);

/// <summary>
/// Minimal client for the OpenSky REST API (https://openskynetwork.github.io/opensky-api/rest.html).
/// Anonymous by default; with <see cref="Credentials"/> it uses the OAuth2 client-credentials flow,
/// which raises the daily credit budget tenfold. Thread-safe for concurrent callers.
/// </summary>
public sealed class OpenSkyClient
{
    public const string ApiBase = "https://opensky-network.org/api";
    public const string TokenUrl = "https://auth.opensky-network.org/auth/realms/opensky-network/protocol/openid-connect/token";

    // OpenSky charges 1 credit for boxes up to 25 square degrees.
    public const double OneCreditSquareDegrees = 25;

    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private OpenSkyCredentials? _credentials;
    private string? _token;
    private DateTimeOffset _tokenExpires;
    private DateTimeOffset _blockedUntil;

    public OpenSkyClient(HttpClient http, TimeProvider? time = null)
    {
        _http = http;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Null for anonymous access. Setting it drops any cached token.</summary>
    public OpenSkyCredentials? Credentials
    {
        get => _credentials;
        set
        {
            _credentials = value;
            _token = null;
            _blockedUntil = default; // A new account has its own budget.
        }
    }

    public bool IsAuthenticated => _credentials is not null;

    /// <summary>Credits left in the last-used bucket, from <c>X-Rate-Limit-Remaining</c>, when OpenSky reported it.</summary>
    public int? RemainingCredits { get; private set; }

    /// <summary>When a rate-limited client may call again; requests before then fail without a round trip.</summary>
    public DateTimeOffset BlockedUntil => _blockedUntil;

    /// <summary>Every aircraft in <paramref name="box"/> (1 credit up to 25 square degrees).</summary>
    public async Task<StatesSnapshot> GetStatesAsync(BoundingBox box, CancellationToken cancellationToken)
    {
        string url = string.Create(CultureInfo.InvariantCulture,
            $"{ApiBase}/states/all?lamin={box.MinLatitude:0.####}&lomin={box.MinLongitude:0.####}&lamax={box.MaxLatitude:0.####}&lomax={box.MaxLongitude:0.####}&extended=1");
        byte[]? body = await SendAsync(url, cancellationToken).ConfigureAwait(false);
        return body is null ? new StatesSnapshot(0, []) : OpenSkyParser.ParseStates(body);
    }

    /// <summary>The current state of specific aircraft, worldwide, in one request (1 credit). Absent aircraft are simply missing.</summary>
    public async Task<StatesSnapshot> GetStatesAsync(IEnumerable<string> icao24s, CancellationToken cancellationToken)
    {
        string query = string.Join('&', icao24s.Select(i => $"icao24={Uri.EscapeDataString(i.ToLowerInvariant())}"));
        byte[]? body = await SendAsync($"{ApiBase}/states/all?{query}&extended=1", cancellationToken).ConfigureAwait(false);
        return body is null ? new StatesSnapshot(0, []) : OpenSkyParser.ParseStates(body);
    }

    /// <summary>The live flight path of one aircraft, or null when OpenSky has none.</summary>
    public async Task<FlightTrack?> GetTrackAsync(string icao24, CancellationToken cancellationToken)
    {
        string url = $"{ApiBase}/tracks/all?icao24={Uri.EscapeDataString(icao24.ToLowerInvariant())}&time=0";
        byte[]? body = await SendAsync(url, cancellationToken).ConfigureAwait(false);
        return body is null ? null : OpenSkyParser.ParseTrack(body);
    }

    /// <summary>Exchanges the credentials for a token; throws <see cref="OpenSkyAuthException"/> when they're wrong.</summary>
    public async Task VerifyCredentialsAsync(CancellationToken cancellationToken)
    {
        _token = null;
        _ = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]?> SendAsync(string url, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (now < _blockedUntil)
        {
            throw new OpenSkyRateLimitedException(_blockedUntil - now);
        }

        for (int attempt = 0; ; attempt++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            if (await GetTokenAsync(cancellationToken).ConfigureAwait(false) is { } token)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (TryHeaderInt(response, "X-Rate-Limit-Remaining") is { } remaining)
            {
                RemainingCredits = remaining;
            }

            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    return null;
                case HttpStatusCode.TooManyRequests:
                    TimeSpan retryAfter = TryHeaderInt(response, "X-Rate-Limit-Retry-After-Seconds") is { } seconds and > 0
                        ? TimeSpan.FromSeconds(seconds)
                        : DefaultRetryAfter;
                    _blockedUntil = _time.GetUtcNow() + retryAfter;
                    RemainingCredits = 0;
                    throw new OpenSkyRateLimitedException(retryAfter);
                case HttpStatusCode.Unauthorized when IsAuthenticated && attempt == 0:
                    // Tokens last 30 minutes; a 401 means ours expired early. Get a new one once.
                    _token = null;
                    continue;
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when IsAuthenticated:
                    throw new OpenSkyAuthException("OpenSky didn't accept your API client. Check it in Settings.");
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_credentials is not { } credentials)
        {
            return null;
        }

        if (_token is { } cached && _time.GetUtcNow() < _tokenExpires)
        {
            return cached;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is { } fresh && _time.GetUtcNow() < _tokenExpires)
            {
                return fresh;
            }

            using FormUrlEncodedContent form = new(
            [
                new("grant_type", "client_credentials"),
                new("client_id", credentials.ClientId),
                new("client_secret", credentials.ClientSecret),
            ]);
            using HttpResponseMessage response = await _http.PostAsync(TokenUrl, form, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                throw new OpenSkyAuthException("OpenSky didn't recognize that client ID and secret.");
            }

            response.EnsureSuccessStatusCode();
            byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            (string token, TimeSpan lifetime) = ParseToken(body);
            if (!ReferenceEquals(credentials, _credentials))
            {
                return null; // Signed out (or switched accounts) while this was in flight.
            }

            _token = token;
            _tokenExpires = _time.GetUtcNow() + lifetime - TokenRefreshMargin;
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    internal static (string Token, TimeSpan Lifetime) ParseToken(byte[] json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        string token = root.TryGetProperty("access_token", out JsonElement t) && t.GetString() is { Length: > 0 } s
            ? s
            : throw new OpenSkyAuthException("OpenSky's sign-in response had no access token.");
        double seconds = root.TryGetProperty("expires_in", out JsonElement e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 1800;
        return (token, TimeSpan.FromSeconds(Math.Max(120, seconds)));
    }

    private static int? TryHeaderInt(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values)
        && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
}
