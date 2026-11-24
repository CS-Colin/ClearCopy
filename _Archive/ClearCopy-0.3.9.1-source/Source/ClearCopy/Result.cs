using System.Collections.Generic;

namespace ClearCopy;

public sealed class Result
{
	public int Copied;

	public int Skipped;

	public int ProtectedNewer;

	public int NotReached;

	public int Errors;

	public int Matched;

	public int Missing;

	public int Mismatched;

	public int Changed;

	public int Unchecked;

	public int MetadataDifferences;

	public int PlannedFiles;

	public int PlannedFolders;

	public int ScanFailures;

	public int FoldersCreated;

	public int FoldersPresent;

	public int FilesProcessed;

	public bool LogIncomplete;

	public bool TreeRechecked;

	public List<ItemResult> Items = new List<ItemResult>();

	public bool Clean
	{
		get
		{
			if (!LogIncomplete)
			{
				return Errors + Missing + Mismatched + Changed + Unchecked + MetadataDifferences + ProtectedNewer + NotReached == 0;
			}
			return false;
		}
	}
}
