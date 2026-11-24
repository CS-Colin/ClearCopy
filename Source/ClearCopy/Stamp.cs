using System;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClearCopy;

public sealed class Stamp
{
	public bool Exists;

	public bool Directory;

	public long Length;

	public DateTime Write;

	public DateTime Created;

	public FileAttributes Attributes;

	public static Stamp Read(string path)
	{
		if (!TryReadData(path, out AttributeData data)) return new Stamp();
		bool directory = (data.Attributes & FileAttributes.Directory) != 0;
		return new Stamp
		{
			Exists = true,
			Directory = directory,
			Length = directory ? 0 : ((long)data.SizeHigh << 32) | data.SizeLow,
			Write = DateTime.FromFileTimeUtc(((long)data.WriteHigh << 32) | data.WriteLow),
			Created = DateTime.FromFileTimeUtc(((long)data.CreatedHigh << 32) | data.CreatedLow),
			Attributes = data.Attributes
		};
	}

	// No cross-file cache: every check observes the filesystem again.
	public static FileAttributes? ReadAttributes(string path)
	{
		return TryReadData(path, out AttributeData data) ? data.Attributes : (FileAttributes?)null;
	}

	private static bool TryReadData(string path, out AttributeData data)
	{
		if (GetFileAttributesEx(LongPath.ForIO(path), 0, out data)) return true;
		int error = Marshal.GetLastWin32Error();
		if (error == 2 || error == 3) return false;
		string message = new Win32Exception(error).Message + ": " + path;
		if (error == 5) throw new UnauthorizedAccessException(message);
		if (error == 206) throw new PathTooLongException(message);
		throw new IOException(message, unchecked((int)0x80070000) | error);
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct AttributeData
	{
		public FileAttributes Attributes;
		public uint CreatedLow, CreatedHigh;
		public uint AccessLow, AccessHigh;
		public uint WriteLow, WriteHigh;
		public uint SizeHigh, SizeLow;
	}

	[DllImport("kernel32.dll", EntryPoint = "GetFileAttributesExW", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetFileAttributesEx(string path, int infoLevel, out AttributeData data);

	public bool Same(Stamp other)
	{
		if (Exists == other.Exists)
		{
			if (Exists)
			{
				if (Directory == other.Directory && Length == other.Length && Write == other.Write)
				{
					return Created == other.Created;
				}
				return false;
			}
			return true;
		}
		return false;
	}
}
