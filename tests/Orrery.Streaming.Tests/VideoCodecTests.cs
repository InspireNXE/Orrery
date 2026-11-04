using Xunit;

namespace Orrery.Streaming.Tests;

public class VideoCodecTests
{
    static readonly HashSet<VideoCodec> All = [VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1];

    [Fact]
    public void The_best_shared_codec_is_chosen()
    {
        Assert.Equal(VideoCodec.Av1, VideoCodecs.Choose(All, All));
        Assert.Equal(VideoCodec.Hevc, VideoCodecs.Choose(All, new HashSet<VideoCodec> { VideoCodec.H264, VideoCodec.Hevc }));
    }

    [Fact]
    public void A_wanted_codec_wins_when_both_sides_have_it()
    {
        Assert.Equal(VideoCodec.H264, VideoCodecs.Choose(All, All, VideoCodec.H264));
    }

    [Fact]
    public void A_wanted_codec_one_side_lacks_falls_back_to_the_best_shared()
    {
        Assert.Equal(VideoCodec.Hevc, VideoCodecs.Choose(new HashSet<VideoCodec> { VideoCodec.H264, VideoCodec.Hevc }, All, VideoCodec.Av1));
    }

    [Fact]
    public void No_shared_codec_is_null()
    {
        Assert.Null(VideoCodecs.Choose(new HashSet<VideoCodec> { VideoCodec.Av1 }, new HashSet<VideoCodec> { VideoCodec.H264 }));
    }
}
