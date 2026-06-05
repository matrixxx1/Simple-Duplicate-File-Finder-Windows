# Simple Duplicate File Finder for Windows

Find duplicate files quickly by size, name, and hash.

This is the initial Microsoft Store-oriented Windows desktop app scaffold for $(System.Collections.Hashtable.Title). It uses .NET 8 and WPF, keeps the first implementation local-first, and includes a repo-root Store-Assets folder for listing and privacy handoff material.

## Initial scope

- Folder-based duplicate scan queue
- Size and hash comparison workflow
- Keep/delete review list
- Local-only cleanup planning

## Build

``powershell
dotnet build .\SimpleDuplicateFileFinder\SimpleDuplicateFileFinder.csproj -c Release
``

## Store notes

Before final packaging, reserve the exact Microsoft Store product name in Partner Center and update package identity values to match that reservation.