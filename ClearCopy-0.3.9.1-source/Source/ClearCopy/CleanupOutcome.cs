using System.Collections.Generic;

namespace ClearCopy;

public sealed class CleanupOutcome
{
	public int Removed;

	public int Skipped;

	public long BytesRemoved;

	public readonly List<string> Lines = new List<string>();
}
