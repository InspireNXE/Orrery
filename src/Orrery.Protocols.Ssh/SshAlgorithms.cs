using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;

namespace Orrery.Protocols.Ssh;

/// <summary>
/// Which algorithms a connection may negotiate. SSH.NET lists SHA-1, CBC and 3DES last, so a modern server never
/// picks them, but a server that offers nothing else would otherwise get a weak connection with no warning.
/// </summary>
static class SshAlgorithms
{
    internal const string AllowLegacyHint = "Allow older, weaker algorithms";

    static readonly string[] LegacyKeyExchange = ["diffie-hellman-group1-sha1", "diffie-hellman-group14-sha1", "diffie-hellman-group-exchange-sha1"];
    static readonly string[] LegacyEncryption = ["3des-cbc", "aes128-cbc", "aes192-cbc", "aes256-cbc"];
    static readonly string[] LegacyMac = ["hmac-sha1", "hmac-sha1-etm@openssh.com"];

    // The rsa-sha2 variants stay; only the SHA-1 signature is dropped.
    static readonly string[] LegacyHostKey = ["ssh-rsa"];

    /// <summary>Leaves SSH.NET's default lists alone when <paramref name="allowLegacy"/>, otherwise takes the weak entries out.</summary>
    internal static void Apply(ConnectionInfo info, bool allowLegacy)
    {
        if (allowLegacy)
        {
            return;
        }

        Remove(info.KeyExchangeAlgorithms, LegacyKeyExchange);
        Remove(info.Encryptions, LegacyEncryption);
        Remove(info.HmacAlgorithms, LegacyMac);
        Remove(info.HostKeyAlgorithms, LegacyHostKey);
    }

    static void Remove<T>(IDictionary<string, T> algorithms, string[] names)
    {
        foreach (var name in names)
        {
            algorithms.Remove(name);
        }
    }

    /// <summary>
    /// The text to show for a failed connection. A "no matching algorithm" refusal, while the weak ones are off,
    /// gets a pointer to the setting that turns them on; everything else keeps SSH.NET's message.
    /// </summary>
    internal static string ExplainFailure(SshConnectionException e, bool allowLegacy)
    {
        // SSH.NET's wording; compression isn't something the setting changes.
        if (!allowLegacy && e.DisconnectReason == DisconnectReason.KeyExchangeFailed
            && e.Message.StartsWith("No matching ", StringComparison.Ordinal)
            && e.Message.Contains(" algorithm (server offers ", StringComparison.Ordinal)
            && !e.Message.Contains("compression", StringComparison.Ordinal))
        {
            return $"{e.Message}. The server may only offer older algorithms; if you trust it, turn on \"{AllowLegacyHint}\" in the connection's settings.";
        }

        return e.Message;
    }
}
