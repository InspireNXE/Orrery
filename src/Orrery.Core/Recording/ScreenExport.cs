using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Orrery.Core.Recording;

/// <summary>A picture of a screen recording at one moment, for export: RGB, with the pointer drawn in.</summary>
public sealed record ExportFrame(TimeSpan Time, int Width, int Height, byte[] Rgb, bool Changed);

/// <summary>
/// Steps through a screen recording at a steady frame rate (design §3.4), the way a video file needs it, rather than
/// event by event. Idle stretches can be shortened, as the player's Skip idle does.
/// </summary>
public static class ScreenExport
{
    /// <summary>Gaps longer than this play as this long when idle stretches are skipped.</summary>
    public static readonly TimeSpan IdleCap = TimeSpan.FromSeconds(2);

    /// <param name="width">The video's size: every frame is drawn at it, a screen of another size centered on black.</param>
    /// <remarks>Each frame's pixels are the same buffer, drawn again for the next one: use them before moving on.</remarks>
    public static IEnumerable<ExportFrame> Frames(RecordingReader reader, int framesPerSecond, int width, int height, bool skipIdle,
        CancellationToken cancel = default)
    {
        var decoder = new ScreenDecoder();
        var step = TimeSpan.FromSeconds(1.0 / Math.Max(1, framesPerSecond));
        var output = new byte[width * height * 3];
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
                }

                yield return new ExportFrame(next, width, height, output, changed);
                changed = false;
                next += step;
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
            yield return new ExportFrame(next, width, height, output, true);
        }
    }

    /// <summary>The size of a recording's first key frame, which export uses for the video.</summary>
    public static (int Width, int Height)? FirstSize(RecordingReader reader)
    {
        foreach (var e in reader.Events())
        {
            if (e.Type == RecordingEventType.Resize && RecordingFile.ReadPair(e.Payload) is var (w, h) && w > 0 && h > 0)
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
/// arguments as a list, never through a shell, and never found on PATH, downloaded or bundled.
/// </summary>
public static class Ffmpeg
{
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

    /// <summary>How ffmpeg is started to read raw RGB frames from standard input and write <paramref name="output"/>.</summary>
    public static ProcessStartInfo ExportStart(string ffmpeg, FfmpegCodec codec, int width, int height, int framesPerSecond, string output)
    {
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
        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{width}x{height}", "-r", framesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", "pipe:0",
            // yuv420p, which every player takes, needs an even width and height.
            "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2",
            "-c:v", codec.Encoder, "-pix_fmt", "yuv420p",
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
