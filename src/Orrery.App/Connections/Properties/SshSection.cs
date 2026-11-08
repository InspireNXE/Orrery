using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Orrery.App.Controls;
using Orrery.Core.Connections;
using static Orrery.App.Connections.PropertyEditors;

namespace Orrery.App.Connections;

/// <summary>SSH: the agent, older algorithms, keep-alives and a SOCKS proxy. Its key file is with the credentials and its forwards with the tunnels.</summary>
sealed class SshSection : PropertiesSection
{
    /// <summary>An hour: anything longer wouldn't keep a session from being dropped as idle.</summary>
    const int MaxKeepAliveSeconds = 3600;

    readonly ToggleSwitch _sshAgent = Switch("UseSshAgent");
    readonly ToggleSwitch _sshLegacy = Switch("AllowLegacySsh");
    readonly TextBox _keepAlive = new() { FontFamily = Dialogs.MonoFont, PlaceholderText = SettingsResolver.DefaultKeepAliveSeconds.ToString(CultureInfo.InvariantCulture) };
    readonly TextBox _socksProxy = new() { FontFamily = Dialogs.MonoFont, PlaceholderText = "None" };
    readonly InheritRow _sshAgentRow, _sshLegacyRow, _keepAliveRow, _socksProxyRow;

    public SshSection(PropertiesContext context) : base(context)
    {
        _sshAgentRow = new InheritRow("Use SSH agent keys", _sshAgent, () => Context.ShowInherited(r => _sshAgent.IsChecked = r.UseSshAgent.Value));
        _sshAgentRow.Tip("SSH: log in with keys from Pageant or the OpenSSH agent when there is no saved credential.");
        _sshLegacyRow = new InheritRow("Allow older, weaker algorithms", _sshLegacy, () => Context.ShowInherited(r => _sshLegacy.IsChecked = r.AllowLegacySsh.Value));
        _sshLegacyRow.Tip("SSH: also accept SHA-1, CBC and 3DES algorithms.");
        var legacyHint = Hint("Only for old devices that can't connect otherwise", new Thickness(14, -4, 0, 4));
        _keepAliveRow = new InheritRow("Keep-alive (seconds)", _keepAlive,
            () => Context.ShowInherited(r => _keepAlive.Text = r.KeepAliveSeconds.Value.ToString(CultureInfo.InvariantCulture)));
        _keepAliveRow.Tip("SSH: how often to send the server a keep-alive message, so an idle session isn't dropped by a firewall or the server. 0 sends none. "
            + "Also used for the connection as a jump host.");
        _socksProxyRow = new InheritRow("SOCKS proxy", _socksProxy, () => Context.ShowInherited(r => _socksProxy.Text = r.SocksProxy.Value));
        _socksProxyRow.Tip("SSH: every SSH connection under this opens a SOCKS proxy on this port, like its own D port forward: a port, such as 1080, "
            + "or an address and a port, such as 127.0.0.1:1080. Only one open session can have the port; the others say so in their terminal.");
        View = Pane.RowSection("SSH", _sshAgentRow.View, _sshLegacyRow.View, legacyHint, _keepAliveRow.View, _socksProxyRow.View);
    }

    public override Control View { get; }

    public override IEnumerable<InheritRow> Rows => [_sshAgentRow, _sshLegacyRow, _keepAliveRow, _socksProxyRow];

    public override IEnumerable<Control> Fields => [_sshAgent, _sshLegacy, _keepAlive, _socksProxy];

    public override void Load(ConnectionNode node, InheritableSettings own, ResolvedSettings inherited)
    {
        _sshAgentRow.Load(own.UseSshAgent is null, Describe(inherited.UseSshAgent));
        _sshAgent.IsChecked = own.UseSshAgent ?? inherited.UseSshAgent.Value;
        _sshLegacyRow.Load(own.AllowLegacySsh is null, Describe(inherited.AllowLegacySsh));
        _sshLegacy.IsChecked = own.AllowLegacySsh ?? inherited.AllowLegacySsh.Value;
        _keepAliveRow.Load(own.KeepAliveSeconds is null, Describe(inherited.KeepAliveSeconds));
        _keepAlive.Text = (own.KeepAliveSeconds ?? inherited.KeepAliveSeconds.Value).ToString(CultureInfo.InvariantCulture);
        _socksProxyRow.Load(own.SocksProxy is null, Describe(inherited.SocksProxy));
        _socksProxy.Text = own.SocksProxy ?? inherited.SocksProxy.Value;
    }

    public override string? Save(InheritableSettings settings)
    {
        int? keepAlive = null;
        if (!_keepAliveRow.Inheriting)
        {
            if (!int.TryParse(_keepAlive.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > MaxKeepAliveSeconds)
            {
                return $"The keep-alive interval must be a number of seconds from 0 (none) to {MaxKeepAliveSeconds}.";
            }

            keepAlive = seconds;
        }

        string? socksProxy = null;
        if (!_socksProxyRow.Inheriting)
        {
            // Empty with Inherit off means none, overriding a folder's.
            socksProxy = _socksProxy.Text?.Trim() ?? string.Empty;
            if (socksProxy.Length > 0 && PortForward.TryParseSocks(socksProxy, out var socksError) is null)
            {
                return socksError!;
            }
        }

        settings.UseSshAgent = _sshAgentRow.Inheriting ? null : _sshAgent.IsChecked == true;
        settings.AllowLegacySsh = _sshLegacyRow.Inheriting ? null : _sshLegacy.IsChecked == true;
        settings.KeepAliveSeconds = keepAlive;
        settings.SocksProxy = socksProxy;
        return null;
    }
}
