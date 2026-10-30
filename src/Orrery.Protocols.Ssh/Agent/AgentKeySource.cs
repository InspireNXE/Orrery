using Renci.SshNet;
using Renci.SshNet.Security;

namespace Orrery.Protocols.Ssh.Agent;

/// <summary>
/// One agent key as SSH.NET sees a private key: the algorithms it can sign with, where signing is done by
/// the agent. RSA keys offer rsa-sha2-512 and rsa-sha2-256 before the old SHA-1 ssh-rsa, as OpenSSH does.
/// </summary>
sealed class AgentKeySource(SshAgentClient agent, AgentIdentity identity) : IPrivateKeySource
{
    // SSH_AGENT_RSA_SHA2_256 and _512: ask the agent for a SHA-2 RSA signature.
    const uint RsaSha256 = 2, RsaSha512 = 4;

    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; } = Algorithms(agent, identity);

    public AgentIdentity Identity => identity;

    static HostAlgorithm[] Algorithms(SshAgentClient agent, AgentIdentity identity)
    {
        // Certificates name their algorithm after the key type, e.g. ssh-rsa-cert-v01@openssh.com.
        const string rsa = "ssh-rsa";
        if (identity.KeyType == rsa || identity.KeyType.StartsWith(rsa + "-cert-", StringComparison.Ordinal))
        {
            var suffix = identity.KeyType[rsa.Length..];
            return
            [
                new AgentHostAlgorithm("rsa-sha2-512" + suffix, agent, identity, RsaSha512),
                new AgentHostAlgorithm("rsa-sha2-256" + suffix, agent, identity, RsaSha256),
                new AgentHostAlgorithm(identity.KeyType, agent, identity, 0),
            ];
        }

        return [new AgentHostAlgorithm(identity.KeyType, agent, identity, 0)];
    }
}

/// <summary>Signs through the agent. <see cref="HostAlgorithm.Data"/> is the public key the server checks.</summary>
sealed class AgentHostAlgorithm(string name, SshAgentClient agent, AgentIdentity identity, uint flags) : HostAlgorithm(name)
{
    public override byte[] Data => identity.PublicKey;

    public override byte[] Sign(byte[] data) =>
        agent.Sign(identity.PublicKey, data, flags)
        ?? throw new Renci.SshNet.Common.SshException($"The SSH agent declined to sign with \"{identity.Comment}\".");

    // Only the server verifies.
    public override bool VerifySignature(byte[] data, byte[] signature) => throw new NotSupportedException();
}
