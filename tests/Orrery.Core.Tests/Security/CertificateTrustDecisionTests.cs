using Orrery.Core.Security;
using Xunit;

namespace Orrery.Core.Tests.Security;

public class CertificateTrustDecisionTests
{
    // Closing a trust prompt returns default(CertificateTrustDecision), so the default has to refuse.
    [Fact]
    public void The_default_decision_is_to_reject()
    {
        Assert.Equal(CertificateTrustDecision.Reject, default);
        Assert.Equal(0, (int)CertificateTrustDecision.Reject);
    }
}
