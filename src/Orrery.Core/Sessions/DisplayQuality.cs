namespace Orrery.Core.Sessions;

/// <summary>
/// How a remote desktop trades looks for speed. RDP turns the remote desktop's visual effects on or off; VNC
/// asks for higher or lower JPEG quality. HDR isn't among the choices: neither protocol can carry it.
/// </summary>
public enum DisplayQuality
{
    /// <summary>Font smoothing and wallpaper, without window contents while dragging or animations; full-quality VNC.</summary>
    Balanced,

    /// <summary>RDP: every visual effect, desktop composition included. VNC is already at full quality in <see cref="Balanced"/>.</summary>
    Best,

    /// <summary>For slow links: RDP without wallpaper, themes or font smoothing; VNC with lower JPEG quality and colour detail.</summary>
    Fastest,
}
