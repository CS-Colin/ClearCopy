namespace ClearCopy;

public sealed class Entry
{
	public string Source;

	public string Target;

	public string ScanError;

	public bool Directory;

	public long Length;

	public Stamp SourceState;

	public Stamp VerifiedSource;

	public Stamp VerifiedTarget;
}
