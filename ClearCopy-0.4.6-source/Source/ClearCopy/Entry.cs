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

	// Set by the final source re-check when this entry is found again.
	public bool SeenInAudit;

	// 0.4.1: the scan was refused access (offered to "Fix access").
	public bool AccessDenied;

	// 0.4.5: SHA-256 of the bytes read from the source while copying, and the
	// source's size/date at that moment. Lets the check read only the destination.
	public string CopyHash;

	public Stamp CopyStamp;
}
