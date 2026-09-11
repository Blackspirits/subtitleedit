using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class TesseractDownloadServiceTests
{
    [Fact]
    public void WindowsArchiveSha256_MatchesSupportFilesReleaseDigest()
    {
        Assert.Equal(
            "fef2dbb1de8f25d660301c17aff107c0d9b0dc99e0d4f0eee938eb7238d7d2dc",
            TesseractDownloadService.WindowsArchiveSha256);
    }

    [Fact]
    public async Task VerifyRuntimeArchiveAsync_TamperedPayload_RejectsAndRewindsStream()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("tampered"));

        await Assert.ThrowsAsync<IOException>(() =>
            TesseractDownloadService.VerifyRuntimeArchiveAsync(
                stream,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task VerifyRuntimeArchiveAsync_NonSeekableStream_FailsClosed()
    {
        await using var inner = new MemoryStream(Encoding.ASCII.GetBytes("payload"));
        await using var stream = new NonSeekableStream(inner);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TesseractDownloadService.VerifyRuntimeArchiveAsync(
                stream,
                TestContext.Current.CancellationToken));
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
