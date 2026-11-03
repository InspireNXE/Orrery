using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Orrery.App.Updates;
using Orrery.Core.Settings;
using Orrery.Core.Updates;

namespace Orrery.App;

// Updates: the bar above the tabs, checking by hand, and the restart that finishes an update.
public partial class MainWindow : IUpdateHost
{
    UpdateController? _updates;
    ContentControl _updateBarHost = null!;

    // A restart to finish an update is under way: the open tabs reopen at the next start without asking.
    bool _reopenAfterUpdate;

    void InitUpdates(IUpdateService? service)
    {
        _updateBarHost = this.FindControl<ContentControl>("UpdateBarHost")!;
        if (service is null)
        {
            return;
        }

        _updates = new UpdateController(service, _settingsStore) { Host = this };
        _updateBarHost.Content = new UpdateBar(_updates);
    }

    /// <summary>Checking by hand, from the palette: the details if there's a newer release, a short note if not.</summary>
    internal async Task CheckForUpdatesAsync()
    {
        if (_updates is null)
        {
            return;
        }

        ShowNotice("Checking for updates…");
        UpdateOffer? offer;
        try
        {
            offer = await _updates.CheckNowAsync();
        }
        catch (UpdateException e)
        {
            ShowNotice("Couldn't check for updates");
            await Dialogs.ShowMessageAsync(this, "Couldn't check for updates", e.Message);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (offer is null)
        {
            ShowNotice($"You have the latest version ({_updates.CurrentVersion})");
            return;
        }

        ShowNotice(_updates.BarText);
        await _updates.ShowDetailsAsync();
    }

    Window IUpdateHost.Owner => this;

    int IUpdateHost.OpenSessions => _tabs.Count;

    bool IUpdateHost.ReopensSessions => _settingsStore.Load().RestoreMode != RestoreSessionsMode.Never;

    void IUpdateHost.PrepareToRestart(bool restarting)
    {
        _reopenAfterUpdate = restarting;
        _properties.TrySave();
        SaveNow();
        WriteWorkspace();
    }

    void IUpdateHost.Quit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Close();
        }
    }

    void IUpdateHost.ShowNotice(string text) => ShowNotice(text);
}
