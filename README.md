# Simple Duplicate File Finder for Windows

Find duplicate files quickly by size, name, and hash.

This is a Microsoft Store-ready WPF utility built on .NET 8 for duplicate file detection and cleanup.

## Current features

- Multi-folder scan queue with size/type filters
- Duplicate discovery through size bucketing + SHA-256 hashing
- Duplicate group review grid and file-level action controls
- Keep newest/oldest presets
- Delete selected files or move selected files to local Quarantine folder
- CSV report export
- Trial and license flow:
  - 15 day fully functional trial
  - Full version key unlock
- Activity log file and built-in View Logs action
- About page with usage guidance and trial/full status

## Build

```powershell
dotnet build .\SimpleDuplicateFileFinder\SimpleDuplicateFileFinder.csproj -c Release
```

## Store readiness

- Microsoft Store price target: **$1.99 USD**
- A Store manifest is included at `SimpleDuplicateFileFinder\\Package.appxmanifest`.
- Store assets are available in `Store-Assets` and include:
  - `StoreLogo.png`
  - `Square44x44Logo.png`
  - `Square150x150Logo.png`
  - `Square310x310Logo.png`
  - `Wide310x150Logo.png`
  - `SplashScreen.png`
  - `Screenshots/screenshot-home.png`
  - `Screenshots/screenshot-setup.png`
  - `Screenshots/screenshot-review.png`

## Important notes

Before final submission, reserve the exact Partner Center name and match package identity publisher/brand values in `Package.appxmanifest`.
