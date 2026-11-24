namespace ClearCopy;

public sealed class Update
{
	public int ProtectedNewer;
	public int NotReached;
	public string Phase;

	public string Path;

	public long Bytes;

	public long TotalBytes;

	public long Transferred;

	public long WorkBytes;

	public long WorkTotal;

	public long FileBytes;

	public long FileTotal;

	public long ReadBytes;

	public long CopyLeft;

	public long VerifyLeft;

	public bool Copying;

	public bool Verifying;

	public int Done;

	public int Total;

	public int Folders;

	public int Matched;

	public int Missing;

	public int Mismatched;

	public int Errors;

	public int Copied;

	public int Skipped;

	public int Changed;

	public int Unchecked;

	public int MetadataDifferences;
}
