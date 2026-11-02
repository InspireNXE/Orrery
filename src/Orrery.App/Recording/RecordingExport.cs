using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;
using Orrery.Core.Recording;
using SkiaSharp;

namespace Orrery.App.Recording;

/// <summary>
/// Exporting a recording for other software (docs/design-session-recording.md §3.4): a terminal as <c>.cast</c>, a
/// screen as Motion-JPEG <c>.avi</c>, or as MP4 through the ffmpeg chosen in Settings. The copy isn't encrypted, so
/// the user is told first.
/// </summary>
static class RecordingExport
{
    static readonly ILogger Logger = Log.For("Recording");

    const int JpegQuality = 85;

    public const string Warning = "The exported copy isn't encrypted: anyone who has the file can watch it. "
        + "Everything the session showed is in it.";

    public static async Task ExportAsync(Window owner, RecordingService service, string path, byte[] privateKey, string suggestedName)
    {
        RecordingHeader header;
        try
        {
            header = RecordingReader.ReadHeader(path);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(owner, "Couldn't export", e.Message);
            return;
        }

        if (header.Kind == RecordingKind.Terminal)
        {
            if (!await Dialogs.ConfirmAsync(owner, "Export recording", Warning, "Export"))
            {
                return;
            }

            await ExportCastAsync(owner, path, privateKey, suggestedName);
            return;
        }

        var ffmpeg = service.Settings.FfmpegPath is { } ffmpegPath ? await Ffmpeg.ProbeAsync(ffmpegPath) : null;
        var choice = await new ExportChoiceWindow(ffmpeg).ShowDialog<ExportChoice?>(owner);
        if (choice is null)
        {
            return;
        }

        var extension = choice.Codec is null ? "avi" : "mp4";
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export recording",
            SuggestedFileName = $"{suggestedName}.{extension}",
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(choice.Codec is null ? "AVI video" : "MP4 video") { Patterns = [$"*.{extension}"] }],
        });
        if (file?.TryGetLocalPath() is not { } output)
        {
            return;
        }

        int framesPerSecond = service.Settings.RecordingFramesPerSecond;
        var progress = new ExportProgressWindow();
        var work = Task.Run(() => choice.Codec is { } codec
            ? ExportMp4(path, privateKey, ffmpeg!, codec, output, framesPerSecond, choice.SkipIdle, progress)
            : ExportAvi(path, privateKey, output, framesPerSecond, choice.SkipIdle, progress));
        var shown = progress.ShowDialog(owner);
        string? problem;
        try
        {
            problem = await work;
        }
        catch (OperationCanceledException)
        {
            problem = null;
        }
        finally
        {
            progress.Finish();
        }

        await shown;
        if (problem is not null)
        {
            await Dialogs.ShowMessageAsync(owner, "Couldn't export", problem);
        }
        else if (!progress.Canceled)
        {
            Logger.LogInformation("Exported a screen recording as {Format}", extension);
        }
    }

    static async Task ExportCastAsync(Window owner, string path, byte[] privateKey, string suggestedName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export recording",
            SuggestedFileName = $"{suggestedName}.cast",
            DefaultExtension = "cast",
            FileTypeChoices = [new FilePickerFileType("asciinema recording") { Patterns = ["*.cast"] }],
        });
        if (file is null)
        {
            return;
        }

        try
        {
            using var reader = RecordingReader.Open(path, privateKey);
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            AsciicastExport.Write(reader, writer);
            Logger.LogInformation("Exported a terminal recording as .cast");
        }
        catch (Exception e) when (e is InvalidDataException or RecordingKeyException or IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(owner, "Couldn't export", e.Message);
        }
    }

    // Each returns a problem to show, or null; a canceled export deletes what it wrote.
    static string? ExportAvi(string path, byte[] privateKey, string output, int framesPerSecond, bool skipIdle, ExportProgressWindow progress)
    {
        var parts = new List<string>();
        try
        {
            using var reader = RecordingReader.Open(path, privateKey);
            if (ScreenExport.FirstSize(reader) is not var (width, height))
            {
                return "This recording has no picture in it.";
            }

            Stream? stream = null;
            AviWriter? avi = null;
            void NextPart()
            {
                avi?.Finish();
                stream?.Dispose();
                var name = parts.Count == 0 ? output
                    : Path.Combine(Path.GetDirectoryName(output)!, $"{Path.GetFileNameWithoutExtension(output)} (part {parts.Count + 1}){Path.GetExtension(output)}");
                parts.Add(name);
                stream = new FileStream(name, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                avi = new AviWriter(stream, width, height, framesPerSecond);
            }

            NextPart();
            var rgba = new byte[width * height * 4];
            byte[]? lastJpeg = null;
            int index = 0;
            foreach (var frame in ScreenExport.Frames(reader, framesPerSecond, width, height, skipIdle, progress.Token))
            {
                if (avi!.IsFull)
                {
                    NextPart();
                    lastJpeg = null;
                }

                if (frame.Changed || lastJpeg is null)
                {
                    lastJpeg = Jpeg(frame.Rgb, rgba, width, height);
                    avi.AddFrame(lastJpeg);
                }
                else
                {
                    avi.AddFrame([], repeat: true);
                }

                if (++index % 25 == 0)
                {
                    progress.Report(frame.Time);
                }
            }

            avi!.Finish();
            stream!.Dispose();
            return reader.Ending == RecordingEnding.Damaged ? "Part of the recording couldn't be read, so the video stops where that part starts." : null;
        }
        catch (OperationCanceledException)
        {
            DeleteAll(parts);
            throw;
        }
        catch (Exception e) when (e is InvalidDataException or RecordingKeyException or IOException or UnauthorizedAccessException)
        {
            DeleteAll(parts);
            return e.Message;
        }
    }

    static string? ExportMp4(string path, byte[] privateKey, FfmpegInfo ffmpeg, FfmpegCodec codec, string output, int framesPerSecond, bool skipIdle,
        ExportProgressWindow progress)
    {
        Process? process = null;
        try
        {
            using var reader = RecordingReader.Open(path, privateKey);
            if (ScreenExport.FirstSize(reader) is not var (width, height))
            {
                return "This recording has no picture in it.";
            }

            process = Process.Start(Ffmpeg.ExportStart(ffmpeg.Path, codec, width, height, framesPerSecond, output));
            if (process is null)
            {
                return "ffmpeg didn't start.";
            }

            var errors = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            var input = process.StandardInput.BaseStream;
            int index = 0;
            try
            {
                foreach (var frame in ScreenExport.Frames(reader, framesPerSecond, width, height, skipIdle, progress.Token))
                {
                    input.Write(frame.Rgb);
                    if (++index % 25 == 0)
                    {
                        progress.Report(frame.Time);
                    }
                }

                input.Close();
            }
            catch (IOException)
            {
                // ffmpeg stopped reading: its own message below says why.
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                TryDelete(output);
                var message = errors.Result.Trim();
                return $"ffmpeg couldn't make the video{(message.Length > 0 ? ": " + message.Split('\n')[^1] : ".")}";
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            TryDelete(output);
            throw;
        }
        catch (Exception e) when (e is InvalidDataException or RecordingKeyException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            TryDelete(output);
            return e.Message;
        }
        finally
        {
            process?.Dispose();
        }
    }

    static byte[] Jpeg(byte[] rgb, byte[] rgba, int width, int height)
    {
        for (int i = 0, o = 0; i < rgb.Length; i += 3, o += 4)
        {
            rgba[o] = rgb[i];
            rgba[o + 1] = rgb[i + 1];
            rgba[o + 2] = rgb[i + 2];
            rgba[o + 3] = 255;
        }

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap();
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(rgba, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bitmap.InstallPixels(info, pinned.AddrOfPinnedObject(), info.RowBytes);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            return data.ToArray();
        }
        finally
        {
            pinned.Free();
        }
    }

    static void DeleteAll(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            TryDelete(path);
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

sealed record ExportChoice(FfmpegCodec? Codec, bool SkipIdle);

/// <summary>Asks what a screen recording should become: AVI, or MP4 with one of the codecs the chosen ffmpeg has.</summary>
sealed class ExportChoiceWindow : Window
{
    public ExportChoiceWindow(FfmpegInfo? ffmpeg)
    {
        Title = "Export recording";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        var format = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        format.Items.Add(new ComboBoxItem { Content = "AVI, Motion-JPEG (plays in VLC and Windows Media Player)", Tag = null });
        foreach (var codec in ffmpeg?.Codecs ?? [])
        {
            format.Items.Add(new ComboBoxItem { Content = $"MP4, {codec.Name} (through ffmpeg)", Tag = codec });
        }

        format.SelectedIndex = format.Items.Count > 1 ? 1 : 0;
        var skipIdle = new CheckBox { Content = "Skip idle stretches (pauses over 2 seconds play as 2 seconds)", IsChecked = true };
        var export = new Button { Content = "Export…", IsDefault = true };
        export.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        export.Click += (_, _) => Close(new ExportChoice((format.SelectedItem as ComboBoxItem)?.Tag as FfmpegCodec, skipIdle.IsChecked == true));
        cancel.Click += (_, _) => Close(null);

        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                Dialogs.Heading(new TextBlock { Text = Title, FontSize = 16, FontWeight = FontWeight.SemiBold }),
                new TextBlock { Text = RecordingExport.Warning, TextWrapping = TextWrapping.Wrap },
                Dialogs.Field("Format", format),
                skipIdle,
            },
        };
        if (ffmpeg is null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "For MP4, choose an ffmpeg you've installed in Settings › Sessions › Recordings.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = Dialogs.MutedBrush,
            });
        }

        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, export } });
        Content = panel;
        AutomationProperties.SetHelpText(this, RecordingExport.Warning);
    }
}

/// <summary>Shows how far an export has got, with Cancel.</summary>
sealed class ExportProgressWindow : Window
{
    readonly CancellationTokenSource _cancel = new();
    readonly TextBlock _text = new() { Text = "Exporting…" };
    bool _finished;

    public ExportProgressWindow()
    {
        Title = "Exporting";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;
        var cancel = new Button { Content = "Cancel", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (_, _) =>
        {
            Canceled = true;
            _cancel.Cancel();
            _text.Text = "Canceling…";
        };
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children = { _text, new ProgressBar { IsIndeterminate = true }, cancel },
        };
        Closing += (_, e) =>
        {
            // Closing the window cancels; it goes once the export has stopped.
            if (!_finished)
            {
                e.Cancel = true;
                Canceled = true;
                _cancel.Cancel();
            }
        };
    }

    public CancellationToken Token => _cancel.Token;

    public bool Canceled { get; private set; }

    /// <summary>Called from the export's thread.</summary>
    public void Report(TimeSpan done) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (!Canceled)
        {
            _text.Text = $"Exporting… {done:h\\:mm\\:ss} done";
        }
    });

    public void Finish()
    {
        _finished = true;
        Close();
    }
}
