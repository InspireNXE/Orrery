using Orrery.Core.Connections;
using Orrery.Core.Settings;

namespace Orrery.App.Sessions;

/// <summary>
/// A remote desktop's view, whatever draws it: what the window's session menu and toolbar, the session settings,
/// the status line, color correction and Alt+Tab handling ask of one. <see cref="RemoteFrameView"/> (RDP, VNC) is
/// one; how it spreads over several displays and takes screenshots stays its own.
/// </summary>
interface IDesktopView
{
    /// <summary>Watching only: no keys, clicks or clipboard go to the remote.</summary>
    bool ViewOnly { get; set; }

    /// <summary>How the desktop is fitted into the tab when it doesn't follow its size.</summary>
    DesktopScaling Scaling { get; set; }

    /// <summary>The server resizes the desktop to the view, so <see cref="Scaling"/> has nothing to do.</summary>
    bool ResizesToView { get; }

    event EventHandler? ResizesToViewChanged;

    /// <summary>Raised when the remote desktop's size changes, for the status line.</summary>
    event EventHandler? DesktopResized;

    /// <summary>The connection's "Color correction": whether the app's correction applies to this desktop.</summary>
    bool AllowsColorCorrection { get; set; }

    /// <summary>The app's color correction, applied when <see cref="AllowsColorCorrection"/>.</summary>
    ColorCorrection? Correction { set; }

    /// <summary>Where Alt+Tab and the like go, as the connection saves it and as this session has it.</summary>
    SystemShortcuts SavedShortcuts { get; }

    SystemShortcuts SessionShortcuts { get; set; }

    /// <summary>Takes up a changed app setting for where Alt+Tab goes.</summary>
    void RefreshShortcuts();

    void SendKeys(RemoteKeyCombo combo);
}
