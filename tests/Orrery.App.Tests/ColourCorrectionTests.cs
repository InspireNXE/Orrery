using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Orrery.App.Sessions;
using Orrery.Core.Sessions;
using Vision = Orrery.Core.Settings.ColourVision;
using Xunit;
using Xunit.Abstractions;

namespace Orrery.App.Tests;

public class ColourCorrectionTests(ITestOutputHelper output)
{
    static readonly Vision[] Kinds = [Vision.Protanopia, Vision.Deuteranopia, Vision.Tritanopia];

    static double Srgb(double linear) => linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;

    static void AssertIdentity(double[,] m)
    {
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                Assert.Equal(row == column ? 1 : 0, m[row, column], 12);
            }
        }
    }

    [Fact]
    public void Nothing_to_correct_without_a_deficiency_or_strength()
    {
        AssertIdentity(ColourCorrection.MatrixFor(Vision.None, 1));
        foreach (var kind in Kinds)
        {
            AssertIdentity(ColourCorrection.MatrixFor(kind, 0));
            Assert.Null(ColourCorrection.For(kind, 0));
        }

        Assert.Null(ColourCorrection.For(Vision.None, 100));
        Assert.NotNull(ColourCorrection.For(Vision.Deuteranopia, 100));
    }

    [Fact]
    public void Pure_red_for_protanopia_gains_green_and_blue()
    {
        // Machado's protanopia matrix takes red (1, 0, 0) to (0.152286, 0.114503, -0.003882); the loss is
        // (0.847714, -0.114503, 0.003882), and 0.7 of the red loss goes to green and to blue.
        var m = ColourCorrection.MatrixFor(Vision.Protanopia, 1);
        Assert.Equal(1, m[0, 0], 6);
        Assert.Equal(0.7 * 0.847714 - 0.114503, m[1, 0], 6);
        Assert.Equal(0.7 * 0.847714 + 0.003882, m[2, 0], 6);

        var (r, g, b) = ColourCorrection.For(Vision.Protanopia, 100)!.Apply(255, 0, 0);
        Assert.Equal(255, r);
        Assert.InRange(g, Math.Round(Srgb(m[1, 0]) * 255) - 1, Math.Round(Srgb(m[1, 0]) * 255) + 1);
        Assert.InRange(b, Math.Round(Srgb(m[2, 0]) * 255) - 1, Math.Round(Srgb(m[2, 0]) * 255) + 1);
    }

    [Fact]
    public void Strength_scales_the_correction()
    {
        var full = ColourCorrection.MatrixFor(Vision.Deuteranopia, 1);
        var half = ColourCorrection.MatrixFor(Vision.Deuteranopia, 0.5);
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                double identity = row == column ? 1 : 0;
                Assert.Equal(identity + (full[row, column] - identity) / 2, half[row, column], 12);
            }
        }
    }

    [Fact]
    public void Results_outside_the_screen_colours_are_clamped()
    {
        // Blue for protanopia comes out at 1.09 blue in linear light; green for protanopia at -0.69 blue.
        var protan = ColourCorrection.MatrixFor(Vision.Protanopia, 1);
        Assert.True(protan[2, 2] > 1.05);
        Assert.Equal(255, ColourCorrection.For(Vision.Protanopia, 100)!.Apply(0, 0, 255).B);
        Assert.True(protan[2, 1] < -0.6);
        Assert.Equal(0, ColourCorrection.For(Vision.Protanopia, 100)!.Apply(0, 255, 0).B);

        // Green for tritanopia loses 0.41 of red, below zero.
        Assert.True(ColourCorrection.MatrixFor(Vision.Tritanopia, 1)[0, 1] < -0.4);
        Assert.Equal(0, ColourCorrection.For(Vision.Tritanopia, 100)!.Apply(0, 255, 0).R);
    }

    [Fact]
    public void Greys_stay_grey()
    {
        foreach (var kind in Kinds)
        {
            var correction = ColourCorrection.For(kind, 100)!;
            for (int v = 0; v < 256; v += 15)
            {
                var (r, g, b) = correction.Apply((byte)v, (byte)v, (byte)v);
                Assert.InRange(r, v - 1, v + 1);
                Assert.InRange(g, v - 1, v + 1);
                Assert.InRange(b, v - 1, v + 1);
            }
        }
    }

    [Fact]
    public void Corrected_colours_are_further_apart_for_the_viewer()
    {
        // The same red and green as the simulation test: close together for red-green colour blindness.
        Color red = Color.Parse("#C86450"), green = Color.Parse("#7D8C3C");
        foreach (var (kind, deficiency) in new[] { (Vision.Protanopia, Deficiency.Protanopia), (Vision.Deuteranopia, Deficiency.Deuteranopia) })
        {
            var correction = ColourCorrection.For(kind, 100)!;
            double before = ColourVision.Difference(red, green, deficiency);
            double after = ColourVision.Difference(Corrected(correction, red), Corrected(correction, green), deficiency);
            output.WriteLine($"{kind}: red/green ΔE {before:0.0} before, {after:0.0} after");
            Assert.True(after > before + 5, $"{kind}: {before:0.0} to {after:0.0}");
        }

        // Blue and green, which tritanopia runs together.
        Color blue = Color.Parse("#4F7FC0"), teal = Color.Parse("#3F9F8F");
        var tritan = ColourCorrection.For(Vision.Tritanopia, 100)!;
        double tritanBefore = ColourVision.Difference(blue, teal, Deficiency.Tritanopia);
        double tritanAfter = ColourVision.Difference(Corrected(tritan, blue), Corrected(tritan, teal), Deficiency.Tritanopia);
        output.WriteLine($"Tritanopia: blue/teal ΔE {tritanBefore:0.0} before, {tritanAfter:0.0} after");
        Assert.True(tritanAfter > tritanBefore + 5, $"Tritanopia: {tritanBefore:0.0} to {tritanAfter:0.0}");

        static Color Corrected(ColourCorrection correction, Color c)
        {
            var (r, g, b) = correction.Apply(c.R, c.G, c.B);
            return Color.FromRgb(r, g, b);
        }
    }

    static byte[] Frame(int width, int height, byte r, byte g, byte b)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (r, g, b, 255);
        }

        return pixels;
    }

    static void Apply(ColourCorrection correction, byte[] pixels, int width, int height, FrameRect region)
    {
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            correction.Apply(pinned.AddrOfPinnedObject(), width * 4, width, height, region);
        }
        finally
        {
            pinned.Free();
        }
    }

    [Fact]
    public void Only_the_region_changes_and_alpha_is_kept()
    {
        const int width = 64, height = 48;
        var pixels = Frame(width, height, 200, 60, 50);
        var correction = ColourCorrection.For(Vision.Deuteranopia, 100)!;
        var expected = correction.Apply(200, 60, 50);

        Apply(correction, pixels, width, height, new FrameRect(10, 5, 20, 10));

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bool inside = x is >= 10 and < 30 && y is >= 5 and < 15;
                var want = inside ? expected : ((byte)200, (byte)60, (byte)50);
                Assert.Equal(want, (pixels[i], pixels[i + 1], pixels[i + 2]));
                Assert.Equal(255, pixels[i + 3]);
            }
        }
    }

    [Fact]
    public void A_region_past_the_edges_is_clipped()
    {
        const int width = 16, height = 8;
        var pixels = Frame(width, height, 200, 60, 50);
        var correction = ColourCorrection.For(Vision.Protanopia, 100)!;

        Apply(correction, pixels, width, height, new FrameRect(-5, -5, 100, 100));
        Apply(correction, Frame(width, height, 1, 2, 3), width, height, new FrameRect(20, 20, 4, 4));

        Assert.Equal(correction.Apply(200, 60, 50), (pixels[^4], pixels[^3], pixels[^2]));
    }

    [Fact]
    public void A_full_hd_frame_is_corrected_quickly()
    {
        const int width = 1920, height = 1080;
        var pixels = new byte[width * height * 4];
        new Random(1).NextBytes(pixels);
        var correction = ColourCorrection.For(Vision.Deuteranopia, 100)!;
        for (int i = 0; i < 50; i++)
        {
            Apply(correction, pixels, width, height, new FrameRect(0, 0, 256, 256)); // Warm up, past the JIT's first tier.
        }

        double Best(FrameRect region)
        {
            double best = double.MaxValue;
            for (int run = 0; run < 5; run++)
            {
                var watch = Stopwatch.StartNew();
                Apply(correction, pixels, width, height, region);
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
            }

            return best;
        }

        double full = Best(new FrameRect(0, 0, width, height));
        double tile = Best(new FrameRect(100, 100, 256, 256));
        double strip = Best(new FrameRect(0, 0, width, 64)); // Under the size that's split across threads.
        output.WriteLine($"Whole 1920x1080 frame: {full:0.00} ms on {Environment.ProcessorCount} threads; a 256x256 update: {tile:0.000} ms; "
            + $"one thread: {strip * 1e6 / (width * 64):0.0} ns a pixel, {strip * 1080 / 64:0.0} ms a frame");

        // Loose, so a busy test machine doesn't fail it; it's here to catch a slip into something per-pixel slow.
        Assert.True(full < 200, $"{full:0} ms");
    }

    // ---- In the remote desktop view ----

    /// <summary>A connected desktop of one colour whose pixels can be changed, drawn from a plain RGBA array.</summary>
    sealed class PaintedDesktop(int width, int height) : IFrameBufferSession
    {
        public byte[] Pixels { get; } = Frame(width, height, 200, 60, 50);
        public List<FrameRect> Copied { get; } = [];
        public ConnectionTarget Target { get; } = new("desk", 3389, "user", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated;
        public int Width => width;
        public int Height => height;

        public void Paint(FrameRect region, byte r, byte g, byte b)
        {
            for (int y = region.Y; y < region.Bottom; y++)
            {
                for (int x = region.X; x < region.Right; x++)
                {
                    int i = (y * width + x) * 4;
                    (Pixels[i], Pixels[i + 1], Pixels[i + 2]) = (r, g, b);
                }
            }

            FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(region, resized: false));
        }

        public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride)
        {
            Copied.Add(region);
            for (int y = region.Y; y < region.Bottom; y++)
            {
                Marshal.Copy(Pixels, (y * width + region.X) * 4, destination + y * destinationStride + region.X * 4, region.Width * 4);
            }
        }

        public void SendKey(ushort scancode, bool pressed) { }
        public void ReleaseAllKeys() { }
        public void SendPointerMove(int x, int y) { }
        public void SendPointerButton(PointerButton button, bool pressed) { }
        public void SendWheel(int units, bool horizontal) { }
        public void RequestResize(int width, int height) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Headless bitmaps don't keep their pixels, so drawing is checked on a plain array, the way Flush draws into the bitmap.
    static void Draw(PaintedDesktop desktop, ColourCorrection? correction, byte[] bitmap, FrameRect region)
    {
        var pinned = GCHandle.Alloc(bitmap, GCHandleType.Pinned);
        try
        {
            RemoteFrameView.Draw(desktop, correction, pinned.AddrOfPinnedObject(), desktop.Width * 4, desktop.Width, desktop.Height, region);
        }
        finally
        {
            pinned.Free();
        }
    }

    static (byte R, byte G, byte B) PixelAt(byte[] bitmap, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return (bitmap[i], bitmap[i + 1], bitmap[i + 2]);
    }

    [Fact]
    public void Without_correction_the_desktop_is_drawn_as_sent()
    {
        var desktop = new PaintedDesktop(32, 24);
        var bitmap = new byte[32 * 24 * 4];

        Draw(desktop, null, bitmap, new FrameRect(0, 0, 32, 24));

        Assert.Equal(desktop.Pixels, bitmap);
    }

    [Fact]
    public void Each_update_is_corrected_once_and_nothing_outside_it_changes()
    {
        var desktop = new PaintedDesktop(32, 24);
        var correction = ColourCorrection.For(Vision.Protanopia, 100)!;
        var bitmap = new byte[32 * 24 * 4];
        Draw(desktop, correction, bitmap, new FrameRect(0, 0, 32, 24));
        var red = correction.Apply(200, 60, 50);
        Assert.Equal(red, PixelAt(bitmap, 32, 0, 0));
        var before = (byte[])bitmap.Clone();

        // A green square arrives: it's corrected, and the red around it isn't corrected a second time.
        desktop.Paint(new FrameRect(8, 8, 4, 4), 60, 160, 70);
        Draw(desktop, correction, bitmap, new FrameRect(8, 8, 4, 4));

        for (int y = 0; y < 24; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                bool inside = x is >= 8 and < 12 && y is >= 8 and < 12;
                Assert.Equal(inside ? correction.Apply(60, 160, 70) : PixelAt(before, 32, x, y), PixelAt(bitmap, 32, x, y));
            }
        }
    }

    [AvaloniaFact]
    public void Changing_the_correction_redraws_the_whole_desktop_unless_the_connection_turns_it_off()
    {
        var desktop = new PaintedDesktop(32, 24);
        var view = new RemoteFrameView(desktop) { FollowsViewSize = false };
        var window = new Window { Content = view, Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(view.ActiveCorrection);

        desktop.Copied.Clear();
        var correction = ColourCorrection.For(Vision.Tritanopia, 100);
        view.Correction = correction;
        Dispatcher.UIThread.RunJobs();
        Assert.Same(correction, view.ActiveCorrection);
        Assert.Equal([new FrameRect(0, 0, 32, 24)], desktop.Copied);

        // A small update copies only itself.
        desktop.Copied.Clear();
        desktop.Paint(new FrameRect(2, 3, 5, 6), 1, 2, 3);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([new FrameRect(2, 3, 5, 6)], desktop.Copied);
        window.Close();

        var unchanged = new PaintedDesktop(32, 24);
        var off = new RemoteFrameView(unchanged) { FollowsViewSize = false, AllowsColourCorrection = false };
        var offWindow = new Window { Content = off, Width = 400, Height = 300 };
        offWindow.Show();
        Dispatcher.UIThread.RunJobs();
        unchanged.Copied.Clear();

        off.Correction = correction;
        Dispatcher.UIThread.RunJobs();
        Assert.Null(off.ActiveCorrection);
        Assert.Empty(unchanged.Copied); // Nothing to redraw: it isn't corrected.
        offWindow.Close();
    }
}
