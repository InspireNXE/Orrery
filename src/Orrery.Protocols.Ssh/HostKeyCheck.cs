using System.Diagnostics;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Orrery.Protocols.Ssh;

/// <summary>
/// Checks the host key a client is offered against <see cref="Target"/>'s real name, not the loopback address it
/// may be reached through. SSH.NET asks synchronously on its own thread, partway through connecting, so the time
/// someone spends reading the fingerprint counts against its connect timeout. When a trusted answer took long
/// enough for that to run out, <see cref="Connect"/> connects again and takes the same key without asking twice.
/// </summary>
internal sealed class HostKeyCheck
{
    readonly HostKeyValidator _hostKeys;
    readonly TimeSpan _slowAnswer;
    string? _accepted;
    bool _answeredSlowly;

    internal HostKeyCheck(HostKeyValidator hostKeys, ConnectionTarget target, TimeSpan? slowAnswer = null)
    {
        _hostKeys = hostKeys;
        Target = target;
        _slowAnswer = slowAnswer ?? TimeSpan.FromSeconds(1);
    }

    public ConnectionTarget Target { get; }

    /// <summary>The key was turned down, so the connection failing is that and not a network fault.</summary>
    public bool Rejected { get; private set; }

    /// <summary>Checks the keys <paramref name="client"/> is offered.</summary>
    public static HostKeyCheck Attach(BaseClient client, HostKeyValidator hostKeys, ConnectionTarget target)
    {
        var check = new HostKeyCheck(hostKeys, target);
        client.HostKeyReceived += (_, e) => e.CanTrust = check.Trusts(e.HostKeyName, e.FingerPrintSHA256);
        return check;
    }

    /// <summary>Whether a key is trusted, asking if it isn't pinned. A key accepted once isn't asked about again.</summary>
    internal bool Trusts(string keyType, string sha256Fingerprint)
    {
        if (string.Equals(_accepted, sha256Fingerprint, StringComparison.Ordinal))
        {
            return true;
        }

        var asked = Stopwatch.StartNew();
        bool trusted = _hostKeys.ValidateAsync(Target.Host, Target.Port, keyType, sha256Fingerprint).GetAwaiter().GetResult();
        Rejected = !trusted;
        if (trusted)
        {
            _accepted = sha256Fingerprint;
            _answeredSlowly = asked.Elapsed >= _slowAnswer;
        }

        return trusted;
    }

    /// <summary>Whether a timeout is owed to the user taking their time over the key, and is worth one more try.</summary>
    internal bool RetryAfter(Exception e)
    {
        if (e is not SshOperationTimeoutException || !_answeredSlowly)
        {
            return false;
        }

        _answeredSlowly = false;
        return true;
    }

    /// <summary>Connects <paramref name="client"/>, once more if the first try ran out of time while the user decided.</summary>
    public void Connect(BaseClient client)
    {
        try
        {
            client.Connect();
        }
        catch (Exception e) when (RetryAfter(e))
        {
            client.Connect();
        }
    }
}
