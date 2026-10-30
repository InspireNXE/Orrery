namespace Orrery.App.Sessions;

/// <summary>A key on the remote computer: its scancode for RDP and its X11 keysym for VNC.</summary>
public readonly record struct RemoteKey(ushort Scancode, uint Keysym)
{
    public static readonly RemoteKey Ctrl = new(0x1D, 0xFFE3);
    public static readonly RemoteKey Alt = new(0x38, 0xFFE9);
    public static readonly RemoteKey Shift = new(0x2A, 0xFFE1);
    public static readonly RemoteKey Windows = new(0xE05B, 0xFFEB);
    public static readonly RemoteKey Delete = new(0xE053, 0xFFFF);
    public static readonly RemoteKey Escape = new(0x01, 0xFF1B);
    public static readonly RemoteKey Tab = new(0x0F, 0xFF09);
    public static readonly RemoteKey F4 = new(0x3E, 0xFFC1);
    public static readonly RemoteKey PrintScreen = new(0xE037, 0xFF61);
    public static readonly RemoteKey R = new(0x13, 0x0072);
}

/// <summary>Keys pressed together and let go, for shortcuts the local computer would otherwise keep.</summary>
public sealed record RemoteKeyCombo(string Label, params RemoteKey[] Keys)
{
    public static readonly RemoteKeyCombo CtrlAltDel = new("Ctrl+Alt+Del", RemoteKey.Ctrl, RemoteKey.Alt, RemoteKey.Delete);

    /// <summary>What the session toolbar's Send keys menu offers.</summary>
    public static readonly IReadOnlyList<RemoteKeyCombo> Menu =
    [
        CtrlAltDel,
        new("Ctrl+Shift+Esc (Task Manager)", RemoteKey.Ctrl, RemoteKey.Shift, RemoteKey.Escape),
        new("Alt+Tab", RemoteKey.Alt, RemoteKey.Tab),
        new("Alt+F4", RemoteKey.Alt, RemoteKey.F4),
        new("Windows key", RemoteKey.Windows),
        new("Windows+R (Run)", RemoteKey.Windows, RemoteKey.R),
        new("Print Screen", RemoteKey.PrintScreen),
    ];
}
