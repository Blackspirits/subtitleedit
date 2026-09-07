from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    raw = p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    count = text.count(old)
    assert count == 1, f"{path}: expected one anchor, found {count}"
    text = text.replace(old, new, 1)
    p.write_bytes((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))


command = "src/seconv/Commands/ConvertCommand.cs"
converter = "src/seconv/Core/SubtitleConverter.cs"
tests = "tests/seconv/Core/VobSubExtractorTest.cs"

replace_once(
    command,
    """    public static string[] RawArgs { get; set; } = [];

    public sealed class Settings : CommandSettings
""",
    """    public static string[] RawArgs { get; set; } = [];

    internal static bool ResolveVobIsPal(bool vobPal, bool vobNtsc)
    {
        if (vobPal && vobNtsc)
        {
            throw new ArgumentException("--vob-pal and --vob-ntsc are mutually exclusive.");
        }

        // Preserve the existing CLI behaviour unless NTSC is explicitly selected.
        return !vobNtsc;
    }

    public sealed class Settings : CommandSettings
""",
)

replace_once(
    command,
    """        [CommandOption("--fps")]
        [Description("Frame rate")]
        public double? Fps { get; init; }

        [CommandOption("--input-folder|--inputfolder")]
""",
    """        [CommandOption("--fps")]
        [Description("Frame rate")]
        public double? Fps { get; init; }

        [CommandOption("--vob-pal")]
        [Description("VOB input: treat DVD video as PAL (720x576; default)")]
        public bool VobPal { get; init; }

        [CommandOption("--vob-ntsc")]
        [Description("VOB input: treat DVD video as NTSC (720x480)")]
        public bool VobNtsc { get; init; }

        [CommandOption("--input-folder|--inputfolder")]
""",
)

replace_once(
    command,
    """            // Parse offset if supplied
            TimeSpan? offset = null;
""",
    """            bool vobIsPal;
            try
            {
                vobIsPal = ResolveVobIsPal(settings.VobPal, settings.VobNtsc);
            }
            catch (ArgumentException ex)
            {
                return Fail(settings, ex.Message);
            }

            // Parse offset if supplied
            TimeSpan? offset = null;
""",
)

replace_once(
    command,
    """                Fps = settings.Fps,
                TargetFps = settings.TargetFps,
                Overwrite = settings.Overwrite,
""",
    """                Fps = settings.Fps,
                TargetFps = settings.TargetFps,
                VobIsPal = vobIsPal,
                Overwrite = settings.Overwrite,
""",
)

replace_once(
    converter,
    """            // IsPal — there's no single reliable auto-detect from VOB alone (would need
            // IFO parsing). Default to PAL to match the GUI's batch converter. Future
            // work: add --vob-pal/--vob-ntsc and/or read VIDEO_TS.IFO.
            var outputs = VobSubExtractor.Extract(vobFiles, outputBase, isPal: true);
""",
    """            // There is no reliable PAL/NTSC auto-detect from VOB alone without IFO parsing.
            // Preserve PAL as the default, while allowing the CLI to select NTSC explicitly.
            var outputs = VobSubExtractor.Extract(vobFiles, outputBase, options.VobIsPal);
""",
)

replace_once(
    converter,
    """    public double? Fps { get; init; }
    public double? TargetFps { get; init; }
    public bool Overwrite { get; init; }
""",
    """    public double? Fps { get; init; }
    public double? TargetFps { get; init; }

    /// <summary>DVD VOB extraction video standard. PAL remains the default for backwards compatibility.</summary>
    public bool VobIsPal { get; init; } = true;

    public bool Overwrite { get; init; }
""",
)

replace_once(
    tests,
    """using SeConv.Core;
using Xunit;
""",
    """using SeConv.Commands;
using SeConv.Core;
using Xunit;
""",
)

replace_once(
    tests,
    """    [Fact]
    public async Task ConvertAsync_VobInput_NonVobSubTarget_ErrorsWithGuidance()
""",
    """    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public void ResolveVobIsPal_SelectsExpectedStandard(bool vobPal, bool vobNtsc, bool expectedIsPal)
    {
        Assert.Equal(expectedIsPal, ConvertCommand.ResolveVobIsPal(vobPal, vobNtsc));
    }

    [Fact]
    public void ResolveVobIsPal_RejectsConflictingFlags()
    {
        var ex = Assert.Throws<ArgumentException>(() => ConvertCommand.ResolveVobIsPal(vobPal: true, vobNtsc: true));
        Assert.Contains("mutually exclusive", ex.Message);
    }

    [Fact]
    public void ConversionOptions_VobStandardDefaultsToPal_AndAllowsNtsc()
    {
        var defaultOptions = new ConversionOptions { Patterns = [], Format = "VobSub" };
        var ntscOptions = new ConversionOptions { Patterns = [], Format = "VobSub", VobIsPal = false };

        Assert.True(defaultOptions.VobIsPal);
        Assert.False(ntscOptions.VobIsPal);
    }

    [Fact]
    public async Task ConvertAsync_VobInput_NonVobSubTarget_ErrorsWithGuidance()
""",
)
