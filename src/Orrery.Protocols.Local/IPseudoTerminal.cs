namespace Orrery.Protocols.Local;

/// <summary>
/// A program running on a pseudo-terminal: ConPTY on Windows, a Unix pty elsewhere. Reads and the exit wait
/// block, so each runs on a thread of its own; <see cref="Write"/> comes from one more.
/// </summary>
interface IPseudoTerminal : IDisposable
{
    int ProcessId { get; }

    /// <summary>The program's output, as it would reach a terminal. 0 once there's no more.</summary>
    int Read(Span<byte> buffer);

    void Write(ReadOnlySpan<byte> data);

    void Resize(int columns, int rows);

    /// <summary>Blocks until the program exits and returns its exit code.</summary>
    int WaitForExit();

    /// <summary>
    /// After the program exits: lets <see cref="Read"/> return what's left and then 0, even if something the
    /// program started still holds the terminal open.
    /// </summary>
    void EndOutput();

    /// <summary>Ends the program and everything it started.</summary>
    void Kill();
}
