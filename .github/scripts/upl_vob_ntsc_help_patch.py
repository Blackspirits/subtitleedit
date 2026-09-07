from pathlib import Path

p = Path("src/seconv/Helpers/HelpDisplay.cs")
raw = p.read_bytes()
bom = raw.startswith(b"\xef\xbb\xbf")
text = raw.decode("utf-8-sig")
old = '''        ShowParameter(console, "--fps:<frame rate>", "Frame rate for conversion");
        ShowParameter(console, "--input-folder:<folder name>", "Input folder path");
'''
new = '''        ShowParameter(console, "--fps:<frame rate>", "Frame rate for conversion");
        ShowParameter(console, "--vob-pal", "VOB input: treat DVD video as PAL (720x576; default)");
        ShowParameter(console, "--vob-ntsc", "VOB input: treat DVD video as NTSC (720x480)");
        ShowParameter(console, "--input-folder:<folder name>", "Input folder path");
'''
assert text.count(old) == 1, f"HelpDisplay.cs: expected one anchor, found {text.count(old)}"
text = text.replace(old, new, 1)
p.write_bytes((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))
