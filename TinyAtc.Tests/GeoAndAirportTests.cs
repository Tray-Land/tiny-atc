using TinyAtc.Airports;
using TinyAtc.Geo;
using TinyAtc.OpenSky;

namespace TinyAtc.Tests;

[TestClass]
public sealed class GeoAndAirportTests
{
    private static readonly Airport Mke = new("KMKE", "MKE", "General Mitchell International Airport", "Milwaukee", "US", 42.9472, -87.8966, 723, true);

    private static readonly Lazy<AirportCatalog> Catalog = new(() =>
    {
        using StreamReader reader = new(Path.Combine(AppContext.BaseDirectory, "Data", "airports.csv"));
        return AirportCatalog.Parse(reader);
    });

    [TestMethod]
    public void DistanceAndBearingMatchKnownValues()
    {
        GeoPoint ord = new(41.9786, -87.9048);
        Assert.AreEqual(107.8, GeoMath.DistanceKm(Mke.Position, ord), 0.5);
        Assert.AreEqual(180, GeoMath.BearingDegrees(Mke.Position, ord), 1);
        Assert.AreEqual("S", GeoMath.CompassPoint(GeoMath.BearingDegrees(Mke.Position, ord)));
    }

    [TestMethod]
    public void DestinationRoundTripsWithDistance()
    {
        GeoPoint p = GeoMath.Destination(Mke.Position, 45, 50);
        Assert.AreEqual(50, GeoMath.DistanceKm(Mke.Position, p), 0.01);
        Assert.AreEqual(45, GeoMath.BearingDegrees(Mke.Position, p), 0.5);
    }

    [TestMethod]
    public void ClampAreaKeepsCenterAndFitsOneCredit()
    {
        BoundingBox big = new(30, -100, 50, -70);
        BoundingBox clamped = big.ClampArea(OpenSkyClient.OneCreditSquareDegrees);

        Assert.AreEqual(25, clamped.SquareDegrees, 0.001);
        Assert.AreEqual(big.Center.Latitude, clamped.Center.Latitude, 1e-9);
        Assert.AreEqual(big.Center.Longitude, clamped.Center.Longitude, 1e-9);

        BoundingBox small = new(42, -88, 43, -87);
        Assert.AreEqual(small, small.ClampArea(OpenSkyClient.OneCreditSquareDegrees), "A box that already fits is unchanged.");
    }

    [TestMethod]
    public void AroundCoversRadiusInEveryDirection()
    {
        BoundingBox box = BoundingBox.Around(Mke.Position, 60);
        foreach (double bearing in new[] { 0.0, 90, 180, 270 })
        {
            Assert.IsTrue(box.Contains(GeoMath.Destination(Mke.Position, bearing, 59.5)));
        }

        Assert.IsLessThan(OpenSkyClient.OneCreditSquareDegrees, box.SquareDegrees);
    }

    [TestMethod]
    public void ClassifiesApproachDepartureGroundAndOverflight()
    {
        // 15 km south, descending through 900 m, flying north at the field.
        StateVector arriving = State("arr", GeoMath.Destination(Mke.Position, 180, 15), altitude: 900, track: 0, vertical: -4, velocity: 75);
        // 8 km north, climbing out northbound.
        StateVector departing = State("dep", GeoMath.Destination(Mke.Position, 0, 8), altitude: 1200, track: 5, vertical: 10, velocity: 90);
        StateVector taxiing = State("gnd", GeoMath.Destination(Mke.Position, 90, 1), altitude: null, track: 90, vertical: 0, velocity: 8, onGround: true);
        StateVector cruising = State("hi", Mke.Position, altitude: 11000, track: 270, vertical: 0, velocity: 230);
        StateVector distantGround = State("far", GeoMath.Destination(Mke.Position, 0, 30), altitude: null, track: 0, vertical: 0, velocity: 0, onGround: true);

        IReadOnlyList<TrafficEntry> traffic = AirportTraffic.Classify(Mke, [cruising, taxiing, departing, arriving, distantGround]);

        Assert.HasCount(3, traffic);
        Assert.AreEqual(("arr", TrafficKind.Arriving), (traffic[0].State.Icao24, traffic[0].Kind));
        Assert.AreEqual(("dep", TrafficKind.Departing), (traffic[1].State.Icao24, traffic[1].Kind));
        Assert.AreEqual(("gnd", TrafficKind.OnGround), (traffic[2].State.Icao24, traffic[2].Kind));
        Assert.AreEqual(200, traffic[0].Eta!.Value.TotalSeconds, 2, "15 km at 75 m/s.");
    }

    [TestMethod]
    public void DescendingAwayFromTheFieldIsNotAnArrival()
    {
        StateVector s = State("x", GeoMath.Destination(Mke.Position, 180, 15), altitude: 900, track: 180, vertical: -4, velocity: 75);
        Assert.IsNull(AirportTraffic.Classify(Mke, s));
    }

    [TestMethod]
    public void CatalogLoadsAndSearchesTheBundledData()
    {
        AirportCatalog catalog = Catalog.Value;

        Assert.IsGreaterThan(3000, catalog.All.Count);
        Assert.AreEqual("KMKE", catalog.Search("MKE")[0].Icao, "IATA code is an exact match.");
        Assert.AreEqual("EGLL", catalog.Search("egll")[0].Icao);
        Assert.IsTrue(catalog.Search("Heathrow").Any(a => a.Icao == "EGLL"));
        Assert.AreEqual("KMKE", catalog.Nearest(new GeoPoint(42.95, -87.9), 10)?.Icao);
    }

    [TestMethod]
    public void OriginComesFromAGroundStart()
    {
        FlightTrack fromDenver = new("a", "SKW1", 0, 0, [new TrackPoint(0, 39.8775, -104.6555, 1524, 90, false)]);
        FlightTrack midAir = new("a", "SKW1", 0, 0, [new TrackPoint(0, 39.8775, -104.6555, 9000, 90, false)]);

        Assert.AreEqual("KDEN", FlightOrigin.Find(fromDenver, Catalog.Value)?.Icao);
        Assert.IsNull(FlightOrigin.Find(midAir, Catalog.Value));
    }

    internal static StateVector State(string icao, GeoPoint p, double? altitude, double track, double vertical, double velocity, bool onGround = false) =>
        new(icao, icao.ToUpperInvariant() + "1", "US", 100, 100, p.Longitude, p.Latitude, altitude, onGround, velocity, track, vertical, altitude, null, 0, 0);
}
