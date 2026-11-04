using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Orrery.Core.Recording;

/// <summary>
/// A picture of a screen recording at one moment, for export: RGB, with the pointer drawn in; and the sound track laid
/// since the frame before (<see cref="SoundTrack"/>), to be written after it. The sound runs from where the last
/// frame's ended, so it may lag the picture by up to a frame while it's being read; its timing comes from its own length.
/// </summary>
public sealed record ExportFrame(TimeSpan Time, int Width, int Height, byte[] Rgb, bool Changed)
{
    /// <summary>PCM in the export's <see cref="SoundFormat"/>; empty without sound.</summary>
    public byte[] Sound { get; init; } = [];
}

/// <summary>
/// Steps through a screen recording at a steady frame rate (design §3.4), the way a video file needs it, rather than
/// event by event. Idle stretches can be shortened, as the player's Skip idle does.
/// </summary>
public static class ScreenExport
{
    /// <summary>Gaps longer than this play as this long when idle stretches are skipped.</summary>
    public static readonly TimeSpan IdleCap = TimeSpan.FromSeconds(2);

    /// <summary>Silence longer than this is laid down as frames pass, where the sound needn't run steadily.</summary>
    static readonly TimeSpan LongSilence = TimeSpan.FromSeconds(1);

    /// <param name="width">The video's size: every frame is drawn at it, a screen of another size centered on black.</param>
    /// <remarks>Each frame's pixels are the same buffer, drawn again for the next one: use them before moving on.</remarks>
    /// <param name="sound">The sound track's format, from <see cref="SoundFormat.Of"/>; null leaves sound out.</param>
    /// <param name="steadySound">
    /// Whether each frame's sound runs on, in silence where none was recorded, to two frames past it. ffmpeg reads its
    /// inputs in step, so a sound track that stops while the picture goes on would leave both waiting on each other.
    /// </param>
    /// <param name="recolor">Changes each frame's RGB pixels in place once drawn, as undoing a web page's color correction.</param>
    public static IEnumerable<ExportFrame> Frames(RecordingReader reader, int framesPerSecond, int width, int height, bool skipIdle,
        CancellationToken cancel = default, SoundFormat? sound = null, bool steadySound = false,
        Action<byte[]>? recolor = null)
    {
        byte[] Sound(SoundTrack? track, TimeSpan frame, TimeSpan step)
        {
            if (track is null)
            {
                return [];
            }

            if (steadySound)
            {
                track.PadTo(frame + step + step);
            }
            else if (frame - track.Laid > LongSilence)
            {
                // A long quiet stretch goes out as the frames do, not all at once with the next sound: an hour of
                // silence is hundreds of megabytes.
                track.PadTo(frame);
            }

            return track.Take();
        }

        var track = sound is null ? null : new SoundTrack(sound);
        var decoder = new ScreenDecoder();
        var step = TimeSpan.FromSeconds(1.0 / Math.Max(1, framesPerSecond));
        var output = new byte[checked(width * height * 3)];
        var next = TimeSpan.Zero;       // the next frame's time, on the export's clock
        var skipped = TimeSpan.Zero;    // idle time taken out so far
        var last = TimeSpan.Zero;       // the last event's time, on the recording's clock
        bool changed = true, any = false;

        foreach (var e in reader.Events())
        {
            cancel.ThrowIfCancellationRequested();
            var time = e.Time;
            if (skipIdle && time - last > IdleCap)
            {
                skipped += time - last - IdleCap;
            }

            last = time > last ? time : last;
            var at = time - skipped;
            while (any && next <= at)
            {
                // A long still stretch is many frames from one event.
                cancel.ThrowIfCancellationRequested();
                if (changed)
                {
                    Render(decoder, output, width, height);
                    recolor?.Invoke(output);
                }

                yield return new ExportFrame(next, width, height, output, changed) { Sound = Sound(track, next, step) };
                changed = false;
                next += step;
            }

            if (e.Type == RecordingEventType.Audio)
            {
                track?.Add(at, e.Payload);
                continue;
            }

            if (decoder.Apply(e) && e.Type is RecordingEventType.KeyFrame or RecordingEventType.Change or RecordingEventType.Pointer)
            {
                changed = true;
                any |= decoder.HasFrame;
            }
        }

        if (any)
        {
            Render(decoder, output, width, height);
            recolor?.Invoke(output);
            yield return new ExportFrame(next, width, height, output, true) { Sound = Sound(track, next, step) };
        }
    }

    /// <summary>The size of a recording's first key frame, which export uses for the video.</summary>
    public static (int Width, int Height)? FirstSize(RecordingReader reader)
    {
        foreach (var e in reader.Events())
        {
            // Sized as the player's frames are (design §8): a file can claim any size, and this one is allocated.
            if (e.Type == RecordingEventType.Resize && RecordingFile.ReadPair(e.Payload) is var (w, h)
                && w is > 0 and <= ScreenFrames.MaxSide && h is > 0 and <= ScreenFrames.MaxSide)
            {
                return (w, h);
            }
        }

        return null;
    }

    static void Render(ScreenDecoder decoder, byte[] output, int width, int height)
    {
        Array.Clear(output);
        if (!decoder.HasFrame)
        {
            return;
        }

        int w = Math.Min(width, decoder.Width), h = Math.Min(height, decoder.Height);
        int left = (width - w) / 2, top = (height - h) / 2;
        var source = decoder.Rgb;
        for (int row = 0; row < h; row++)
        {
            source.Slice(row * decoder.Width * 3, w * 3).CopyTo(output.AsSpan(((top + row) * width + left) * 3));
        }

        if (decoder.Pointer is var (x, y))
        {
            PointerArrow.Draw(output, width, height, x + left, y + top);
        }
    }
}

/// <summary>A plain arrow where the pointer was: the remote pointer's own shape isn't recorded (design §2.2).</summary>
public static class PointerArrow
{
    // The arrow, row by row: X is its black outline, . its white inside, a space is left alone.
    static readonly string[] Shape =
    [
        "X",
        "XX",
        "X.X",
        "X..X",
        "X...X",
        "X....X",
        "X.....X",
        "X......X",
        "X.......X",
        "X........X",
        "X.........X",
        "X......XXXXX",
        "X...X..X",
        "X..XX..X",
        "X.X  X..X",
        "XX   X..X",
        "X     X..X",
        "      X..X",
        "       XX",
    ];

    /// <summary>Draws it with its tip at (<paramref name="x"/>, <paramref name="y"/>), into RGB pixels or, with 4 bytes a pixel, RGBA.</summary>
    public static void Draw(Span<byte> rgb, int width, int height, int x, int y, int bytesPerPixel = 3)
    {
        for (int row = 0; row < Shape.Length; row++)
        {
            int py = y + row;
            if (py < 0 || py >= height)
            {
                continue;
            }

            var line = Shape[row];
            for (int column = 0; column < line.Length; column++)
            {
                int px = x + column;
                if (px < 0 || px >= width || line[column] == ' ')
                {
                    continue;
                }

                byte value = line[column] == 'X' ? (byte)0 : (byte)255;
                var pixel = rgb.Slice((py * width + px) * bytesPerPixel, 3);
                pixel.Fill(value);
            }
        }
    }
}

/// <summary>A video codec an ffmpeg can export to.</summary>
public sealed record FfmpegCodec(string Name, string Encoder);

/// <summary>What Orrery found out about the ffmpeg the user chose.</summary>
public sealed record FfmpegInfo(string Path, string Version, IReadOnlyList<FfmpegCodec> Codecs);

/// <summary>
/// MP4 export through an ffmpeg the user installed and chose (design §3.4). It's started by its full path with its
/// arguments as a list, never through a shell, and never found on PATH, downloaded or bundled. <see cref="Find"/>
/// looks where installers put it, for Settings to suggest; nothing found is run until the user takes it.
/// </summary>
public static class Ffmpeg
{
    /// <summary>The first ffmpeg in a place installers put it on this system, or null. Only looks: runs nothing.</summary>
    public static string? Find()
    {
        IEnumerable<string> places = OperatingSystem.IsWindows()
            ? WindowsPlaces(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            : UnixPlaces(OperatingSystem.IsMacOS());
        return places.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Where Windows installers put ffmpeg.exe, in order: winget's packages, newest first, then its links (for this
    /// user, then everyone), Scoop, Chocolatey and Program Files. Folders any user can make, like C:\ffmpeg, aren't
    /// looked in, so another account can't plant one there.
    /// </summary>
    public static IEnumerable<string> WindowsPlaces(string localAppData, string programFiles, string programData, string home)
    {
        foreach (var packages in new[] { System.IO.Path.Combine(localAppData, "Microsoft", "WinGet", "Packages"), System.IO.Path.Combine(programFiles, "WinGet", "Packages") })
        {
            foreach (var build in Subfolders(packages, "*FFmpeg*").SelectMany(p => Subfolders(p, "ffmpeg-*")).OrderDescending(StringComparer.OrdinalIgnoreCase))
            {
                yield return System.IO.Path.Combine(build, "bin", "ffmpeg.exe");
            }
        }

        yield return System.IO.Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        yield return System.IO.Path.Combine(programFiles, "WinGet", "Links", "ffmpeg.exe");
        yield return System.IO.Path.Combine(home, "scoop", "apps", "ffmpeg", "current", "bin", "ffmpeg.exe");
        yield return System.IO.Path.Combine(programData, "chocolatey", "bin", "ffmpeg.exe");
        yield return System.IO.Path.Combine(programFiles, "ffmpeg", "bin", "ffmpeg.exe");
    }

    /// <summary>Where package managers put ffmpeg on macOS (Homebrew, then MacPorts) and Linux (the distribution's, then Homebrew).</summary>
    public static IEnumerable<string> UnixPlaces(bool macOS) => macOS
        ? ["/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/opt/local/bin/ffmpeg"]
        : ["/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/home/linuxbrew/.linuxbrew/bin/ffmpeg"];

    /// <summary>
    /// Which package manager put an ffmpeg at <paramref name="path"/>, and the version its folder is named for, from
    /// the path alone (nothing is run): "winget" and "9.0.2" for winget's Gyan.FFmpeg. Null for what the path doesn't say.
    /// </summary>
    public static (string? Installer, string? Version) Describe(string path)
    {
        var unified = path.Replace('\\', '/');
        string? installer =
            Contains("/WinGet/") ? "winget"
            : Contains("/scoop/") ? "Scoop"
            : Contains("/chocolatey/") ? "Chocolatey"
            : Contains("/opt/homebrew/") || Contains("/linuxbrew/") ? "Homebrew"
            : Contains("/opt/local/") ? "MacPorts"
            : unified.StartsWith("/usr/bin/", StringComparison.Ordinal) ? "your distribution"
            : null;
        var version = Regex.Match(unified, @"/ffmpeg-n?(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        return (installer, version.Success ? version.Groups[1].Value : null);

        bool Contains(string part) => unified.Contains(part, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The version in an <c>ffmpeg -version</c> line, without the builder's suffix: "9.0.2" from
    /// "ffmpeg version 9.0.2-full_build-www.gyan.dev Copyright …". A build named otherwise keeps its whole name.
    /// </summary>
    public static string ShortVersion(string versionLine)
    {
        var name = Regex.Match(versionLine, @"^ffmpeg version (\S+)");
        if (!name.Success)
        {
            return versionLine;
        }

        var number = Regex.Match(name.Groups[1].Value, @"^n?(\d+\.\d+(?:\.\d+)?)");
        return number.Success ? number.Groups[1].Value : name.Groups[1].Value;
    }

    static IEnumerable<string> Subfolders(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetDirectories(folder, pattern) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // In order of preference, per codec: software encoders first, which every build of ffmpeg agrees on.
    static readonly (string Name, string[] Encoders)[] Known =
    [
        ("H.264", ["libx264", "libopenh264", "h264_mf", "h264_videotoolbox", "h264_nvenc", "h264_qsv", "h264_amf", "h264_vaapi"]),
        ("H.265", ["libx265", "hevc_mf", "hevc_videotoolbox", "hevc_nvenc", "hevc_qsv", "hevc_amf", "hevc_vaapi"]),
        ("AV1", ["libsvtav1", "libaom-av1", "librav1e", "av1_nvenc", "av1_qsv", "av1_amf", "av1_vaapi"]),
    ];

    /// <summary>Runs <c>ffmpeg -version</c> and <c>-encoders</c>; null when it isn't an ffmpeg that runs.</summary>
    public static async Task<FfmpegInfo?> ProbeAsync(string path, CancellationToken cancel = default)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            return null;
        }

        var version = await RunAsync(path, ["-hide_banner", "-version"], cancel);
        if (version is null || !version.StartsWith("ffmpeg version", StringComparison.Ordinal))
        {
            return null;
        }

        var encoders = await RunAsync(path, ["-hide_banner", "-encoders"], cancel) ?? string.Empty;
        return new FfmpegInfo(path, version.Split('\n')[0].Trim(), ParseCodecs(encoders));
    }

    /// <summary>The codecs an <c>-encoders</c> listing offers, each with its preferred encoder.</summary>
    public static IReadOnlyList<FfmpegCodec> ParseCodecs(string listing)
    {
        // Lines look like " V....D libx264              libx264 H.264 / AVC ...": flags, then the encoder's name.
        var names = Regex.Matches(listing, @"^\s*V\S*\s+(\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
        return Known.Select(k => k.Encoders.FirstOrDefault(names.Contains) is { } encoder ? new FfmpegCodec(k.Name, encoder) : null)
            .OfType<FfmpegCodec>().ToList();
    }

    /// <summary>
    /// How ffmpeg is started to read raw RGB frames from standard input and write <paramref name="output"/>; with
    /// <paramref name="sound"/>, it also reads 16-bit PCM from <paramref name="soundInput"/> (a <see cref="SoundPipe"/>)
    /// and encodes it as AAC.
    /// </summary>
    public static ProcessStartInfo ExportStart(string ffmpeg, FfmpegCodec codec, int width, int height, int framesPerSecond, string output,
        SoundFormat? sound = null, string? soundInput = null)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        // A full path never starts with "-", so ffmpeg can't take the file name for an option.
        output = System.IO.Path.GetFullPath(output);
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        bool withSound = sound is not null && soundInput is not null;
        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{width}x{height}", "-r", framesPerSecond.ToString(culture),
            "-i", "pipe:0",
            .. withSound
                ? (string[])["-f", "s16le", "-ar", sound!.SampleRate.ToString(culture), "-ac", sound.Channels.ToString(culture), "-i", soundInput!]
                : [],
            // yuv420p, which every player takes, needs an even width and height.
            "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2",
            "-c:v", codec.Encoder, "-pix_fmt", "yuv420p",
            // AAC at 192 kbit/s, as Windows' own MP4 export makes; every build of ffmpeg has this encoder.
            .. withSound ? (string[])["-map", "0:v", "-map", "1:a", "-c:a", "aac", "-b:a", "192k"] : [],
            "-movflags", "+faststart",
            output,
        ];
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    static async Task<string?> RunAsync(string path, string[] arguments, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? await output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// The pipe an ffmpeg export reads its sound from, alongside the picture on standard input: a named pipe only this
/// user can open on Windows, or a FIFO in a private folder elsewhere, so the sound never touches the disk. Sound is
/// queued with <see cref="Write"/> and written on its own task, so the picture never waits on it.
/// </summary>
public sealed class SoundPipe : IDisposable
{
    readonly System.Threading.Channels.Channel<byte[]> _queue = System.Threading.Channels.Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });
    readonly CancellationTokenSource _stop = new();
    readonly System.IO.Pipes.NamedPipeServerStream? _server;
    readonly string? _folder;
    Task? _writing;

    /// <summary>What to give ffmpeg as the input's file name.</summary>
    public string Path { get; }

    public SoundPipe()
    {
        if (OperatingSystem.IsWindows())
        {
            var name = $"orrery-sound-{Guid.NewGuid():N}";
            _server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.Out, 1,
                System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
            Path = $@"\\.\pipe\{name}";
        }
        else
        {
            // A new temp subfolder is made readable by this user only.
            _folder = Directory.CreateTempSubdirectory("orrery-sound-").FullName;
            Path = System.IO.Path.Combine(_folder, "sound");
            if (MakeFifo(Path, 0b110_000_000) != 0)
            {
                var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                Directory.Delete(_folder, recursive: true);
                throw new IOException($"Couldn't make a pipe for the sound (error {error}).");
            }
        }
    }

    /// <summary>Starts writing what's queued, once ffmpeg opens the pipe; call it after ffmpeg has started.</summary>
    public void Start() => _writing = Task.Run(WriteAllAsync);

    /// <summary>Queues sound to write; empty sound is skipped.</summary>
    public void Write(byte[] pcm)
    {
        if (pcm.Length > 0)
        {
            _queue.Writer.TryWrite(pcm);
        }
    }

    /// <summary>Ends the sound: what's queued is still written, then ffmpeg reads the end of it.</summary>
    public void Complete() => _queue.Writer.TryComplete();

    async Task WriteAllAsync()
    {
        try
        {
            Stream stream;
            if (_server is not null)
            {
                await _server.WaitForConnectionAsync(_stop.Token);
                stream = _server;
            }
            else
            {
                // Opening a FIFO to write waits until ffmpeg opens it to read.
                stream = await Task.Run(() => new FileStream(Path, FileMode.Open, FileAccess.Write, FileShare.Read), _stop.Token);
            }

            await using (stream)
            {
                await foreach (var pcm in _queue.Reader.ReadAllAsync(_stop.Token))
                {
                    await stream.WriteAsync(pcm, _stop.Token);
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // ffmpeg went away, or the export was stopped: its own exit says what happened.
        }
    }

    /// <summary>Stops writing, waiting at most a moment, once ffmpeg has exited.</summary>
    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _stop.Cancel();
        if (_folder is not null)
        {
            // A writer still waiting to open the FIFO is let go by opening the other end, without waiting for one.
            int reader = Open(Path, OperatingSystem.IsMacOS() ? 0x4 : 0x800); // O_RDONLY | O_NONBLOCK
            if (reader >= 0)
            {
                Close(reader);
            }
        }

        _writing?.Wait(TimeSpan.FromSeconds(5));
        _server?.Dispose();
        if (_folder is not null)
        {
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        _stop.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    static extern int MakeFifo(string path, uint mode);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "open", SetLastError = true)]
    static extern int Open(string path, int flags);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "close")]
    static extern int Close(int descriptor);
}
