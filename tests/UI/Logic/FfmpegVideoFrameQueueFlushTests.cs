using Nikse.SubtitleEdit.Logic.VideoPlayers.Ffmpeg;

namespace UITests.Logic;

public class FfmpegVideoFrameQueueFlushTests
{
    [Fact]
    public void Flush_DisposesQueuedFrameInsteadOfReusingPeekedReference()
    {
        var queue = new VideoFrameQueue(2);
        var serial = 1;
        var frame = queue.Rent(8, 8, serial, ref serial)!;
        queue.Push(frame);

        var peeked = queue.Peek();
        Assert.Same(frame, peeked);

        queue.Flush();

        Assert.Equal(IntPtr.Zero, peeked!.Data);
        Assert.Equal(0, queue.Count);

        var replacement = queue.Rent(8, 8, serial, ref serial)!;
        Assert.NotSame(peeked, replacement);
        Assert.NotEqual(IntPtr.Zero, replacement.Data);

        queue.Return(replacement);
        queue.Close();
    }
}