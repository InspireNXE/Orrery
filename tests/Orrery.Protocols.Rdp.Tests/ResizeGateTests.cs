using System.Diagnostics;
using Xunit;
using static Orrery.Protocols.Rdp.ResizeGate;

namespace Orrery.Protocols.Rdp.Tests;

public class ResizeGateTests
{
    static readonly DesktopRequest Current = new(1052, 1234, 100);
    static readonly DesktopRequest Wider = new(1400, 1234, 100);
    static readonly DesktopRequest Widest = new(1800, 1234, 100);

    /// <summary>A Stopwatch timestamp <paramref name="ms"/> milliseconds after an arbitrary start.</summary>
    static long At(double ms) => 1_000_000 + (long)(ms * Stopwatch.Frequency / 1000);

    [Fact]
    public void The_size_the_desktop_has_is_not_asked_for()
    {
        Assert.Equal(Decision.Skip, new ResizeGate().Offer(Current, Current, At(0)));
    }

    [Fact]
    public void Two_pixels_more_than_a_width_that_is_a_multiple_of_4_is_not_asked_for()
    {
        var gate = new ResizeGate();

        // The first size is rounded down to a multiple of 4; the view laid out asks for the 2 pixels back.
        Assert.Equal(Decision.Skip, gate.Offer(Current with { Width = 1054 }, Current, At(0)));

        Assert.Equal(Decision.Send, gate.Offer(Current with { Width = 1056 }, Current, At(0)));
        Assert.Equal(Decision.Send, gate.Offer(new DesktopRequest(1056, 1234, 100), new DesktopRequest(1054, 1234, 100), At(0)));
        Assert.Equal(Decision.Send, gate.Offer(Current with { Width = 1054, Height = 1200 }, Current, At(0)));
        Assert.Equal(Decision.Send, gate.Offer(Current with { Width = 1054, Scale = 150 }, Current, At(0)));
        Assert.Equal(Decision.Send, gate.Offer(Current with { Width = 1050 }, Current, At(0)));
    }

    [Fact]
    public void A_resize_is_held_while_the_server_works_on_the_last_and_sent_once_it_has()
    {
        var gate = new ResizeGate();
        Assert.Equal(Decision.Send, gate.Offer(Wider, Current, At(0)));
        gate.Sent(Wider, At(0));

        Assert.Equal(Decision.Hold, gate.Offer(Widest, Current, At(100)));
        Assert.True(gate.HasWaiting);
        Assert.Null(gate.TakeWaiting(At(200)));

        Assert.Equal(At(0), gate.Answered());
        Assert.Equal(Widest, gate.TakeWaiting(At(250)));
        Assert.False(gate.HasWaiting);
        Assert.Null(gate.TakeWaiting(At(250)));
    }

    [Fact]
    public void Only_the_newest_size_held_is_sent()
    {
        var gate = new ResizeGate();
        gate.Sent(new DesktopRequest(1200, 1234, 100), At(0));
        gate.Offer(Wider, Current, At(50));
        gate.Offer(Widest, Current, At(100));
        gate.Answered();

        Assert.Equal(Widest, gate.TakeWaiting(At(150)));
    }

    [Fact]
    public void Coming_back_to_the_size_asked_for_drops_the_one_held()
    {
        var gate = new ResizeGate();
        gate.Sent(Wider, At(0));
        Assert.Equal(Decision.Hold, gate.Offer(Widest, Current, At(50)));

        Assert.Equal(Decision.Skip, gate.Offer(Wider, Current, At(100)));
        Assert.False(gate.HasWaiting);
    }

    [Fact]
    public void Coming_back_to_the_desktop_size_while_the_server_leaves_it_is_kept()
    {
        // Dragged wider and straight back: the server is going to the wider size, so the old one is asked for after.
        var gate = new ResizeGate();
        gate.Sent(Wider, At(0));

        Assert.Equal(Decision.Hold, gate.Offer(Current, Current, At(50)));
        gate.Answered();
        Assert.Equal(Current, gate.TakeWaiting(At(100)));
    }

    [Fact]
    public void A_resize_the_server_never_answers_stops_holding_the_next_after_a_while()
    {
        var gate = new ResizeGate();
        gate.Sent(Wider, At(0));
        Assert.Equal(Decision.Hold, gate.Offer(Widest, Current, At(100)));

        double after = AnswerWait.TotalMilliseconds;
        Assert.Equal(Widest, gate.TakeWaiting(At(after + 1)));
        Assert.Equal(Decision.Send, gate.Offer(Widest, Current, At(after + 1)));
    }

    [Fact]
    public void It_says_how_long_the_last_resize_holds_the_next_back()
    {
        var gate = new ResizeGate();
        Assert.Equal(TimeSpan.Zero, gate.HeldFor(At(0)));

        gate.Sent(Wider, At(0));
        var left = gate.HeldFor(At(400)) - (AnswerWait - TimeSpan.FromMilliseconds(400));
        Assert.InRange(left.TotalMilliseconds, -1, 1);
        Assert.Equal(TimeSpan.Zero, gate.HeldFor(At(AnswerWait.TotalMilliseconds + 5)));

        gate.Answered();
        Assert.Equal(TimeSpan.Zero, gate.HeldFor(At(500)));
    }

    [Fact]
    public void A_size_kept_for_the_display_channel_goes_once_taken()
    {
        var gate = new ResizeGate();
        gate.Keep(Wider);
        Assert.True(gate.HasWaiting);
        Assert.Equal(Wider, gate.TakeWaiting(At(0)));

        gate.Keep(Wider);
        gate.Drop();
        Assert.Null(gate.TakeWaiting(At(0)));
    }

    [Fact]
    public void The_server_answering_when_nothing_was_asked_gives_no_time()
    {
        Assert.Equal(0, new ResizeGate().Answered());
    }
}
