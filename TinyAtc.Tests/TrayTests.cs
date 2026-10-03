using System.Buffers.Binary;
using System.Globalization;
using TinyAtc.Formatting;
using TinyAtc.Geo;
using TinyAtc.OpenSky;
using TinyAtc.Tracking;
using TinyAtc.Tray;

namespace TinyAtc.Tests;

[TestClass]
public sealed class TrayTests
{
    private static readonly GeoPoint Home = new(43.0389, -87.9065);

    [TestInitialize]
    public void UseInvariantCulture() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    [TestMethod]
    public void OverheadKeepsAirborneWithinRadiusNearestFirst()
    {
        StateVector near = GeoAndAirportTests.State("near", GeoMath.Destination(Home, 45, 3), 3000, 0, 0, 200);
        StateVector farther = GeoAndAirportTests.State("far", GeoMath.Destination(Home, 180, 12), 10668, 0, 0, 230);
        StateVector outside = GeoAndAirportTests.State("out", GeoMath.Destination(Home, 0, 40), 10000, 0, 0, 230);
        StateVector ground = GeoAndAirportTests.State("gnd", Home, null, 0, 0, 0, onGround: true);

        IReadOnlyList<OverheadFlight> flights = Overhead.Select([farther, outside, ground, near], Home, radiusKm: 20);

        CollectionAssert.AreEqual(new[] { "near", "far" }, flights.Select(f => f.State.Icao24).ToArray());
        Assert.AreEqual("NEAR1 9,800 ft 1.6 nm NE", Overhead.Describe(flights[0], UnitSystem.Aviation));
        Assert.AreEqual("FAR1 35k ft 6.5 nm S", Overhead.Describe(flights[1], UnitSystem.Aviation));
        Assert.AreEqual("FAR1 10.7 km 12 km S", Overhead.Describe(flights[1], UnitSystem.Metric));
        Assert.AreEqual("NEAR1 9,800 ft 1.6 nm NE   •   FAR1 35k ft 6.5 nm S", Overhead.MarqueeText(flights, UnitSystem.Aviation));
    }

    [TestMethod]
    public void TooltipFitsTheShellLimit()
    {
        List<OverheadFlight> many = Enumerable.Range(0, 20)
            .Select(i => new OverheadFlight(GeoAndAirportTests.State($"a{i:00}", Home, 10000, 0, 0, 200), i, 0))
            .ToList();

        string tooltip = Overhead.Tooltip(many, 20, UnitSystem.Aviation);

        Assert.IsLessThanOrEqualTo(127, tooltip.Length);
        Assert.StartsWith("20 aircraft overhead\n", tooltip);
        Assert.AreEqual("No aircraft within 11 nm", Overhead.Tooltip([], 20, UnitSystem.Aviation));
    }

    [TestMethod]
    public void MarqueeFramesScrollTheStripRightToLeft()
    {
        // A 4-pixel strip with one lit column at x = 1.
        const int size = 4;
        const int width = 4;
        byte[] coverage = new byte[width * size];
        for (int y = 0; y < size; y++)
        {
            coverage[(y * width) + 1] = 255;
        }

        MarqueeStrip strip = new(coverage, width, size);
        Assert.AreEqual(8, strip.FrameCount);

        // Frame 0 is all lead-in; at frame 4 the strip's column 1 sits at icon x = 1; at frame 5 it's moved left to x = 0.
        Assert.AreEqual(-1, LitColumn(strip.RenderFrame(0, 0xFFFFFFFF), size));
        Assert.AreEqual(1, LitColumn(strip.RenderFrame(4, 0xFFFFFFFF), size));
        Assert.AreEqual(0, LitColumn(strip.RenderFrame(5, 0xFFFFFFFF), size));
        Assert.AreEqual(-1, LitColumn(strip.RenderFrame(7, 0xFFFFFFFF), size));
    }

    [TestMethod]
    public void IconEncoderWritesAValidHeader()
    {
        byte[] ico = IconEncoder.Encode(new uint[16 * 16], 16);

        Assert.AreEqual(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)));
        Assert.AreEqual(16, ico[6]);
        Assert.AreEqual(32, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(12)));
        Assert.AreEqual(ico.Length - IconEncoder.ImageOffset, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(14)));
        Assert.AreEqual(32, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(IconEncoder.ImageOffset + 8)), "DIB height is doubled for the mask.");
    }

    [TestMethod]
    public void PollingStaysInsideTheDailyBudget()
    {
        const double secondsPerDay = 86400;
        Assert.IsLessThanOrEqualTo(400 * 0.4, secondsPerDay / PollPolicy.Marquee(authenticated: false).TotalSeconds);
        Assert.IsLessThanOrEqualTo(4000 * 0.4, secondsPerDay / PollPolicy.Marquee(authenticated: true).TotalSeconds);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.IsTrue(PollPolicy.CanReuse(now.AddSeconds(-30), coversHome: true, now, authenticated: true));
        Assert.IsFalse(PollPolicy.CanReuse(now.AddSeconds(-30), coversHome: false, now, authenticated: true));
        Assert.IsFalse(PollPolicy.CanReuse(now.AddMinutes(-2), coversHome: true, now, authenticated: true));
    }

    [TestMethod]
    public void TrailsGrowPerPollAndForgetVanishedAircraft()
    {
        TrailStore trails = new(maxPoints: 3, forgetAfter: TimeSpan.FromSeconds(60));
        for (int t = 0; t < 5; t++)
        {
            StateVector s = GeoAndAirportTests.State("a", new GeoPoint(43 + (t * 0.01), -88), 1000, 0, 0, 100) with { TimePosition = t * 10 };
            trails.Add(new StatesSnapshot(t * 10, [s]));
        }

        Assert.HasCount(3, trails.Get("a"));
        Assert.AreEqual(43.04, trails.Get("a")[^1].Latitude, 1e-9);

        trails.Add(new StatesSnapshot(200, []));
        Assert.AreEqual(0, trails.Count);
    }

    [TestMethod]
    public void FormatsInEachUnitSystem()
    {
        Assert.AreEqual("35,000 ft", FlightFormat.Altitude(10668, false, UnitSystem.Aviation));
        Assert.AreEqual("10,670 m", FlightFormat.Altitude(10668, false, UnitSystem.Metric));
        Assert.AreEqual("Ground", FlightFormat.Altitude(null, true, UnitSystem.Aviation));
        Assert.AreEqual("450 kt", FlightFormat.Speed(231.5, UnitSystem.Aviation));
        Assert.AreEqual("518 mph", FlightFormat.Speed(231.5, UnitSystem.Imperial));
        Assert.AreEqual("↓ 800 ft/min", FlightFormat.VerticalRate(-4.23, UnitSystem.Aviation));
        Assert.AreEqual("Level", FlightFormat.VerticalRate(0.2, UnitSystem.Aviation));
        Assert.AreEqual("007°", FlightFormat.Heading(6.9));
        Assert.AreEqual("in 4 min", FlightFormat.Eta(TimeSpan.FromSeconds(230)));
    }

    // The single lit column's x in a frame, or -1 when the frame is blank. ICO rows are bottom-up BGRA.
    private static int LitColumn(byte[] ico, int size)
    {
        int bits = IconEncoder.ImageOffset + 40;
        for (int x = 0; x < size; x++)
        {
            if (ico[bits + (x * 4) + 3] != 0)
            {
                return x;
            }
        }

        return -1;
    }
}

[TestClass]
public sealed class AirlineColorTests
{
    [TestMethod]
    public void UsesBrandColorForKnownAirlinesAndNullForNonAirlineCallsigns()
    {
        Assert.AreEqual(0x0A6EC8u, AirlineColors.For("UAL123"));
        Assert.IsNull(AirlineColors.For("N123AB"));
        Assert.IsNull(AirlineColors.For(null));
    }

    [TestMethod]
    public void UnknownAirlinesGetAStableColor()
    {
        Assert.AreEqual(AirlineColors.For("ZZZ12"), AirlineColors.For("ZZZ99"));
        Assert.IsNotNull(AirlineColors.For("ZZZ12"));
    }

    [TestMethod]
    public void ForBackgroundMakesColorsReadable()
    {
        Assert.AreNotEqual(0xFFFFEC00u, AirlineColors.ForBackground(0xFFEC00, lightBackground: true));
        Assert.AreEqual(0xFFFFEC00u, AirlineColors.ForBackground(0xFFEC00, lightBackground: false));
    }

    [TestMethod]
    public void ColumnColorsCoverEachSegmentOnly()
    {
        uint[] colors = MarqueeStrip.ColumnColors(10, [0, 4, 6], [0xFF112233u, null]);
        CollectionAssert.AreEqual(new uint[] { 0xFF112233, 0xFF112233, 0xFF112233, 0xFF112233, 0, 0, 0, 0, 0, 0 }, colors);
    }
}
