using System.Text;
using System.Text.Json;
using Orrery.Core.Recording;
using Xunit;

namespace Orrery.Core.Tests.Recording;

public sealed class TypingTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly byte[] _privateKey;
    readonly byte[] _publicKey;
    TimeSpan _now;

    public TypingTests()
    {
        Directory.CreateDirectory(_dir);
        (_privateKey, _publicKey) = RecordingTestKeys.New();
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static readonly RecordingInfo Info = new("web-01", "SSH", DateTimeOffset.UnixEpoch);

    string NewPath() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".orrec");

    async Task<(int Version, List<RecordedEvent> Events)> RecordAsync(bool typing, Action<TerminalRecorder, FakeTerminal> run)
    {
        var session = new FakeTerminal();
        var recorder = new TerminalRecorder(RecordingWriter.Create(NewPath(), RecordingKind.Terminal, _publicKey, typing), Info, 80, 24, long.MaxValue,
            () => _now, recordTyping: typing);
        recorder.Attach(session);
        run(recorder, session);
        recorder.Stop();
        await recorder.Completion;

        using var reader = RecordingReader.Open(recorder.Path, _privateKey);
        var events = reader.Events().ToList();
        Assert.Equal(RecordingEnding.Closed, reader.Ending);
        return (reader.Header.Version, events);
    }

    static List<string> Typed(List<RecordedEvent> events) =>
        [.. events.Where(e => e.Type == RecordingEventType.Input).Select(e => Encoding.UTF8.GetString(e.Payload))];

    [Fact]
    public async Task Typing_is_recorded_with_its_time_in_a_version_2_file_when_asked()
    {
        var (version, events) = await RecordAsync(typing: true, (recorder, session) =>
        {
            _now = TimeSpan.FromSeconds(1);
            recorder.Typed("ls -la\r", atPasswordPrompt: false);
            session.Output("total 0\r\n");
        });

        Assert.Equal(RecordingFile.VersionWithTyping, version);
        var input = Assert.Single(events, e => e.Type == RecordingEventType.Input);
        Assert.Equal("ls -la\r", Encoding.UTF8.GetString(input.Payload));
        Assert.Equal(TimeSpan.FromSeconds(1), input.Time);
    }

    [Fact]
    public async Task Without_the_setting_nothing_typed_is_kept_and_the_file_stays_version_1()
    {
        var (version, events) = await RecordAsync(typing: false, (recorder, _) => recorder.Typed("secret stuff\r", atPasswordPrompt: false));

        Assert.Equal(RecordingFile.Version, version);
        Assert.Empty(Typed(events));
        Assert.DoesNotContain(events, e => Encoding.UTF8.GetString(e.Payload).Contains("secret"));
    }

    [Fact]
    public async Task Typing_at_a_password_prompt_is_left_out_with_one_marker_per_prompt()
    {
        var (_, events) = await RecordAsync(typing: true, (recorder, _) =>
        {
            recorder.Typed("sudo -i\r", atPasswordPrompt: false);
            foreach (var key in new[] { "h", "u", "n", "t", "e", "r", "2", "\r" })
            {
                recorder.Typed(key, atPasswordPrompt: true);
            }

            recorder.Typed("whoami\r", atPasswordPrompt: false);
            recorder.Typed("x", atPasswordPrompt: true);
        });

        Assert.Equal(["sudo -i\r", "whoami\r"], Typed(events));
        Assert.Equal(2, events.Count(e => e.Type == RecordingEventType.Marker && Encoding.UTF8.GetString(e.Payload) == TerminalRecorder.TypingHeldMarker));
        Assert.DoesNotContain(events, e => e.Type == RecordingEventType.Input && Encoding.UTF8.GetString(e.Payload).Length == 1);
    }

    [Fact]
    public void A_version_1_recording_still_opens()
    {
        var path = NewPath();
        using (var writer = RecordingWriter.Create(path, RecordingKind.Terminal, _publicKey))
        {
            writer.Append(RecordingEventType.Info, TimeSpan.Zero, RecordingFile.InfoPayload(Info));
            writer.Complete(TimeSpan.FromSeconds(1));
        }

        using var reader = RecordingReader.Open(path, _privateKey);
        Assert.Equal(1, reader.Header.Version);
        Assert.Single(reader.Events(), e => e.Type == RecordingEventType.Info);
        Assert.Equal(RecordingEnding.Closed, reader.Ending);
    }

    [Fact]
    public void A_cast_export_has_what_was_typed_as_input_events()
    {
        var path = NewPath();
        using (var writer = RecordingWriter.Create(path, RecordingKind.Terminal, _publicKey, typing: true))
        {
            writer.Append(RecordingEventType.Info, TimeSpan.Zero, RecordingFile.InfoPayload(Info));
            writer.Append(RecordingEventType.Resize, TimeSpan.Zero, RecordingFile.Pair(80, 24));
            writer.Append(RecordingEventType.Input, TimeSpan.FromSeconds(1), "uptime\r"u8);
            writer.Complete(TimeSpan.FromSeconds(2));
        }

        using var reader = RecordingReader.Open(path, _privateKey);
        var text = new StringWriter();
        AsciicastExport.Write(reader, text);

        var events = text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => JsonSerializer.Deserialize<JsonElement[]>(l)!).ToList();
        var input = Assert.Single(events);
        Assert.Equal("i", input[1].GetString());
        Assert.Equal("uptime\r", input[2].GetString());
    }
}
