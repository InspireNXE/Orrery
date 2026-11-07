namespace Orrery.App.Sessions;

/// <summary>
/// A window that can show a session full screen: the main window and a popped-out session's. Full screen over several
/// displays leaves the window in its normal state, so its WindowState doesn't say.
/// </summary>
interface IFullScreenHost
{
    bool IsFullScreen { get; }

    /// <summary>The window went in or out of full screen.</summary>
    event EventHandler? FullScreenChanged;
}
