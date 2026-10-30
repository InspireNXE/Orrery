namespace Orrery.Core.Storage;

/// <summary>
/// Holds <c>.lock</c> in the data folder for the life of the app, so two copies of Orrery never write
/// the same files. The OS releases the lock if the process dies.
/// </summary>
public sealed class DataFolderLock : IDisposable
{
    readonly FileStream _stream;

    DataFolderLock(FileStream stream) => _stream = stream;

    /// <returns>Null if another Orrery instance already holds the folder.</returns>
    public static DataFolderLock? TryAcquire(string dataDir)
    {
        PrivateStorage.CreateDirectory(dataDir);
        PrivateStorage.RestrictDirectory(dataDir);
        try
        {
            var stream = new FileStream(Path.Combine(dataDir, ".lock"), PrivateStorage.Private(new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 1,
                Options = FileOptions.DeleteOnClose,
            }));
            return new DataFolderLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
