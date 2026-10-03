using System.Globalization;
using TinyAtc.Formatting;
using TinyAtc.Geo;
using TinyAtc.Tray;

namespace TinyAtc.Tests;

[TestClass]
public sealed class MarqueeTemplateTests
{
    private static readonly GeoPoint Home = new(43.0389, -87.9065);

    [TestInitialize]
    public void UseInvariantCulture() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    private static OverheadFlight Flight() =>
        Overhead.Select([GeoAndAirportTests.State("far", GeoMath.Destination(Home, 180, 12), 10668, 0, 0, 230)], Home, 20)[0];

    [TestMethod]
    public void FieldsAndLiteralsRenderInOrder()
    {
        string[] tokens = ["Now:", "{distance}", "{callsign}", "{direction}"];

        Assert.AreEqual("Now: 6.5 nm FAR1 S", MarqueeTemplate.Render(Flight(), tokens, UnitSystem.Aviation));
    }

    [TestMethod]
    public void EmptyOrMissingTemplateUsesDefault()
    {
        Assert.AreEqual("FAR1 35k ft 6.5 nm S", MarqueeTemplate.Render(Flight(), [], UnitSystem.Aviation));
        Assert.AreEqual("FAR1 35k ft 6.5 nm S", MarqueeTemplate.Render(Flight(), null, UnitSystem.Aviation));
    }

    [TestMethod]
    public void FieldsWithoutValuesAreSkipped()
    {
        Assert.AreEqual("FAR1", MarqueeTemplate.Render(Flight(), ["{callsign}", "{squawk}"], UnitSystem.Aviation));
    }

    [TestMethod]
    public void TokenParsing()
    {
        Assert.IsTrue(MarqueeTemplate.TryParseField("{VerticalRate}", out MarqueeField f));
        Assert.AreEqual(MarqueeField.VerticalRate, f);
        Assert.IsFalse(MarqueeTemplate.TryParseField("{nope}", out _));
        Assert.IsFalse(MarqueeTemplate.TryParseField("hello", out _));
        Assert.AreEqual("{speed}", MarqueeTemplate.Token(MarqueeField.Speed));
        Assert.AreEqual("hello", MarqueeTemplate.DisplayText("hello"));
        Assert.AreEqual("Compass direction", MarqueeTemplate.DisplayText("{direction}"));
    }
}
