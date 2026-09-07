from pathlib import Path

path = Path('.github/workflows/build-ui.yml')
text = path.read_text(encoding='utf-8-sig')
old = '''      - name: Install Inno Setup 6.7.3
        # Pin an exact version: the .iss script requires 6.7.3+, but Chocolatey still ships 6.7.1.
        run: |
          $url = "https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe"
          Invoke-WebRequest -Uri $url -OutFile "$env:RUNNER_TEMP\\innosetup.exe"
          Start-Process -FilePath "$env:RUNNER_TEMP\\innosetup.exe" `
            -ArgumentList "/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/SP-" -Wait

      - name: Build Inno Setup installer
        run: |
          & "C:\\Program Files (x86)\\Inno Setup 6\\ISCC.exe" "installer\\WindowsInno\\Subtitle_Edit_Installer.iss"
'''
new = '''      - name: Install Inno Setup 7.1.0 x64
        # Pin the exact x64 release and verify its hash before executing it.
        run: |
          $url = "https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe"
          $expectedSha256 = "0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f"
          Invoke-WebRequest -Uri $url -OutFile "$env:RUNNER_TEMP\\innosetup.exe"
          $actualSha256 = (Get-FileHash "$env:RUNNER_TEMP\\innosetup.exe" -Algorithm SHA256).Hash.ToLowerInvariant()
          if ($actualSha256 -ne $expectedSha256) {
            throw "Inno Setup SHA-256 mismatch: expected $expectedSha256, got $actualSha256"
          }
          Start-Process -FilePath "$env:RUNNER_TEMP\\innosetup.exe" `
            -ArgumentList "/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/SP-" -Wait

      - name: Build Inno Setup installer
        run: |
          & "C:\\Program Files\\Inno Setup 7\\ISCC.exe" "installer\\WindowsInno\\Subtitle_Edit_Installer.iss"
'''
assert text.count(old) == 1, text.count(old)
path.write_text(text.replace(old, new, 1), encoding='utf-8')
