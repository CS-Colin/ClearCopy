using System;
using System.IO;

namespace ClearCopy;

// Keep normal paths in plans and logs. Expand only at the Windows I/O boundary.
public static class LongPath
{
    public static string ForIO(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("A file path is required.", nameof(path));
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal)) throw new IOException("Device paths are not supported.");
        string full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full.Substring(2)
            : @"\\?\" + full;
    }

    public static string ForDisplay(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path.Substring(8);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path.Substring(4);
        return path;
    }
}
