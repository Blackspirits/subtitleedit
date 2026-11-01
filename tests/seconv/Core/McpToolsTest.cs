using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SeConv.Mcp;
using Xunit;

namespace SeConvTests.Core;

public class McpToolsTest : IDisposable
{
    private readonly string _tempDir;

    public McpToolsTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "seconv-mcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static JsonElement Payload(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        return JsonDocument.Parse(text).RootElement;
    }

    private static string ErrorText(CallToolResult result)
    {
        Assert.True(result.IsError);
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("--verbose", true)]
    [InlineData("-v", true)]
    public void McpServerHost_ParsesSupportedArguments(string? argument, bool expectedVerbose)
    {
        string[] args = argument is null ? ["--read-dir", _tempDir] : ["--read-dir", _tempDir, argument];
        Assert.True(McpServerHost.TryParseArguments(args, out var verbose,
            out var reads, out var writes, out var error));
        Assert.Equal(expectedVerbose, verbose);
        Assert.Equal(_tempDir, Assert.Single(reads));
        Assert.Empty(writes);
        Assert.Null(error);
    }

    [Fact]
    public void McpServerHost_RejectsUnknownArguments()
    {
        Assert.False(McpServerHost.TryParseArguments(["--bogus"], out _, out _, out _, out var error));
        Assert.NotNull(error);
        Assert.Contains("--bogus", error);
    }

    [Fact]
    public void McpServerHost_RejectsMissingRootAndDanglingOptions()
    {
        Assert.False(McpServerHost.TryParseArguments([], out _, out _, out _, out var missing));
        Assert.Contains("--read-dir", missing);
        Assert.False(McpServerHost.TryParseArguments(["--read-dir"], out _, out _, out _, out var bad));
        Assert.Contains("Missing directory", bad);
    }

    [Theory]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("zho_Hant", "zho_Hant")]
    [InlineData("eng", "eng")]
    [InlineData("..\\..\\secret:track/evil", "secrettrackevil")]
    [InlineData("../unsafe", "unsafe")]
    [InlineData("e:n?g|*<bad>", "engbad")]
    public void ContainerLanguageCode_CannotInjectPathComponents(string input, string expected)
    {
        var safe = SeConv.Core.ContainerSubtitleLoader.SanitizeLang(input);
        Assert.Equal(expected, safe);
        Assert.DoesNotContain('\\', safe);
        Assert.DoesNotContain('/', safe);
        Assert.DoesNotContain(':', safe);
        Assert.DoesNotContain('.', safe);
    }

    [Fact]
    public void PathPolicy_DeniesTraversalAndUnapprovedWrites()
    {
        var root = Path.Combine(_tempDir, "approved");
        Directory.CreateDirectory(root);
        var policy = new McpPathPolicy([root], []);

        policy.RequireRead(Path.Combine(root, "movie.srt"));
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireRead(Path.Combine(root, "nested", "..", "movie.srt")));
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireRead(Path.Combine(root, "..", "outside.srt")));
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireReadPattern(Path.Combine(root, "nested", "..", "*.srt")));
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireRead(root + "-other/movie.srt"));
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireWrite(Path.Combine(root, "translated.srt")));
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireConversionPaths([Path.Combine(root, "*.srt")], null, null));
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireReadPattern(Path.Combine(root, "*", "movie.srt")));

        var writer = new McpPathPolicy([root], [root]);
        writer.RequireConversionPaths([Path.Combine(root, "*.srt")], root, "movie.vtt");
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            writer.RequireConversionPaths([Path.Combine(root, "*.srt")], root, "../escape.vtt"));
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            writer.RequireConversionPaths([Path.Combine(root, "*.srt")], "..", "escape.vtt"));
    }

    [Fact]
    public void PathPolicy_RefusesSymlinkEscape()
    {
        if (OperatingSystem.IsWindows())
        {
            // Developer mode or privileges may be needed to create Windows symlinks.
            return;
        }

        var root = Path.Combine(_tempDir, "allowed");
        var outside = Path.Combine(_tempDir, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var link = Path.Combine(root, "link");
        Directory.CreateSymbolicLink(link, outside);
        var privateFile = Path.Combine(outside, "private.srt");
        File.WriteAllText(privateFile, "Private subtitles");
        var fileLink = Path.Combine(root, "escape.srt");
        File.CreateSymbolicLink(fileLink, privateFile);
        var policy = new McpPathPolicy([root], [root]);

        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireRead(Path.Combine(link, "private.srt")));
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireWrite(Path.Combine(link, "changed.srt")));
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireRead(fileLink));
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireReadPattern(Path.Combine(root, "*.srt")));
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireRead(Path.Combine(link, "..", "private.srt")));
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireConversionPaths(
                [Path.Combine(root, "link", "..", "*.srt")], root, null));
    }

    [Fact]
    public void PathPolicy_RejectsUnauthorizedVobSubCompanionFiles()
    {
        if (OperatingSystem.IsWindows())
        {
            // Symlink creation requires developer mode or elevated rights on Windows.
            return;
        }

        var root = Path.Combine(_tempDir, "allowed-vobsub");
        var outside = Path.Combine(_tempDir, "outside-vobsub");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var idx = Path.Combine(root, "movie.idx");
        var sub = Path.Combine(root, "movie.sub");
        var privateSub = Path.Combine(outside, "private.sub");
        var privateIdx = Path.Combine(outside, "private.idx");
        File.WriteAllText(idx, "index");
        File.WriteAllText(privateSub, "private images");
        File.WriteAllText(privateIdx, "private index");
        File.CreateSymbolicLink(sub, privateSub);

        var policy = new McpPathPolicy([root], [root]);
        policy.RequireRead(idx); // The requested .idx is safe by itself.
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireReadWithCompanions(idx));
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireReadPattern(idx));
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireReadPattern(Path.Combine(root, "*.idx")));

        File.Delete(sub);
        File.WriteAllText(sub, "local images");
        File.Delete(idx);
        File.CreateSymbolicLink(idx, privateIdx);
        policy.RequireRead(sub); // The requested .sub is safe by itself.
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireReadWithCompanions(sub));
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireReadPattern(Path.Combine(root, "*.sub")));
    }

    [Fact]
    public void PathPolicy_RejectsInputPathsDisguisedByLeadingWhitespace()
    {
        var cwd = Directory.GetCurrentDirectory();
        var outside = Path.Combine(_tempDir, "private.srt");
        File.WriteAllText(outside, "1\n00:00:01,000 --> 00:00:02,000\nPrivate.\n");

        // GetInputFiles trims patterns before resolving roots. A leading space
        // must not make this absolute path appear relative to the allowed cwd.
        var policy = new McpPathPolicy([cwd], [cwd]);
        foreach (var disguised in new[] { " " + outside, "\t" + outside, " " + outside + " " })
        {
            Assert.Throws<ModelContextProtocol.McpException>(() =>
                policy.RequireReadPattern(disguised));
            Assert.Throws<ModelContextProtocol.McpException>(() =>
                policy.RequireConversionPaths([disguised], cwd, null));
        }

        // Whitespace around a genuinely approved absolute path remains usable.
        var local = Path.Combine(cwd, "source.srt");
        policy.RequireReadPattern(" " + local + " ");
    }

    [Fact]
    public void PathPolicy_RejectsLegacyCommaPatternSplitting()
    {
        var cwd = Directory.GetCurrentDirectory();
        var policy = new McpPathPolicy([cwd], [cwd]);

        // A validated relative path such as "safe/file.srt,../outside.srt"
        // must not become multiple unchecked CLI inputs during conversion.
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireReadPattern("safe/file.srt,../outside.srt"));
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireConversionPaths(["safe/file.srt,../outside.srt"], cwd, null));

        // Absolute paths containing commas are literal names, not split by seconv.
        policy.RequireReadPattern(Path.Combine(cwd, "movie, director-cut.srt"));
    }

    [Fact]
    public void PathPolicy_RejectsUnauthorizedDvdSidecars()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Symlink creation may require special permissions.
        }

        var root = Path.Combine(_tempDir, "allowed-dvd");
        var outside = Path.Combine(_tempDir, "outside-dvd");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var vob = Path.Combine(root, "VTS_01_1.VOB");
        var ifo = Path.Combine(root, "VTS_01_0.IFO");
        var privateIfo = Path.Combine(outside, "private.IFO");
        var privateVob = Path.Combine(outside, "private.VOB");
        File.WriteAllText(vob, "local VOB");
        File.WriteAllText(privateIfo, "private IFO");
        File.WriteAllText(privateVob, "private VOB");
        File.CreateSymbolicLink(ifo, privateIfo);

        var policy = new McpPathPolicy([root], [root]);
        policy.RequireRead(vob);
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireReadWithCompanions(vob));

        File.Delete(ifo);
        File.WriteAllText(ifo, "local IFO");
        File.Delete(vob);
        File.CreateSymbolicLink(vob, privateVob);
        policy.RequireRead(ifo);
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireReadWithCompanions(ifo));

        // A .BUP may cause the DVD extractor to read its .IFO sibling.
        // Verify the .IFO link is rejected even when the VOB is safe.
        File.Delete(vob);
        File.WriteAllText(vob, "local VOB");
        File.Delete(ifo);
        File.CreateSymbolicLink(ifo, privateIfo);
        var bup = Path.Combine(root, "VTS_01_0.BUP");
        File.WriteAllText(bup, "local BUP");
        policy.RequireRead(bup);
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            policy.RequireReadWithCompanions(bup));
    }

    [Fact]
    public void PathPolicy_RejectsResolvedOutputSymlinksAndFallbackNames()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Symlink creation may require developer mode or elevation.
        }

        var read = Path.Combine(_tempDir, "input-scope");
        var write = Path.Combine(_tempDir, "output-scope");
        var outside = Path.Combine(_tempDir, "outside-scope");
        Directory.CreateDirectory(read);
        Directory.CreateDirectory(write);
        Directory.CreateDirectory(outside);
        var input = Path.Combine(read, "movie.srt");
        File.WriteAllText(input, "1\\n00:00:01,000 --> 00:00:02,000\\nHello.\\n");
        var output = Path.Combine(write, "movie.vtt");
        var privateOutput = Path.Combine(outside, "private.vtt");
        File.WriteAllText(privateOutput, "private data");
        File.CreateSymbolicLink(output, privateOutput);

        var policy = new McpPathPolicy([read], [write]);
        var options = new SeConv.Core.ConversionOptions
        {
            Patterns = [input],
            Format = "webvtt",
            OutputFolder = write,
            Overwrite = true,
            ValidateOutputPath = policy.RequireWrite,
        };
        Assert.Throws<ModelContextProtocol.McpException>(() =>
            SeConv.Core.SubtitleConverter.ResolveOutputFileName(input, options));

        // With overwrite disabled the converter picks movie_2.vtt; that fallback
        // must also be checked, including a dangling link (File.Exists = false).
        File.Delete(output);
        File.WriteAllText(output, "existing output");
        var fallback = Path.Combine(write, "movie_2.vtt");
        var missingTarget = Path.Combine(outside, "missing.vtt");
        File.CreateSymbolicLink(fallback, missingTarget);
        Assert.False(File.Exists(missingTarget));
        // Runtimes differ on File.Exists(danglingLink): some count the
        // symlink itself, others follow the missing target. Regardless,
        // the policy must reject the link if it is ever selected for writing.
        Assert.Throws<ModelContextProtocol.McpException>(() => policy.RequireWrite(fallback));
        if (File.Exists(fallback))
        {
            // The converter skips an occupied filename; no write uses this link.
            var chosen = SeConv.Core.SubtitleConverter.ResolveOutputFileName(
                input, options with { Overwrite = false });
            Assert.NotEqual(fallback, chosen);
            policy.RequireWrite(chosen);
        }
        else
        {
            // If File.Exists reports false, _2 is selected and must be denied.
            Assert.Throws<ModelContextProtocol.McpException>(() =>
                SeConv.Core.SubtitleConverter.ResolveOutputFileName(
                    input, options with { Overwrite = false }));
        }
    }

    [Fact]
    public async Task McpConversion_RejectsImageListReferencesBeforeLoadingImages()
    {
        var read = Path.Combine(_tempDir, "image-list-input");
        var write = Path.Combine(_tempDir, "image-list-output");
        Directory.CreateDirectory(read);
        Directory.CreateDirectory(write);
        var input = Path.Combine(read, "images.srt");
        File.WriteAllText(input,
            "1\n00:00:01,000 --> 00:00:02,000\n../../private/0001.png\n\n" +
            "2\n00:00:03,000 --> 00:00:04,000\n../../private/0002.png\n\n" +
            "3\n00:00:05,000 --> 00:00:06,000\n../../private/0003.png\n\n");
        var policy = new McpPathPolicy([read], [write]);
        var options = new SeConv.Core.ConversionOptions
        {
            Patterns = [input],
            Format = "WebVTT",
            OutputFolder = write,
            ValidateOutputPath = policy.RequireWrite,
            Quiet = true,
        };

        var result = await new SeConv.Core.SubtitleConverter().ConvertAsync(options);
        Assert.False(result.Success);
        Assert.Contains(result.Errors,
            error => error.Contains("referenced image paths", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.GetFileSystemEntries(write));
    }

    [Fact]
    public void VobSubOutputFallback_ReservesSubAndIdxAsAPair()
    {
        var input = Path.Combine(_tempDir, "movie.srt");
        File.WriteAllText(input, "1\n00:00:01,000 --> 00:00:02,000\nHello.\n");
        File.WriteAllText(Path.Combine(_tempDir, "movie.idx"), "occupied index");
        var checkedPaths = new List<string>();
        var options = new SeConv.Core.ConversionOptions
        {
            Patterns = [input],
            Format = "VobSub",
            OutputFolder = _tempDir,
            Overwrite = false,
            ValidateOutputPath = checkedPaths.Add,
        };

        var first = SeConv.Core.SubtitleConverter.ResolveOutputFileName(input, options);
        Assert.Equal(Path.Combine(_tempDir, "movie_2.sub"), first);
        Assert.Equal([first, Path.ChangeExtension(first, ".idx")], checkedPaths);

        File.WriteAllText(Path.Combine(_tempDir, "movie_2.idx"), "occupied index");
        checkedPaths.Clear();
        var second = SeConv.Core.SubtitleConverter.ResolveOutputFileName(input, options);
        Assert.Equal(Path.Combine(_tempDir, "movie_3.sub"), second);
        Assert.Equal([second, Path.ChangeExtension(second, ".idx")], checkedPaths);
    }

    [Fact]
    public void VobSubStreamOutputPreflight_BlocksExistingIndexedPairs()
    {
        var first = Path.Combine(_tempDir, "movie.0.sub");
        var second = Path.Combine(_tempDir, "movie.1.sub");
        var secondIdx = Path.ChangeExtension(second, ".idx");
        File.WriteAllText(secondIdx, "existing index");

        Assert.Throws<InvalidOperationException>(() =>
            SeConv.Core.VobSubExtractor.ValidateOutputPairs([first, second], false));
        Assert.False(File.Exists(first));

        var checkedPaths = new List<string>();
        SeConv.Core.VobSubExtractor.ValidateOutputPairs([first, second], true, checkedPaths.Add);
        Assert.Equal([first, Path.ChangeExtension(first, ".idx"), second, secondIdx], checkedPaths);
    }

    [Theory]
    [InlineData("BdnXml")]
    [InlineData("BdnXml8Bit")]
    [InlineData("ImscImage")]
    [InlineData("Dost")]
    [InlineData("FcpImage")]
    [InlineData("DCinemaInterop")]
    [InlineData("DCinemaSmpte2014")]
    [InlineData("ImagesWithTimeCodes")]
    [InlineData("WebVttThumbnail")]
    public void MultiFileImageTargets_AreNotExposedThroughMcp(string format)
    {
        Assert.True(SubtitleTools.IsUnsupportedMultiFileImageTarget(format));
    }

    [Theory]
    [InlineData("BluRaySup")]
    [InlineData("DvdSup")]
    [InlineData("VobSub")]
    [InlineData("DCinemaSmpte2014PngMxf")]
    [InlineData("WebVTT")]
    public void SingleFileAndValidatedPairTargets_RemainSupported(string format)
    {
        Assert.False(SubtitleTools.IsUnsupportedMultiFileImageTarget(format));
    }

    [Theory]
    [InlineData("movie.txt")]
    [InlineData("movie.idx")]
    [InlineData("movie")]
    public async Task ConvertSubtitle_VobSubRequiresSubExtension(string filename)
    {
        var message = ErrorText(await SubtitleTools.ConvertSubtitle(
            inputs: [Fixtures.Path("test.srt")],
            format: "VobSub",
            outputFolder: _tempDir,
            outputFilename: filename));

        Assert.Contains("must end with .sub", message);
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task ConvertSubtitle_MultiFileImageTarget_IsRejectedBeforeWriting()
    {
        var message = ErrorText(await SubtitleTools.ConvertSubtitle(
            inputs: [Fixtures.Path("test.srt")],
            format: "BdnXml",
            outputFolder: _tempDir));

        Assert.Contains("additional output files", message);
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public void ListFormats_FilterMatchesIdNameAndExtension()
    {
        var all = Payload(SubtitleTools.ListFormats());
        Assert.True(all.GetProperty("total").GetInt32() > 300);

        var filtered = Payload(SubtitleTools.ListFormats("subrip"));
        var ids = filtered.GetProperty("formats").EnumerateArray().Select(f => f.GetProperty("id").GetString()).ToList();
        Assert.Contains("SubRip", ids);
        Assert.Equal(ids.Count, filtered.GetProperty("total").GetInt32());
        Assert.True(ids.Count < all.GetProperty("total").GetInt32());

        var extraIds = all.GetProperty("extraIds").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.DoesNotContain("customtext", extraIds);
    }

    [Fact]
    public async Task ConvertSubtitle_CustomText_IsRejectedBeforeWriting()
    {
        var message = ErrorText(await SubtitleTools.ConvertSubtitle(
            inputs: [Fixtures.Path("test.srt")],
            format: "customtext",
            outputFolder: _tempDir));

        Assert.Contains("--custom-format", message);
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public void SubtitleInfo_DetectsFixtureFormat()
    {
        var info = Payload(SubtitleTools.SubtitleInfo(Fixtures.Path("test.srt")));
        Assert.Equal("SubRip", info.GetProperty("format").GetString());
        Assert.True(info.GetProperty("paragraphCount").GetInt32() > 0);
    }

    [Fact]
    public void SubtitleInfo_MissingFile_IsToolError()
    {
        var message = ErrorText(SubtitleTools.SubtitleInfo(Path.Combine(_tempDir, "nope.srt")));
        Assert.Contains("nope.srt", message);
        Assert.DoesNotContain(_tempDir, message);
    }

    [Fact]
    public void ReadSubtitle_PagesParagraphs()
    {
        var path = Fixtures.Path("test.srt");
        var all = Payload(SubtitleTools.ReadSubtitle(path));
        var total = all.GetProperty("total").GetInt32();
        Assert.True(total > 1);
        Assert.False(all.GetProperty("hasMore").GetBoolean());

        var page = Payload(SubtitleTools.ReadSubtitle(path, start: 2, count: 1));
        Assert.Equal(1, page.GetProperty("returned").GetInt32());
        Assert.Equal(total > 2, page.GetProperty("hasMore").GetBoolean());
        var p = Assert.Single(page.GetProperty("paragraphs").EnumerateArray());
        Assert.Equal(2, p.GetProperty("number").GetInt32());
        Assert.True(p.GetProperty("endMs").GetInt64() >= p.GetProperty("startMs").GetInt64());
        Assert.False(string.IsNullOrEmpty(p.GetProperty("text").GetString()));
    }

    [Fact]
    public void LintSubtitle_ReportsOverlap()
    {
        var path = Path.Combine(_tempDir, "overlap.srt");
        File.WriteAllText(path,
            "1\n00:00:01,000 --> 00:00:05,000\nFirst.\n\n" +
            "2\n00:00:04,000 --> 00:00:06,000\nSecond.\n");

        var report = Payload(SubtitleTools.LintSubtitle(path));
        Assert.False(report.GetProperty("isClean").GetBoolean());
        Assert.Contains(report.GetProperty("issues").EnumerateArray(),
            i => i.GetProperty("type").GetString() == "overlap");
    }

    [Fact]
    public void ListRules_ExposeIds()
    {
        var fce = Payload(SubtitleTools.ListFixCommonErrorsRules());
        Assert.Contains(fce.GetProperty("rules").EnumerateArray(), r => r.GetProperty("id").GetString() == "FixCommas");

        var rf = Payload(SubtitleTools.ListRemoveFormattingRules());
        Assert.True(rf.GetProperty("total").GetInt32() > 0);
    }

    [Fact]
    public async Task ConvertSubtitle_WritesTargetFormatWithOffset()
    {
        var result = Payload(await SubtitleTools.ConvertSubtitle(
            inputs: [Fixtures.Path("test.srt")],
            format: "webvtt",
            outputFolder: _tempDir,
            offset: "00:00:01.000"));

        Assert.True(result.GetProperty("success").GetBoolean());
        var file = Assert.Single(result.GetProperty("files").EnumerateArray());
        var output = file.GetProperty("output").GetString()!;
        Assert.EndsWith(".vtt", output);
        Assert.True(File.Exists(output));

        var source = Payload(SubtitleTools.ReadSubtitle(Fixtures.Path("test.srt"), count: 1));
        var converted = Payload(SubtitleTools.ReadSubtitle(output, count: 1));
        var sourceStart = source.GetProperty("paragraphs")[0].GetProperty("startMs").GetInt64();
        var convertedStart = converted.GetProperty("paragraphs")[0].GetProperty("startMs").GetInt64();
        Assert.Equal(sourceStart + 1000, convertedStart);
    }

    [Fact]
    public async Task ConvertSubtitle_CoreFailure_IsToolErrorWithJsonPayload()
    {
        var missing = Path.Combine(_tempDir, "missing-input.srt");
        var result = await SubtitleTools.ConvertSubtitle(
            inputs: [missing],
            format: "srt",
            outputFolder: _tempDir);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        var payload = JsonDocument.Parse(text).RootElement;
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.True(payload.GetProperty("errors").GetArrayLength() > 0);
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task ConvertSubtitle_UnknownOperation_IsToolError()
    {
        var message = ErrorText(await SubtitleTools.ConvertSubtitle(
            inputs: [Fixtures.Path("test.srt")],
            format: "srt",
            outputFolder: _tempDir,
            operations: ["Bogus"]));

        Assert.Contains("Unknown operation 'Bogus'", message);
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Theory]
    [InlineData("offset", "not-a-time", "offset value")]
    [InlineData("resolution", "1920-by-1080", "Resolution")]
    public async Task ConvertSubtitle_InvalidParsedInput_IsActionable(string field, string value, string expected)
    {
        CallToolResult result;
        if (field == "offset")
        {
            result = await SubtitleTools.ConvertSubtitle(
                inputs: [Fixtures.Path("test.srt")],
                format: "srt",
                outputFolder: _tempDir,
                offset: value);
        }
        else
        {
            result = await SubtitleTools.ConvertSubtitle(
                inputs: [Fixtures.Path("test.srt")],
                format: "srt",
                outputFolder: _tempDir,
                resolution: value);
        }

        Assert.Contains(expected, ErrorText(result), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task ConvertSubtitle_UnknownRule_IsActionable()
    {
        var message = ErrorText(await SubtitleTools.ConvertSubtitle(
            inputs: [Fixtures.Path("test.srt")],
            format: "srt",
            outputFolder: _tempDir,
            fixCommonErrorsRules: "DefinitelyNotARule"));

        Assert.Contains("Unknown FixCommonErrors rule", message);
        Assert.Contains("list_fix_common_errors_rules", message);
        Assert.DoesNotContain("seconv list-fce-rules", message);
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task ConvertSubtitle_UnknownFceLanguage_IsRejectedInsteadOfFallingBack()
    {
        var message = ErrorText(await SubtitleTools.ConvertSubtitle(
            inputs: [Fixtures.Path("test.srt")],
            format: "srt",
            outputFolder: _tempDir,
            fixCommonErrorsRules: "FixCommas",
            fixCommonErrorsLanguage: "definitely-not-a-language"));

        Assert.Contains("not recognized", message);
        Assert.Contains("auto-detection", message);
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task ConvertSubtitle_NonPositiveSpeed_IsRejected()
    {
        var message = ErrorText(await SubtitleTools.ConvertSubtitle(
            inputs: [Fixtures.Path("test.srt")],
            format: "srt",
            outputFolder: _tempDir,
            changeSpeedPercent: 0));

        Assert.Contains("greater than 0", message);
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task ConvertSubtitle_RuleSelectionImpliesOperation()
    {
        var path = Path.Combine(_tempDir, "dots.srt");
        File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:03,000\n<i>Hello</i> world..\n");

        var result = Payload(await SubtitleTools.ConvertSubtitle(
            inputs: [path],
            format: "srt",
            outputFilename: Path.Combine(_tempDir, "out.srt"),
            removeFormattingRules: "all"));

        Assert.True(result.GetProperty("success").GetBoolean());
        var converted = Payload(SubtitleTools.ReadSubtitle(Path.Combine(_tempDir, "out.srt")));
        var text = converted.GetProperty("paragraphs")[0].GetProperty("text").GetString();
        Assert.DoesNotContain("<i>", text);
    }

    [Fact]
    public async Task ConvertSubtitle_CancelledWhileQueued_WritesNothing()
    {
        var gate = (SemaphoreSlim)typeof(SubtitleTools).GetField(
            "ToolGate", System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Static)!.GetValue(null)!;

        await gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            using var cts = new CancellationTokenSource();
            var conversion = SubtitleTools.ConvertSubtitle(
                inputs: [Fixtures.Path("test.srt")],
                format: "srt",
                outputFolder: _tempDir,
                cancellationToken: cts.Token);

            Assert.False(conversion.IsCompleted);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conversion);
            Assert.Empty(Directory.GetFiles(_tempDir));
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task ConvertSubtitle_PreCancelledToken_ThrowsWithoutWriting()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SubtitleTools.ConvertSubtitle(
                inputs: [Fixtures.Path("test.srt")],
                format: "srt",
                outputFolder: _tempDir,
                cancellationToken: cts.Token));

        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task StdioServer_CommaSeparatedInputCannotEscapeReadScopeThroughLeadingWhitespace()
    {
        if (OperatingSystem.IsWindows())
        {
            // An embedded second C:\\ drive prefix is not a valid Windows file
            // component; this Unix test exercises the legacy comma-splitting bug.
            return;
        }

        var allowed = Path.Combine(_tempDir, "approved-read");
        var outside = Path.Combine(_tempDir, "private");
        var output = Path.Combine(_tempDir, "approved-write");
        Directory.CreateDirectory(allowed);
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(output);

        var secret = Path.Combine(outside, "secret.srt");
        File.WriteAllText(secret, "1\n00:00:01,000 --> 00:00:02,000\nUNAUTHORIZED SUBTITLE\n");

        // Validating the trimmed value alone approves a missing path lexically
        // inside 'allowed'; the legacy CLI used to split the untrimmed entry
        // into two absolute paths, including the real, unapproved secret.
        var crafted = " " + Path.Combine(allowed, "missing.srt,") + secret;
        var scope = new McpPathPolicy([allowed], [output]);
        scope.RequireConversionPaths([crafted], output, null);

        var dll = Path.Combine(AppContext.BaseDirectory, "seconv.dll");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "seconv",
            Command = "dotnet",
            Arguments = [dll, "mcp", "--read-dir", allowed, "--write-dir", output],
        });
        var ct = TestContext.Current.CancellationToken;
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

        var rejected = await client.CallToolAsync("convert_subtitle",
            new Dictionary<string, object?>
            {
                ["inputs"] = new[] { crafted },
                ["format"] = "WebVTT",
                ["outputFolder"] = output,
            }, cancellationToken: ct);

        Assert.True(rejected.IsError);
        Assert.Empty(Directory.GetFiles(output));

        // Literal comma-named files remain supported after normalizing first.
        var commaFile = Path.Combine(allowed, "my, name.srt");
        File.WriteAllText(commaFile, "1\n00:00:01,000 --> 00:00:02,000\nAllowed text\n");
        var valid = await client.CallToolAsync("convert_subtitle",
            new Dictionary<string, object?>
            {
                ["inputs"] = new[] { " " + commaFile + " " },
                ["format"] = "WebVTT",
                ["outputFolder"] = output,
            }, cancellationToken: ct);

        Assert.True(Payload(valid).GetProperty("success").GetBoolean());
        Assert.Contains("Allowed text", File.ReadAllText(Path.Combine(output, "my, name.vtt")));
        Assert.DoesNotContain("UNAUTHORIZED SUBTITLE", File.ReadAllText(Path.Combine(output, "my, name.vtt")));
    }

    [Fact]
    public async Task StdioServer_ListsToolsWithSafeAnnotationsAndAnswersCalls()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "seconv.dll");
        Assert.True(File.Exists(dll), $"seconv.dll not found next to the tests: {dll}");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "seconv",
            Command = "dotnet",
            Arguments =
            [
                dll, "mcp",
                "--read-dir", Path.GetDirectoryName(Fixtures.Path("test.srt"))!,
                "--read-dir", _tempDir,
                "--write-dir", _tempDir,
            ],
        });

        var ct = TestContext.Current.CancellationToken;
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        Assert.Equal("seconv", client.ServerInfo.Name);

        var tools = await client.ListToolsAsync(cancellationToken: ct);
        var names = tools.Select(t => t.Name).ToHashSet();
        foreach (var expected in new[]
                 {
                     "list_formats", "subtitle_info", "read_subtitle", "lint_subtitle",
                     "convert_subtitle", "list_fix_common_errors_rules", "list_remove_formatting_rules",
                 })
        {
            Assert.Contains(expected, names);
        }

        foreach (var tool in tools)
        {
            var annotations = Assert.IsType<ToolAnnotations>(tool.ProtocolTool.Annotations);
            if (tool.Name == "convert_subtitle")
            {
                Assert.Equal(false, annotations.ReadOnlyHint);
                Assert.Equal(true, annotations.DestructiveHint);
                Assert.Equal(false, annotations.IdempotentHint);
                Assert.Equal(false, annotations.OpenWorldHint);
            }
            else
            {
                Assert.Equal(true, annotations.ReadOnlyHint);
                Assert.Equal(false, annotations.DestructiveHint);
                Assert.Equal(true, annotations.IdempotentHint);
                Assert.Equal(false, annotations.OpenWorldHint);
            }

            var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
            Assert.False(properties.TryGetProperty("cancellationToken", out _));
        }

        var info = await client.CallToolAsync("subtitle_info",
            new Dictionary<string, object?> { ["path"] = Fixtures.Path("test.vtt") },
            cancellationToken: ct);
        var payload = Payload(info);
        Assert.Equal("WebVTT", payload.GetProperty("format").GetString());

        var failure = await client.CallToolAsync("subtitle_info",
            new Dictionary<string, object?> { ["path"] = Path.Combine(_tempDir, "missing.srt") },
            cancellationToken: ct);
        var failureText = ErrorText(failure);
        Assert.Contains("missing.srt", failureText);
        Assert.DoesNotContain(_tempDir, failureText);

        // End-to-end through JSON-RPC, not a direct static tool invocation. Confirm a
        // real write reaches disk while the stdio protocol remains intact and readable.
        var output = Path.Combine(_tempDir, "mcp-converted.vtt");
        var conversion = await client.CallToolAsync("convert_subtitle",
            new Dictionary<string, object?>
            {
                ["inputs"] = new[] { Fixtures.Path("test.srt") },
                ["format"] = "WebVTT",
                ["outputFilename"] = output,
            },
            cancellationToken: ct);

        Assert.True(Payload(conversion).GetProperty("success").GetBoolean());
        Assert.True(File.Exists(output));
        Assert.Contains("WEBVTT", await File.ReadAllTextAsync(output, ct));

        // The server is still able to process calls after the potentially noisy
        // conversion path: no progress logging has corrupted its stdout stream.
        var afterConversion = await client.CallToolAsync("subtitle_info",
            new Dictionary<string, object?> { ["path"] = output },
            cancellationToken: ct);
        Assert.Equal("WebVTT", Payload(afterConversion).GetProperty("format").GetString());

        // The same live stdio server must refuse both reading outside its
        // configured roots and writing back into a read-only fixture folder.
        var outside = Path.Combine(Path.GetDirectoryName(_tempDir)!, "unapproved-folder",
            "not-authorized.srt");
        var deniedRead = await client.CallToolAsync("subtitle_info",
            new Dictionary<string, object?> { ["path"] = outside },
            cancellationToken: ct);
        Assert.Contains("outside the MCP-approved directories", ErrorText(deniedRead));

        var deniedWrite = await client.CallToolAsync("convert_subtitle",
            new Dictionary<string, object?>
            {
                ["inputs"] = new[] { Fixtures.Path("test.srt") },
                ["format"] = "WebVTT",
                ["outputFilename"] = Path.Combine(
                    Path.GetDirectoryName(Fixtures.Path("test.srt"))!, "denied-write.vtt"),
            },
            cancellationToken: ct);
        Assert.Contains("outside the MCP-approved directories", ErrorText(deniedWrite));
        Assert.False(File.Exists(Path.Combine(
            Path.GetDirectoryName(Fixtures.Path("test.srt"))!, "denied-write.vtt")));
    }
}
