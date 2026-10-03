using TinyAtc.OpenSky;

namespace TinyAtc.Tests;

[TestClass]
public sealed class AircraftKindTests
{
    [TestMethod]
    [DataRow(0, AircraftKind.Jet)]
    [DataRow(1, AircraftKind.Jet)]
    [DataRow(3, AircraftKind.Jet)]
    [DataRow(4, AircraftKind.Jet)]
    [DataRow(5, AircraftKind.Jet)]
    [DataRow(7, AircraftKind.Jet)]
    [DataRow(2, AircraftKind.Light)]
    [DataRow(6, AircraftKind.Heavy)]
    [DataRow(8, AircraftKind.Helicopter)]
    [DataRow(9, AircraftKind.Other)]
    [DataRow(10, AircraftKind.Other)]
    [DataRow(14, AircraftKind.Other)]
    [DataRow(99, AircraftKind.Jet)]
    public void CategoryMapsToKind(int category, AircraftKind expected) =>
        Assert.AreEqual(expected, AircraftKinds.FromCategory(category));
}
