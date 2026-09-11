using System;
using System.Linq;
using Nikse.SubtitleEdit.Features.Ocr;
using Nikse.SubtitleEdit.Features.Ocr.Download;

namespace UITests.Features.Ocr.Engines;

/// <summary>
/// Guards the Paddle OCR download table against a half-applied version bump: the engine and
/// the models used to be pinned in several places, and the folder stripped when unpacking was
/// a separate literal from the URL it belongs to. Both failure modes only show up at runtime
/// (a 404, or an unpack that produces an empty install), so they are pinned down here.
/// </summary>
public class PaddleOcrDownloadArchiveTests
{
    public static TheoryData<PaddleOcrDownloadType> AllDownloadTypes()
    {
        var data = new TheoryData<PaddleOcrDownloadType>();
        foreach (var downloadType in Enum.GetValues<PaddleOcrDownloadType>())
        {
            data.Add(downloadType);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllDownloadTypes))]
    public void EveryDownloadType_HasAnArchive(PaddleOcrDownloadType downloadType)
    {
        var archive = PaddleOcr.GetArchive(downloadType);

        Assert.NotEmpty(archive.Assets);
        Assert.NotEmpty(archive.Urls);
        Assert.NotEmpty(archive.RootFolderInArchive);
        Assert.All(archive.Assets, asset =>
        {
            Assert.StartsWith("https://github.com/timminator/PaddleOCR-Standalone/releases/download/", asset.Url, StringComparison.Ordinal);
            Assert.Matches("^[0-9a-f]{64}$", asset.Sha256);
        });
    }

    [Theory]
    [InlineData("PaddleOCR.PP-OCRv6.support.files.VideOCR.7z", "7f98a187a1d8d9b5291f3be7cd6a6b693b32ddffd75d39c05d333d8f0b3ee145")]
    [InlineData("PaddleOCR-CPU-v3.7.0.7z", "a1b597f5620d1a86cec606b50908a12fc1b215adf6807be5538b7ca6bddc9d20")]
    [InlineData("PaddleOCR-GPU-v3.7.0-CUDA-11.8.7z", "5bfe2009cab89ce7f6b70f43f8250460ce6ccc6ccf176b95e0c363079bc4da50")]
    [InlineData("PaddleOCR-GPU-v3.7.0-CUDA-12.9.7z", "6a2c1f17f093403c8f2f4c4c7b81148b29abe710604aca8fac403af2be173cab")]
    [InlineData("PaddleOCR-CPU-v3.7.0-Linux.7z", "1d2bd1db1d534dcd433c2d658f1c9ed13beb92fc7201a7049d376bd15e8fc39e")]
    [InlineData("PaddleOCR-GPU-v3.7.0-CUDA-11.8-Linux.7z", "3850afef8ba8bf9f65911e855a866f9df0de06b0b8f0030dbd827162819d7158")]
    [InlineData("PaddleOCR-GPU-v3.7.0-CUDA-12.9-Linux.7z.001", "e154edaa5f80913d2a3aba0c05110ebf09f5d100f9db1b11e2d2d2b61bff4212")]
    [InlineData("PaddleOCR-GPU-v3.7.0-CUDA-12.9-Linux.7z.002", "900200376f77a85fc4fc2562b1832fc547092eaa6951772894666be87585bf89")]
    public void PublishedAssetDigest_IsPinned(string fileName, string expected)
    {
        var asset = Assert.Single(
            Enum.GetValues<PaddleOcrDownloadType>()
                .SelectMany(downloadType => PaddleOcr.GetArchive(downloadType).Assets)
                .Where(candidate => candidate.Url.EndsWith("/" + fileName, StringComparison.Ordinal)));

        Assert.Equal(expected, asset.Sha256);
    }

    [Theory]
    [MemberData(nameof(AllDownloadTypes))]
    public void EveryDownloadType_UsesOneRelease(PaddleOcrDownloadType downloadType)
    {
        // Engine and models are versioned independently upstream but downloaded from the same
        // tag here; mixing tags within one archive would download volumes that do not match.
        var tags = PaddleOcr.GetArchive(downloadType).Urls
            .Select(url => url[..url.LastIndexOf('/')])
            .Distinct();

        Assert.Single(tags);
    }

    [Theory]
    [MemberData(nameof(AllDownloadTypes))]
    public void MultiVolumeArchives_AreOrderedByVolumeNumber(PaddleOcrDownloadType downloadType)
    {
        // The extractor is handed the first downloaded file, so ".7z.001" has to come first.
        var fileNames = PaddleOcr.GetArchive(downloadType).Urls
            .Select(url => url[(url.LastIndexOf('/') + 1)..])
            .ToList();

        if (fileNames.Count == 1)
        {
            Assert.DoesNotContain(".7z.", fileNames[0], StringComparison.Ordinal);
            return;
        }

        Assert.Equal(fileNames.OrderBy(f => f, StringComparer.Ordinal), fileNames);
        Assert.EndsWith(".7z.001", fileNames[0], StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllDownloadTypes))]
    public void EngineArchives_StripTheFolderNamedAfterTheArchive(PaddleOcrDownloadType downloadType)
    {
        if (downloadType == PaddleOcrDownloadType.Models)
        {
            // The models archive is the exception: its root folder drops the ".VideOCR" part.
            var models = PaddleOcr.GetArchive(PaddleOcrDownloadType.Models);
            Assert.Equal("PaddleOCR.PP-OCRv6.support.files", models.RootFolderInArchive);
            return;
        }

        var archive = PaddleOcr.GetArchive(downloadType);
        var fileName = archive.Urls[0][(archive.Urls[0].LastIndexOf('/') + 1)..];

        Assert.Equal(fileName[..fileName.IndexOf(".7z", StringComparison.Ordinal)], archive.RootFolderInArchive);
    }
}
