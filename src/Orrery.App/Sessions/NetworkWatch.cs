using System.Net.NetworkInformation;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;

namespace Orrery.App.Sessions;

/// <summary>
/// Tells the window when the OS says the network is back, so sessions waiting to reconnect can go straight away.
/// Addresses change several times while a network comes up, so <see cref="Back"/> is raised once, on the UI thread,
/// after things have been quiet for <see cref="Settle"/>.
/// </summary>
sealed class NetworkWatch : IDisposable
{
    static readonly ILogger Logger = Log.For("Network");
    internal static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    readonly DispatcherTimer _settle = new() { Interval = Settle };
    bool _wasAvailable = IsAvailable();
    bool _watching;

    public NetworkWatch()
    {
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            if (IsAvailable())
            {
                Back?.Invoke(this, EventArgs.Empty);
            }
        };

        try
        {
            NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
            NetworkChange.NetworkAddressChanged += OnAddressChanged;
            _watching = true;
        }
        catch (Exception e) when (e is PlatformNotSupportedException or NetworkInformationException)
        {
            Logger.LogWarning("Can't watch for network changes, so reconnecting only follows its own timer: {Message}", e.Message);
        }
    }

    /// <summary>The network came back (or changed while up), on the UI thread.</summary>
    public event EventHandler? Back;

    /// <summary>Whether the OS has a network connection up. Assumed so when it can't say.</summary>
    public static bool IsAvailable()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch (Exception e) when (e is PlatformNotSupportedException or NetworkInformationException)
        {
            return true;
        }
    }

    void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Changed(e.IsAvailable);

    void OnAddressChanged(object? sender, EventArgs e) => Changed(IsAvailable());

    /// <summary>On any thread. Only a network coming up, or an address change while up (a new Wi-Fi, a VPN), counts.</summary>
    internal void Changed(bool available)
    {
        bool was = _wasAvailable;
        _wasAvailable = available;
        if (!available)
        {
            return;
        }

        Logger.Log(was ? LogLevel.Debug : LogLevel.Information, was ? "The network changed" : "The network is back");
        Dispatcher.UIThread.Post(() =>
        {
            _settle.Stop();
            _settle.Start();
        });
    }

    public void Dispose()
    {
        if (_watching)
        {
            NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
            NetworkChange.NetworkAddressChanged -= OnAddressChanged;
            _watching = false;
        }

        Dispatcher.UIThread.Post(_settle.Stop);
    }
}
