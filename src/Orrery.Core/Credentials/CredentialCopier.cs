namespace Orrery.Core.Credentials;

public static class CredentialCopier
{
    /// <summary>
    /// Copies every credential, keeping its id so connections still find it, then reads each one back from
    /// <paramref name="to"/> to check it arrived intact. Both stores must be unlocked.
    /// </summary>
    /// <returns>The ids copied.</returns>
    /// <exception cref="InvalidDataException">A credential didn't read back the same.</exception>
    public static async Task<IReadOnlyList<Guid>> CopyAllAsync(ICredentialStore from, ICredentialStore to)
    {
        var copied = new List<Guid>();
        var ids = (await from.ListAsync()).Select(s => s.Id).Concat(Credential.ReservedIds).ToList();
        foreach (var id in ids)
        {
            if (await from.GetAsync(id) is not { } credential)
            {
                // Listed but its secret is gone (removed outside Orrery), or a reserved one this store never had: nothing to carry over.
                continue;
            }

            await to.SaveAsync(credential);
            if (await to.GetAsync(credential.Id) != credential)
            {
                throw new InvalidDataException($"The credential \"{credential.Name}\" didn't copy correctly.");
            }

            copied.Add(credential.Id);
        }

        return copied;
    }
}
