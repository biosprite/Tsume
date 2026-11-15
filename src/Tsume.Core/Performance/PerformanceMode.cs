namespace Tsume.Core.Performance;

/// <summary>
/// The four ways the Claw can balance speed against battery life.
/// Ordered from quietest to loudest so the UI can lay them out left to right.
/// </summary>
public enum PerformanceMode
{
    /// <summary>Lowest power draw. MSI calls this "Super Battery".</summary>
    Endurance,

    /// <summary>The everyday default.</summary>
    Balanced,

    /// <summary>Highest stock power limits.</summary>
    Performance,

    /// <summary>The user picks the power limits by hand.</summary>
    Manual,
}
