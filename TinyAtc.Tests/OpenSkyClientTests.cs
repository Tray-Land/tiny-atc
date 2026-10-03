using System.Net;
using System.Text;
using TinyAtc.Geo;
using TinyAtc.OpenSky;

namespace TinyAtc.Tests;

[TestClass]
public sealed class OpenSkyClientTests
{
    private static readonly BoundingBox Box = new(42, -88, 43, -87);

    [TestMethod]
    public async Task AnonymousRequestHasNoAuthorizationAndReadsCredits()
    {
        FakeHandler handler = new(req =>
        {
            Assert.IsNull(req.Headers.Authorization);
            StringAssert.Contains(req.RequestUri!.Query, "lamin=42&lomin=-88&lamax=43&lomax=-87&extended=1");
            return Json("""{"time":1,"states":[]}""", remaining: 321);
        });
        OpenSkyClient client = new(new HttpClient(handler));

        StatesSnapshot snapshot = await client.GetStatesAsync(Box, CancellationToken.None);

        Assert.IsEmpty(snapshot.States);
        Assert.AreEqual(321, client.RemainingCredits);
    }

    [TestMethod]
    public async Task RateLimitBlocksFurtherCallsWithoutARoundTrip()
    {
        int calls = 0;
        FakeHandler handler = new(_ =>
        {
            calls++;
            HttpResponseMessage r = new(HttpStatusCode.TooManyRequests);
            r.Headers.Add("X-Rate-Limit-Retry-After-Seconds", "600");
            return r;
        });
        OpenSkyClient client = new(new HttpClient(handler));

        OpenSkyRateLimitedException first = await Assert.ThrowsExactlyAsync<OpenSkyRateLimitedException>(
            () => client.GetStatesAsync(Box, CancellationToken.None));
        Assert.AreEqual(TimeSpan.FromSeconds(600), first.RetryAfter);

        await Assert.ThrowsExactlyAsync<OpenSkyRateLimitedException>(() => client.GetStatesAsync(Box, CancellationToken.None));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task UsesBearerTokenAndRefreshesOnceAfter401()
    {
        int tokens = 0;
        int apiCalls = 0;
        FakeHandler handler = new(req =>
        {
            if (req.RequestUri!.AbsoluteUri == OpenSkyClient.TokenUrl)
            {
                tokens++;
                return Json($$"""{"access_token":"t{{tokens}}","expires_in":1800}""");
            }

            apiCalls++;
            Assert.AreEqual("Bearer", req.Headers.Authorization?.Scheme);
            return req.Headers.Authorization?.Parameter == "t1"
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Json("""{"time":1,"states":[]}""");
        });
        OpenSkyClient client = new(new HttpClient(handler)) { Credentials = new("id", "secret") };

        _ = await client.GetStatesAsync(Box, CancellationToken.None);

        Assert.AreEqual(2, tokens);
        Assert.AreEqual(2, apiCalls);
    }

    [TestMethod]
    public async Task BadCredentialsThrowAuthException()
    {
        FakeHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        OpenSkyClient client = new(new HttpClient(handler)) { Credentials = new("id", "wrong") };

        await Assert.ThrowsExactlyAsync<OpenSkyAuthException>(() => client.VerifyCredentialsAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task TrackNotFoundIsNull()
    {
        OpenSkyClient client = new(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
        Assert.IsNull(await client.GetTrackAsync("ABC123", CancellationToken.None));
    }

    private static HttpResponseMessage Json(string body, int? remaining = null)
    {
        HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (remaining is { } n)
        {
            r.Headers.Add("X-Rate-Limit-Remaining", n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return r;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
