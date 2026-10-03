using System.Text;
using TinyAtc.OpenSky;

namespace TinyAtc.Tests;

[TestClass]
public sealed class OpenSkyParserTests
{
    // Recorded from /states/all over Milwaukee, 2026-10-02.
    private const string StatesJson = """
        {"time":1790916618,"states":[
          ["a83953","SKW4766 ","United States",1790916617,1790916617,-87.9149,42.8281,769.62,false,81.36,6.9,-0.33,null,777.24,"1465",false,0],
          ["ac5e23","SWA4356 ","United States",1790916600,1790916600,-87.8976,42.931,205.74,false,75.17,7.08,-4.23,null,228.6,"1455",false,0],
          ["a06435","","United States",null,1790916417,null,null,null,true,0,null,null,null,null,null,false,0,3]
        ]}
        """;

    [TestMethod]
    public void ParsesStateVectorFieldsByPosition()
    {
        StatesSnapshot snapshot = OpenSkyParser.ParseStates(Encoding.UTF8.GetBytes(StatesJson));

        Assert.AreEqual(1790916618, snapshot.Time);
        Assert.HasCount(3, snapshot.States);

        StateVector s = snapshot.States[0];
        Assert.AreEqual("a83953", s.Icao24);
        Assert.AreEqual("SKW4766", s.DisplayName);
        Assert.AreEqual(42.8281, s.Latitude);
        Assert.AreEqual(-87.9149, s.Longitude);
        Assert.AreEqual(769.62, s.BaroAltitude);
        Assert.IsFalse(s.OnGround);
        Assert.AreEqual(6.9, s.TrueTrack);
        Assert.AreEqual(-0.33, s.VerticalRate);
        Assert.AreEqual("1465", s.Squawk);
    }

    [TestMethod]
    public void HandlesNullsAndTheOptionalCategory()
    {
        StateVector s = OpenSkyParser.ParseStates(Encoding.UTF8.GetBytes(StatesJson)).States[2];

        Assert.IsFalse(s.HasPosition);
        Assert.IsTrue(s.OnGround);
        Assert.AreEqual("A06435", s.DisplayName, "No callsign falls back to the ICAO address.");
        Assert.AreEqual(3, s.Category);
        Assert.AreEqual(0, s.Altitude);
    }

    [TestMethod]
    public void NullStatesIsAnEmptySnapshot()
    {
        StatesSnapshot snapshot = OpenSkyParser.ParseStates(Encoding.UTF8.GetBytes("""{"time":5,"states":null}"""));
        Assert.IsEmpty(snapshot.States);
    }

    [TestMethod]
    public void ParsesTrackWithExponentTimes()
    {
        const string json = """
            {"icao24":"a83953","callsign":"SKW4766 ","startTime":1.790910002E9,"endTime":1.790916616E9,"path":[
              [1790910002,39.8775,-104.6555,1524,90,false],
              [1790910038,null,null,1524,89,false],
              [1790910043,39.8775,-104.6196,1828,88,false]]}
            """;

        FlightTrack track = OpenSkyParser.ParseTrack(Encoding.UTF8.GetBytes(json));

        Assert.AreEqual("SKW4766", track.Callsign);
        Assert.AreEqual(1790910002, track.StartTime);
        Assert.HasCount(2, track.Path, "Waypoints without a position are skipped.");
        Assert.AreEqual(-104.6196, track.Path[1].Longitude);
    }

    [TestMethod]
    public void EstimatePositionMovesAlongTrackAndCaps()
    {
        StateVector s = OpenSkyParser.ParseStates(Encoding.UTF8.GetBytes(StatesJson)).States[0];

        // Heading 6.9° at 81 m/s: ten seconds later it's ~0.8 km further north.
        var later = s.EstimatePosition(1790916617 + 10);
        Assert.IsGreaterThan(s.Latitude!.Value, later.Latitude);

        var capped = s.EstimatePosition(1790916617 + 3600, maxSeconds: 30);
        var thirty = s.EstimatePosition(1790916617 + 30, maxSeconds: 30);
        Assert.AreEqual(thirty, capped);
    }
}
