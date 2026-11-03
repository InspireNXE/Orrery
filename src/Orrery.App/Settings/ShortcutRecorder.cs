using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Orrery.App.Input;

namespace Orrery.App.Settings;

/// <summary>
/// Shows a shortcut; clicked, it listens for the next key press and offers it through <see cref="Recorded"/>.
/// Esc or clicking elsewhere stops listening and keeps the old shortcut.
/// </summary>
sealed class ShortcutRecorder : Button
{
    KeyGesture _gesture;

    public ShortcutRecorder(KeyGesture gesture)
    {
        _gesture = gesture;
        MinWidth = 200;
        HorizontalContentAlignment = HorizontalAlignment.Left;
        FontFamily = Dialogs.MonoFont;
        FontSize = 12;
        Avalonia.Automation.AutomationProperties.SetName(this, "Full screen shortcut");
        Click += (_, _) => StartRecording();
        ShowGesture();
    }

    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>A key press while listening; the owner checks it and calls <see cref="StopRecording"/> if it's kept.</summary>
    public event EventHandler<KeyGesture>? Recorded;

    public bool IsRecording { get; private set; }

    public KeyGesture Gesture
    {
        get => _gesture;
        set
        {
            _gesture = value;
            ShowGesture();
        }
    }

    void StartRecording()
    {
        IsRecording = true;
        Classes.Set("accent", true);
        Content = "Press the new shortcut…  (Esc cancels)";
        Focus();
    }

    public void StopRecording()
    {
        IsRecording = false;
        Classes.Set("accent", false);
        ShowGesture();
    }

    void ShowGesture() => Content = FullScreenShortcut.Describe(_gesture);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!IsRecording)
        {
            base.OnKeyDown(e);
            return;
        }

        // Every key is the shortcut's, including Enter and Space, which would otherwise click the button.
        e.Handled = true;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            StopRecording();
            return;
        }

        if (FullScreenShortcut.FromKeyPress(e.Key, e.KeyModifiers) is { } gesture)
        {
            Recorded?.Invoke(this, gesture);
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (IsRecording)
        {
            e.Handled = true;
            return;
        }

        base.OnKeyUp(e);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        if (IsRecording)
        {
            StopRecording();
        }
    }
}
