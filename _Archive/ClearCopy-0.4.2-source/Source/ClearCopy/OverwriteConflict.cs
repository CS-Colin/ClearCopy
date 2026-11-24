using System;

namespace ClearCopy;

// A confirmation covers this observation of this exact source/target pair only.
// It is deliberately never persisted or reused by another file or a resumed job.
public sealed class OverwriteConflict
{
	public string Source { get; }
	public string Target { get; }
	public DateTime SourceWriteUtc { get; }
	public DateTime TargetWriteUtc { get; }
	public long SourceSize { get; }
	public long TargetSize { get; }

	internal OverwriteConflict(Entry entry, Stamp source, Stamp target)
	{
		Source = entry.Source;
		Target = entry.Target;
		SourceWriteUtc = source.Write;
		TargetWriteUtc = target.Write;
		SourceSize = source.Length;
		TargetSize = target.Length;
	}
}
