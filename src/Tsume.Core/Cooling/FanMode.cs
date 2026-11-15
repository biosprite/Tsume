namespace Tsume.Core.Cooling;

/// <summary>How the fans decide their speed.</summary>
public enum FanMode
{
    /// <summary>The firmware follows its built-in temperature curve.</summary>
    Auto,

    /// <summary>The firmware follows a curve supplied by Tsume.</summary>
    Custom,

    /// <summary>Fans pinned at full speed. MSI calls this "Cooler Boost".</summary>
    Max,
}
