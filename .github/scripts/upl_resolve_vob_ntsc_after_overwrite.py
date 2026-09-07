from pathlib import Path

path = Path("src/seconv/Core/SubtitleConverter.cs")
text = path.read_text(encoding="utf-8-sig")
old = """            // IsPal — there's no single reliable auto-detect from VOB alone (would need
            // IFO parsing). Default to PAL to match the GUI's batch converter. Future
            // work: add --vob-pal/--vob-ntsc and/or read VIDEO_TS.IFO.
            var outputs = VobSubExtractor.Extract(vobFiles, outputBase, isPal: true, overwrite: options.Overwrite);
"""
new = """            // There is no reliable PAL/NTSC auto-detect from VOB alone without IFO parsing.
            // Preserve PAL as the default, while allowing the CLI to select NTSC explicitly.
            var outputs = VobSubExtractor.Extract(vobFiles, outputBase, options.VobIsPal, overwrite: options.Overwrite);
"""
assert text.count(old) == 1, text.count(old)
path.write_text(text.replace(old, new), encoding="utf-8")
