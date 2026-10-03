using TinyAtc.Formatting;
using TinyAtc.OpenSky;
using TinyAtc.Tracking;

namespace TinyAtc.Tests;

[TestClass]
public sealed class FavoriteFlightTests
{
    private const long Now = 1_800_000_000;

    private static StateVector State(bool onGround, double altitude = 10000) =>
        new("abc123", "UAL123  ", "United States", Now, Now, -122.0, 37.0, altitude, onGround, 230, 90, -5, altitude, null, 0, 3);

    [TestMethod]
    public void FlyingThenOnGroundLands()
    {
        FavoriteFlight f = FavoriteFlight.From(State(false), Now);
        Assert.IsTrue(f.WasAirborne);

        (FavoriteFlight after, FavoriteEvent evt) = f.Apply(State(true), Now + 60);
        Assert.AreEqual(FavoriteEvent.Landed, evt);
        Assert.IsTrue(after.LastOnGround);
    }

    [TestMethod]
    public void StarredOnTheGroundWaitsForTakeoff()
    {
        FavoriteFlight f = FavoriteFlight.From(State(true), Now);
        Assert.AreEqual(FavoriteEvent.None, f.Apply(State(true), Now + 60).Event);
        Assert.AreEqual(FavoriteEvent.None, f.Apply(State(false), Now + 120).Event);
    }

    [TestMethod]
    public void VanishingLowOnApproachLandsAfterTwoMisses()
    {
        FavoriteFlight f = FavoriteFlight.From(State(false, altitude: 600), Now);
        (FavoriteFlight once, FavoriteEvent first) = f.Apply(null, Now + 60);
        Assert.AreEqual(FavoriteEvent.None, first);
        Assert.AreEqual(FavoriteEvent.Landed, once.Apply(null, Now + 120).Event);
    }

    [TestMethod]
    public void VanishingAtCruiseIsNotALanding()
    {
        FavoriteFlight f = FavoriteFlight.From(State(false, altitude: 11000), Now);
        FavoriteFlight missed = f;
        for (int i = 0; i < 5; i++)
        {
            (missed, FavoriteEvent evt) = missed.Apply(null, Now + 60 * i);
            Assert.AreEqual(FavoriteEvent.None, evt);
        }
    }

    [TestMethod]
    public void SeeingItAgainResetsMisses()
    {
        FavoriteFlight f = FavoriteFlight.From(State(false), Now).Apply(null, Now + 60).Flight;
        Assert.AreEqual(1, f.Misses);
        Assert.AreEqual(0, f.Apply(State(false), Now + 120).Flight.Misses);
    }

    [TestMethod]
    public void ExpiresAfterADayUnseen()
    {
        FavoriteFlight f = FavoriteFlight.From(State(false), Now);
        Assert.IsFalse(f.IsExpired(Now + 3600));
        Assert.IsTrue(f.IsExpired(Now + 25 * 3600));
    }

    [TestMethod]
    public void StatusReadsForTheTooltip()
    {
        FavoriteFlight f = FavoriteFlight.From(State(false), Now);
        StringAssert.StartsWith(f.Status(UnitSystem.Aviation), "UAL123 ");
        Assert.AreEqual("UAL123 no signal", f.Apply(null, Now + 60).Flight.Status(UnitSystem.Aviation));
        Assert.AreEqual("UAL123 on the ground", f.Apply(State(true), Now + 60).Flight.Status(UnitSystem.Aviation));
    }
}
