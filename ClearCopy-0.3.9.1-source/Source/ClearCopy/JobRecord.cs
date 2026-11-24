using System;
using System.Collections.Generic;

namespace ClearCopy;

public sealed class JobRecord
{
	public int Schema = 1;

	public string Id = Guid.NewGuid().ToString("N");

	public string Build = "0.3.9.1";

	// Absent in 0.3.5.4 jobs: derive from CompareOnly/Verify when loading.
	public OperationMode? Mode;
	public string[] CompareSources;
	public bool CompareContents;
	public bool RepairOnly;

	public string Destination;

	public string State = "Ready";

	public string Warning;

	public string[] Sources;

	public bool Contents;

	public bool Verify = true;

	public bool CompareOnly;

	public bool Details;

	public bool Permissions;

	public bool Sync;

	public ExistingRule Rule;

	public DateTime UpdatedUtc;

	public int SkipToleranceSeconds;

	public List<ItemResult> Completed = new List<ItemResult>();

	public List<ItemResult> Selection = new List<ItemResult>();

	// Point-in-time copy for background checkpoint writes. Rows are not modified
	// after they are queued, so copying the lists (not the rows) is sufficient.
	public JobRecord Snapshot()
	{
		JobRecord copy = (JobRecord)MemberwiseClone();
		copy.Completed = new List<ItemResult>(Completed);
		copy.Selection = new List<ItemResult>(Selection);
		copy.Sources = (string[])Sources?.Clone();
		copy.CompareSources = (string[])CompareSources?.Clone();
		return copy;
	}
}
