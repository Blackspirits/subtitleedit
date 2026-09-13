using SeConv.Core;
using Xunit;

namespace SeConvTests.Core;

public class SubtitleConverterCancellationTest : IDisposable
{
    private readonly string _tempRoot;

    public SubtitleConverterCancellationTest()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "SubtitleConverterCancellationTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ConvertAsync_PreCancelledToken_ThrowsBeforeWritingOutput()
    {
        var input = Path.Combine(_tempRoot, "input.srt");
        await File.WriteAllTextAsync(input, """
            1
            00:00:01,000 --> 00:00:02,000
            Hello.

            """, TestContext.Current.CancellationToken);

        var outputFolder = Path.Combine(_tempRoot, "out");
        Directory.CreateDirectory(outputFolder);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var converter = new SubtitleConverter();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            converter.ConvertAsync(new ConversionOptions
            {
                Patterns = [input],
                Format = "SubRip",
                OutputFolder = outputFolder,
                Overwrite = true,
            }, cts.Token));

        Assert.Empty(Directory.GetFiles(outputFolder));
    }
}
