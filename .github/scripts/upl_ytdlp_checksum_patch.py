from pathlib import Path

service_path = Path("src/ui/Logic/Download/YtDlpDownloadService.cs")
service = service_path.read_text(encoding="utf-8-sig")
old = '''        var assetName = Path.GetFileName(filePath);
        if (!KnownSha256.TryGetValue(version, out var byAsset) ||
'''
new = '''        var assetName = Path.GetFileName(filePath);
        if (assetName.EndsWith(".part", StringComparison.Ordinal))
        {
            assetName = assetName[..^".part".Length];
        }

        if (!KnownSha256.TryGetValue(version, out var byAsset) ||
'''
assert service.count(old) == 1, service.count(old)
service = service.replace(old, new)
service_path.write_text(service, encoding="utf-8")

test_path = Path("tests/UI/Logic/Download/YtDlpDownloadServiceTests.cs")
test = test_path.read_text(encoding="utf-8-sig")
anchor = '''    [Fact]
    public async Task VerifyChecksumAsync_UnknownAsset_IsNoOp_AndKeepsFile()
'''
regression = '''    [Fact]
    public async Task VerifyChecksumAsync_PartFile_Mismatch_ThrowsAndDeletesFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "VerifyPartMismatch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "yt-dlp.exe.part");
        await File.WriteAllTextAsync(path, "this is not really yt-dlp", TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                YtDlpDownloadService.VerifyChecksumAsync(path, YtDlpDownloadService.CurrentVersion, TestContext.Current.CancellationToken));

            Assert.False(File.Exists(path), "A downloaded .part binary that fails verification must be deleted.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

'''
assert test.count(anchor) == 1, test.count(anchor)
test = test.replace(anchor, regression + anchor)
test_path.write_text(test, encoding="utf-8")
