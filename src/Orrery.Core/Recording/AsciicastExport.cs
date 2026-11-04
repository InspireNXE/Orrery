using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orrery.Core.Recording;

/// <summary>
/// Writes a terminal recording as asciicast v2 (<c>.cast</c>), which asciinema plays (design §3.4). Output is decoded
/// as UTF-8 across events, with invalid bytes as U+FFFD, since the format holds text.
/// </summary>
public static class AsciicastExport
{
    /// <returns>How reading the recording ended.</returns>
    public static RecordingEnding Write(RecordingReader reader, TextWriter output, CancellationToken cancel = default)
    {
        if (reader.Header.Kind != RecordingKind.Terminal)
        {
            throw new InvalidOperationException("Only terminal recordings export as .cast.");
        }

        var decoder = new UTF8Encoding(false, false).GetDecoder();
        RecordingInfo? info = null;
        bool wroteHeader = false;
        var chars = new char[RecordingFile.ChunkSize];
        void Header(int columns, int rows)
        {
            var header = new Dictionary<string, object>
            {
                ["version"] = 2,
                ["width"] = columns,
                ["height"] = rows,
            };
            if (info is not null)
            {
                header["timestamp"] = info.Started.ToUnixTimeSeconds();
                header["title"] = info.Name;
            }

            output.Write(JsonSerializer.Serialize(header));
            output.Write('\n');
            wroteHeader = true;
        }

        void Event(TimeSpan time, string code, string data)
        {
            output.Write('[');
            output.Write(time.TotalSeconds.ToString("0.000###", CultureInfo.InvariantCulture));
            output.Write(", ");
            output.Write(JsonSerializer.Serialize(code));
            output.Write(", ");
            output.Write(JsonSerializer.Serialize(data));
            output.Write("]\n");
        }

        foreach (var e in reader.Events())
        {
            cancel.ThrowIfCancellationRequested();
            switch (e.Type)
            {
                case RecordingEventType.Info:
                    info = RecordingFile.ReadInfo(e.Payload);
                    break;
                case RecordingEventType.Resize when RecordingFile.ReadPair(e.Payload) is var (columns, rows):
                    if (!wroteHeader)
                    {
                        Header(columns, rows);
                    }
                    else
                    {
                        Event(e.Time, "r", $"{columns}x{rows}");
                    }

                    break;
                case RecordingEventType.Output:
                    if (!wroteHeader)
                    {
                        Header(80, 24);
                    }

                    if (chars.Length < e.Payload.Length + 4)
                    {
                        chars = new char[e.Payload.Length + 4];
                    }

                    int count = decoder.GetChars(e.Payload, 0, e.Payload.Length, chars, 0, flush: false);
                    if (count > 0)
                    {
                        Event(e.Time, "o", new string(chars, 0, count));
                    }

                    break;
                case RecordingEventType.Marker when wroteHeader:
                    Event(e.Time, "m", Encoding.UTF8.GetString(e.Payload));
                    break;
                case RecordingEventType.Input:
                    if (!wroteHeader)
                    {
                        Header(80, 24);
                    }

                    Event(e.Time, "i", Encoding.UTF8.GetString(e.Payload));
                    break;
            }
        }

        if (!wroteHeader)
        {
            Header(80, 24);
        }

        return reader.Ending;
    }
}
