namespace Tsume.App.Platform;

/// <summary>What the Claw's Game Bar / Quick Settings button opens when pressed.</summary>
public enum GameBarAction
{
    /// <summary>Leave Win+G alone so Windows opens Xbox Game Bar as normal.</summary>
    Default = 0,

    /// <summary>Suppress Game Bar and toggle the Tsume overlay.</summary>
    OpenTsume = 1,

    /// <summary>Suppress Game Bar and open Steam Big Picture.</summary>
    SteamBigPicture = 2,

    /// <summary>Suppress Game Bar and open Task Manager.</summary>
    TaskManager = 3,

    /// <summary>Suppress Game Bar and toggle the Windows touch keyboard.</summary>
    TouchKeyboard = 4,

    /// <summary>Suppress Game Bar and capture a screenshot.</summary>
    Screenshot = 5,

    /// <summary>Suppress Game Bar and show the desktop / exit FSE.</summary>
    ShowDesktop = 6,

    /// <summary>Suppress Game Bar and do nothing.</summary>
    Disabled = 7,
}
