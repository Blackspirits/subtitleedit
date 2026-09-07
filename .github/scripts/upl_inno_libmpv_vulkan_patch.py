from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    raw = p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    newline = "\r\n" if "\r\n" in text else "\n"
    old = old.replace("\n", newline)
    new = new.replace("\n", newline)
    count = text.count(old)
    assert count == 1, f"{path}: expected one anchor, found {count}"
    text = text.replace(old, new, 1)
    p.write_bytes((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))


replace_once(
    "installer/WindowsInno/Subtitle_Edit_Installer.iss",
    "Source: {#bindir}\\libmpv-2.dll;          DestDir: {userappdata}\\Subtitle Edit; Flags: ignoreversion",
    "Source: {#bindir}\\libmpv-2.dll;          DestDir: {userappdata}\\Subtitle Edit; Flags: ignoreversion\n"
    "Source: ..\\..\\libmpv-temp\\vulkan-1.dll; DestDir: {userappdata}\\Subtitle Edit; Flags: ignoreversion",
)
