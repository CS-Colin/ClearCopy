using System;

namespace ClearCopy;

public sealed class PasteTarget
{
	public IntPtr Window;

	public string Folder;

	public string[] Paths;

	public uint Sequence;
}
