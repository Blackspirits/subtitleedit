from pathlib import Path

p = Path("installer/WindowsInno/Subtitle_Edit_Installer.iss")
raw = p.read_bytes()
bom = raw.startswith(b"\xef\xbb\xbf")
text = raw.decode("utf-8-sig")
newline = "\r\n" if "\r\n" in text else "\n"

old_delete = "Type: files; Name: {userappdata}\\Subtitle Edit\\Settings.xml; Tasks: reset_settings"
new_delete = (
    "Type: files; Name: {userappdata}\\Subtitle Edit\\Settings.json; Tasks: reset_settings" + newline +
    "Type: files; Name: {userappdata}\\Subtitle Edit\\Settings.xml;  Tasks: reset_settings"
)
assert text.count(old_delete) == 1, f"unexpected Settings.xml reset anchor count: {text.count(old_delete)}"
text = text.replace(old_delete, new_delete, 1)

old_exist = newline.join([
    "function SettingsExist(): Boolean;",
    "begin",
    "  Result := FileExists(ExpandConstant('{userappdata}\\Subtitle Edit\\Settings.xml'));",
    "end;",
    "",
])
new_exist = newline.join([
    "function SettingsExist(): Boolean;",
    "begin",
    "  Result :=",
    "    FileExists(ExpandConstant('{userappdata}\\Subtitle Edit\\Settings.json')) or",
    "    FileExists(ExpandConstant('{userappdata}\\Subtitle Edit\\Settings.xml'));",
    "end;",
    "",
])
assert text.count(old_exist) == 1, f"unexpected SettingsExist anchor count: {text.count(old_exist)}"
text = text.replace(old_exist, new_exist, 1)

p.write_bytes((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))
