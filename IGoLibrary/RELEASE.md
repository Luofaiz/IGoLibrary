# Release workflow

IGoLibrary publishes Windows builds through GitHub Releases.

1. Build the installer and update manifest:

```powershell
.\build\publish-installer.ps1 -Version "1.0.17" -Notes "Infrastructure hardening and task history improvements."
```

2. Upload release assets:

```powershell
.\build\publish-github-release.ps1 -Version "1.0.17" -Repo "Luofaiz/IGoLibrary" -Notes "Infrastructure hardening and task history improvements."
```

The desktop app checks this manifest:

```text
https://github.com/Luofaiz/IGoLibrary/releases/latest/download/latest.json
```

The manifest uses this shape:

```json
{
  "version": "1.0.17",
  "notes": "Initial release.",
  "downloadUrl": "https://github.com/Luofaiz/IGoLibrary/releases/latest/download/IGoLibrarySetup.exe",
  "downloadSha256": "<sha256>",
  "releaseUrl": "https://github.com/Luofaiz/IGoLibrary/releases/latest"
}
```

Each release should include:

- `IGoLibrarySetup.exe`
- `latest.json`
- `IGoLibrary-Windows-x64.zip`
