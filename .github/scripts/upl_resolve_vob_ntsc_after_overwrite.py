from pathlib import Path

path = Path("src/seconv/Core/SubtitleConverter.cs")
text = path.read_text(encoding="utf-8-sig")

old_call = """            // IsPal — there's no single reliable auto-detect from VOB alone (would need
            // IFO parsing). Default to PAL to match the GUI's batch converter. Future
            // work: add --vob-pal/--vob-ntsc and/or read VIDEO_TS.IFO.
            var outputs = VobSubExtractor.Extract(vobFiles, outputBase, isPal: true, overwrite: options.Overwrite);
"""
new_call = """            // There is no reliable PAL/NTSC auto-detect from VOB alone without IFO parsing.
            // Preserve PAL as the default, while allowing the CLI to select NTSC explicitly.
            var outputs = VobSubExtractor.Extract(vobFiles, outputBase, options.VobIsPal, overwrite: options.Overwrite);
"""
assert text.count(old_call) == 1, text.count(old_call)
text = text.replace(old_call, new_call)

old_options = """    public double? Fps { get; init; }
    public double? TargetFps { get; init; }
    public bool Overwrite { get; init; }

    /// <summary>--keep-timestamp: copy the source file's creation/last-write time onto every output file.</summary>
"""
new_options = """    public double? Fps { get; init; }
    public double? TargetFps { get; init; }
    public bool Overwrite { get; init; }

    /// <summary>VOB input video standard: <c>true</c> for PAL (default), <c>false</c> for NTSC.</summary>
    public bool VobIsPal { get; init; } = true;

    /// <summary>--keep-timestamp: copy the source file's creation/last-write time onto every output file.</summary>
"""
assert text.count(old_options) == 1, text.count(old_options)
text = text.replace(old_options, new_options)

path.write_text(text, encoding="utf-8")
