namespace Orrery.Core.Sessions;

/// <summary>Which lock keys are on, with the values RDP's synchronize event gives them.</summary>
[Flags]
public enum LockKeys
{
    None = 0,
    ScrollLock = 0x01,
    NumLock = 0x02,
    CapsLock = 0x04,
}

/// <summary>
/// A session that can set the remote computer's lock keys outright, without pressing them, as RDP's synchronize event
/// does. The view sends this computer's state each time the session gets the keyboard, so Caps Lock can't drift apart.
/// </summary>
public interface ILockKeysSession
{
    void SyncLockKeys(LockKeys keys);
}
