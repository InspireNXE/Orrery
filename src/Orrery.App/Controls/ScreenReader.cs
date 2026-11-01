using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace Orrery.App.Controls;

/// <summary>
/// Speaks short messages through the screen reader, for things that change without the focus moving: a terminal's
/// new output, the highlighted result in quick connect.
/// <para>
/// On Windows it raises a UI Automation notification on the window, which Narrator, NVDA and JAWS read out. Avalonia
/// 11.3 has no API for that, and its live regions only fire for elements a screen reader has already looked at, so
/// the notification is raised directly. Elsewhere <see cref="LiveText"/> is a live region, which is as far as
/// Avalonia goes on macOS; it has no screen reader support on Linux at all.
/// </para>
/// </summary>
static class ScreenReader
{
    const int SPI_GETSCREENREADER = 0x0046;
    const int NotificationKindOther = 4;

    /// <summary>Finish what's being read, then read only the latest: older ones that queued up meanwhile are dropped.</summary>
    public const int CurrentThenMostRecent = 4;

    /// <summary>Stop what's being read and read this.</summary>
    public const int MostRecent = 3;

    static bool _notificationsMissing;

    /// <summary>
    /// Whether a screen reader says it's running. Windows keeps a flag that Narrator, NVDA and JAWS set; macOS and
    /// Linux have no such flag Orrery can read, so there it's always false.
    /// </summary>
    public static bool IsRunning
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                return SystemParametersInfo(SPI_GETSCREENREADER, 0, out int running, 0) && running != 0;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>Whether terminals read out new output: the setting, or while a screen reader is running if it's unset.</summary>
    public static bool TerminalAnnouncements(Orrery.Core.Settings.AppSettings settings) => settings.TerminalAnnouncements ?? IsRunning;

    /// <summary>Raised for every message, for tests.</summary>
    internal static event Action<string>? Announced;

    /// <summary>
    /// Reads <paramref name="text"/> out in the window <paramref name="source"/> is in. <paramref name="activity"/>
    /// groups messages of one kind, so <paramref name="processing"/> can drop the older ones.
    /// </summary>
    public static void Announce(Control source, string text, string activity, int processing)
    {
        Announced?.Invoke(text);
        if (!OperatingSystem.IsWindows() || _notificationsMissing
            || TopLevel.GetTopLevel(source)?.TryGetPlatformHandle()?.Handle is not { } window || window == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!UiaClientsAreListening() || UiaHostProviderFromHwnd(window, out var provider) < 0 || provider == IntPtr.Zero)
            {
                return;
            }

            try
            {
                UiaRaiseNotificationEvent(provider, NotificationKindOther, processing, text, activity);
            }
            finally
            {
                Marshal.Release(provider);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows before 10 version 1709 has no notifications; the live region is all there is.
            _notificationsMissing = true;
        }
    }

    /// <summary>On Windows the notification does the talking, so the live region keeps quiet rather than read it twice.</summary>
    public static AutomationLiveSetting LiveRegionSetting => OperatingSystem.IsWindows() ? AutomationLiveSetting.Off : AutomationLiveSetting.Polite;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(int action, int param, out int value, int winIni);

    [DllImport("UIAutomationCore.dll")]
    static extern bool UiaClientsAreListening();

    [DllImport("UIAutomationCore.dll")]
    static extern int UiaHostProviderFromHwnd(IntPtr window, out IntPtr provider);

    [DllImport("UIAutomationCore.dll", CharSet = CharSet.Unicode)]
    static extern int UiaRaiseNotificationEvent(IntPtr provider, int kind, int processing,
        [MarshalAs(UnmanagedType.BStr)] string text, [MarshalAs(UnmanagedType.BStr)] string activity);
}

/// <summary>
/// An invisible element that holds the last thing said through <see cref="Say"/>, as a polite live region: a screen
/// reader that has found it reads each new text. On Windows it also raises <see cref="ScreenReader"/>'s notification.
/// </summary>
sealed class LiveText : Control
{
    string _text = string.Empty;

    public LiveText(string activity, int processing)
    {
        Activity = activity;
        Processing = processing;
        IsHitTestVisible = false;
        Focusable = false;
        AutomationProperties.SetLiveSetting(this, ScreenReader.LiveRegionSetting);
    }

    public string Activity { get; }
    public int Processing { get; }

    /// <summary>What was said last.</summary>
    public string Text => _text;

    public void Say(string text)
    {
        var old = _text;
        _text = text;
        (ControlAutomationPeer.FromElement(this) as Peer)?.Changed(old, text);
        ScreenReader.Announce(this, text, Activity, Processing);
    }

    protected override Size MeasureOverride(Size availableSize) => default;

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    sealed class Peer(LiveText owner) : ControlAutomationPeer(owner)
    {
        // Raised even when the text repeats: the same line of output twice is still worth hearing twice.
        public void Changed(string old, string text) => RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, old, text);

        protected override string GetNameCore() => owner._text;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override bool IsContentElementCore() => false;
    }
}
