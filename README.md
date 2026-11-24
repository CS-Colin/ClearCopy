# ClearCopy

ClearCopy is a Windows file copy and comparison utility. Use it to copy files quickly, verify a transfer with SHA-256, or compare two locations before changing anything.

**[Download the latest release](https://github.com/CS-Colin/ClearCopy/releases/latest)** · [Browse the C# source](Source/ClearCopy)

## Credits

ClearCopy was originally created by [Alucarddp](https://github.com/Alucarddp), who also contributed the [Explorer paste preference feature](https://github.com/CS-Colin/ClearCopy/commit/8238e0f8) in v0.4.8.

## Get started

1. Download the `ClearCopy-<version>.exe` and its matching `.exe.config` from **Assets** on the release page. Keep both files in the same folder with their original names.
2. Open ClearCopy. Use **Add files…** or **Add folder…** to select what to copy or compare, then choose a destination.
3. Choose a mode, start the job, and review the results.

ClearCopy targets **64-bit Windows** and requires **.NET Framework 4.8**. Releases include a `SHA256SUMS.txt` file if you want to verify the downloads.

## Copy and compare modes

| Mode | When to use it |
| --- | --- |
| **Fast Copy** | Copy files without verifying their contents afterward. |
| **Verified Copy** | Check file contents with SHA-256 after copying or when a file is skipped under the selected copy rule. |
| **Compare** | Check for missing or different files without changing the destination. |

For a transfer where verification matters, choose **Verified Copy**. To see how two locations differ before copying, start with **Compare**.

## Check for updates

Click **Check for updates** in ClearCopy to compare your running version with the latest published GitHub release. If a newer build is available, ClearCopy can open the release page so you can download it manually.

## Build from source

On Windows, install the **.NET 10 SDK** and run this from the repository root in PowerShell:

```powershell
./Source/Build.ps1
```

The build writes `ClearCopy.exe` to `Source/bin/Release/net48/`.

| Folder or file | What you'll find |
| --- | --- |
| [Source/ClearCopy](Source/ClearCopy) | Current C# source |
| [Program.cs](Source/ClearCopy/Program.cs) | Application entry point |
| [MainWindow.cs](Source/ClearCopy/MainWindow.cs) | Main window and job controls |
| [CopyEngine.cs](Source/ClearCopy/CopyEngine.cs) | Copy, comparison, and verification logic |
| [Tests](Tests) | Test harnesses and fixtures |
| [\_Archive](_Archive) | Older source snapshots |
