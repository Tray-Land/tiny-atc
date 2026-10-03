namespace TinyAtc.OpenSky;

/// <summary>How the map draws an aircraft. Coarser than ADS-B's emitter category.</summary>
public enum AircraftKind
{
    /// <summary>Category unknown or an ordinary airliner-sized aircraft.</summary>
    Jet,
    Heavy,
    Light,
    Helicopter,
    /// <summary>Balloons, gliders, skydivers, ultralights, drones and anything else that is not a powered plane.</summary>
    Other,
}

public static class AircraftKinds
{
    /// <summary>Maps OpenSky's ADS-B emitter category (state vector index 17) to a drawing kind.</summary>
    public static AircraftKind FromCategory(int category) => category switch
    {
        2 => AircraftKind.Light,
        6 => AircraftKind.Heavy,
        8 => AircraftKind.Helicopter,
        9 or 10 or 11 or 12 or 14 => AircraftKind.Other,
        _ => AircraftKind.Jet,
    };
}
