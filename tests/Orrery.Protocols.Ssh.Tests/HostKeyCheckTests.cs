using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Renci.SshNet.Common;
using Xunit;

namespace Orrery.Protocols.Ssh.Tests;

public class HostKeyCheckTests : IDisposable
{
    const string Key = "iG1UtG1/tgfZnr8tDLjYLSpLcC1VMNzIhRaoA3QTb9M";

    readonly string _path = Path.Combine(Path.GetTempPath(), $"orrery-known-hosts-{Guid.NewGuid():N}.json");
    readonly ConnectionTarget _target = new("server.example", 22, "me", null);
    int _asked;

    HostKeyCheck Check(CertificateTrustDecision answer, TimeSpan? takes = null) => new(
        new HostKeyValidator(new JsonCertificateTrustStore(_path), async _ =>
        {
            _asked++;
            if (takes is { } delay) await Task.Delay(delay);
            return answer;
        }),
        _target,
        slowAnswer: TimeSpan.FromMilliseconds(100));

    [Fact]
    public void A_key_accepted_once_isnt_asked_about_again()
    {
        var check = Check(CertificateTrustDecision.TrustOnce);

        Assert.True(check.Trusts("ssh-ed25519", Key));
        Assert.True(check.Trusts("ssh-ed25519", Key));
        Assert.Equal(1, _asked);
    }

    [Fact]
    public void A_different_key_on_the_retry_is_asked_about()
    {
        var check = Check(CertificateTrustDecision.TrustOnce);

        check.Trusts("ssh-ed25519", Key);
        check.Trusts("ssh-ed25519", "another");
        Assert.Equal(2, _asked);
    }

    [Fact]
    public void A_timeout_after_a_slow_answer_is_retried_once()
    {
        var check = Check(CertificateTrustDecision.TrustOnce, takes: TimeSpan.FromMilliseconds(300));
        check.Trusts("ssh-ed25519", Key);

        Assert.True(check.RetryAfter(new SshOperationTimeoutException()));
        Assert.False(check.RetryAfter(new SshOperationTimeoutException()));
    }

    [Fact]
    public void A_timeout_after_a_quick_answer_is_a_real_timeout()
    {
        var check = Check(CertificateTrustDecision.TrustOnce);
        check.Trusts("ssh-ed25519", Key);

        Assert.False(check.RetryAfter(new SshOperationTimeoutException()));
    }

    [Fact]
    public void A_rejected_key_is_never_retried()
    {
        var check = Check(CertificateTrustDecision.Reject, takes: TimeSpan.FromMilliseconds(300));

        Assert.False(check.Trusts("ssh-ed25519", Key));
        Assert.True(check.Rejected);
        Assert.False(check.RetryAfter(new SshOperationTimeoutException()));
    }

    [Fact]
    public void Only_timeouts_are_retried()
    {
        var check = Check(CertificateTrustDecision.TrustOnce, takes: TimeSpan.FromMilliseconds(300));
        check.Trusts("ssh-ed25519", Key);

        Assert.False(check.RetryAfter(new SshConnectionException("closed")));
    }

    public void Dispose() => File.Delete(_path);
}
