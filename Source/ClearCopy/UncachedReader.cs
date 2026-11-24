using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClearCopy;

// 0.4.5: reads a file past the Windows file cache (FILE_FLAG_NO_BUFFERING), so a
// verification read of the destination comes from the NAS/disk, not from memory.
// Opened with read sharing only, so nobody can write to the file while it is read.
public sealed class UncachedReader : Stream
{
	private const int BufferSize = 1048576;

	private readonly SafeFileHandle handle;

	private IntPtr buffer;

	private int bufferLength;

	private int bufferPosition;

	private bool endOfFile;

	private long position;

	private readonly long length;

	public UncachedReader(string path)
	{
		handle = CreateFile(LongPath.ForIO(path), 0x80000000u, 1u, IntPtr.Zero, 3u, 0x20000000u | 0x08000000u, IntPtr.Zero);
		if (handle.IsInvalid)
		{
			throw Error(Marshal.GetLastWin32Error());
		}
		if (!GetFileSizeEx(handle, out length))
		{
			int error = Marshal.GetLastWin32Error();
			handle.Dispose();
			throw Error(error);
		}
		// VirtualAlloc returns page-aligned memory, which satisfies the sector
		// alignment that unbuffered reads require.
		buffer = VirtualAlloc(IntPtr.Zero, (UIntPtr)BufferSize, 0x3000u, 4u);
		if (buffer == IntPtr.Zero)
		{
			handle.Dispose();
			throw new OutOfMemoryException("Could not allocate the verification buffer.");
		}
	}

	public static bool Supported => Environment.OSVersion.Platform == PlatformID.Win32NT;

	private static Exception Error(int error)
	{
		return Marshal.GetExceptionForHR(unchecked((int)0x80070000) | error) ?? new IOException("Windows error " + error);
	}

	public override bool CanRead => true;

	public override bool CanSeek => false;

	public override bool CanWrite => false;

	public override long Length => length;

	public override long Position
	{
		get
		{
			return position;
		}
		set
		{
			throw new NotSupportedException();
		}
	}

	public override int Read(byte[] array, int offset, int count)
	{
		if (bufferPosition >= bufferLength)
		{
			if (endOfFile)
			{
				return 0;
			}
			if (!ReadFile(handle, buffer, BufferSize, out var read, IntPtr.Zero))
			{
				throw Error(Marshal.GetLastWin32Error());
			}
			bufferLength = (int)read;
			bufferPosition = 0;
			// A short read means the end of the file. The next unbuffered read would
			// start at an unaligned offset, so stop here.
			if (read < BufferSize)
			{
				endOfFile = true;
			}
			if (read == 0)
			{
				return 0;
			}
		}
		int n = Math.Min(count, bufferLength - bufferPosition);
		Marshal.Copy(buffer + bufferPosition, array, offset, n);
		bufferPosition += n;
		position += n;
		return n;
	}

	public override void Flush()
	{
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		throw new NotSupportedException();
	}

	public override void SetLength(long value)
	{
		throw new NotSupportedException();
	}

	public override void Write(byte[] array, int offset, int count)
	{
		throw new NotSupportedException();
	}

	protected override void Dispose(bool disposing)
	{
		handle.Dispose();
		if (buffer != IntPtr.Zero)
		{
			VirtualFree(buffer, UIntPtr.Zero, 0x8000u);
			buffer = IntPtr.Zero;
		}
		base.Dispose(disposing);
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, int count, out uint read, IntPtr overlapped);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetFileSizeEx(SafeFileHandle file, out long size);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);
}
