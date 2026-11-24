using System;
using System.IO;
using System.Xml.Serialization;

namespace ClearCopy;

public sealed class ItemResult
{
	// Status remains the legacy machine-readable report/job value.
	public string DisplayStatus => Status switch
	{
		"Copied" => "Copied — Not Verified",
		"Skipped" => "Policy Skipped — Not Verified",
		"Matched" => "SHA-256 Matched",
		"Mismatch" => "Content Mismatch",
		"Protected newer" => "Protected Newer Destination",
		"Changed" => "Changed During Operation",
		"Error" or "Unchecked" => "Failed / Inaccessible",
		"Not reached" => "Not Reached",
		_ => Status
	};

	public string VerificationState => Status switch
	{
		"Matched" => "SHA-256 Matched",
		"Mismatch" => "Content Mismatch",
		"Missing" => "Missing",
		"Changed" => "Changed During Operation",
		"Error" or "Unchecked" => "Failed / Inaccessible",
		"Not reached" => "Not Reached",
		_ => "Not Verified"
	};

	public string Name
	{
		get
		{
			try
			{
				return Path.GetFileName((Source ?? "").TrimEnd('\\'));
			}
			catch
			{
				return Source;
			}
		}
	}

	public string Source { get; set; }

	public string Target { get; set; }

	public string Status { get; set; }

	public string Reason { get; set; }

	public long Size { get; set; }

	public string Hash { get; set; }

	public string Backup { get; set; }

	public bool Directory { get; set; }

	// 0.4.3: Results tab wording. A Verified Copy reports a file twice (the
	// skip/copy decision, then the SHA-256 check); the window shows one row,
	// the check, with Prior holding the first decision. Reports and the CSV keep
	// Status/Reason/DisplayStatus unchanged.
	[XmlIgnore]
	public string Prior { get; set; }

	[XmlIgnore]
	public bool Hidden { get; set; }

	[XmlIgnore]
	public bool FastMode { get; set; }

	// 0.4.6: set when the row has been added to the Results grid.
	[XmlIgnore]
	public bool Shown { get; set; }

	[XmlIgnore]
	public string PriorCategory { get; set; }

	// 0.4.6: one kind per row for the whole-job breakdown and the "Show" filter.
	// Skips are split by why they were skipped.
	public string Category
	{
		get
		{
			string reason = Reason ?? "";
			switch (Status)
			{
			case "Skipped":
				if (reason.StartsWith("Completed checkpoint", StringComparison.Ordinal))
				{
					return "Skipped — already checked by an earlier run";
				}
				return reason.Contains("tolerance") ? "Skipped — already there, date within tolerance" : "Skipped — already there, same size and date";
			case "Protected newer":
				return "Kept — newer file on destination";
			case "Copied":
				return Directory ? "Folder created" : (Backup != null ? "Copied — replaced older file" : "Copied — new file");
			case "Matched":
				return "Verified identical (SHA-256)";
			case "Mismatch":
				return "Different contents";
			case "Missing":
				return "Missing on destination";
			case "Changed":
				return "Changed during the job";
			case "Not reached":
				return "Not reached";
			case "Unchecked":
				return reason.StartsWith("Scan failed", StringComparison.Ordinal) ? "Could not be read" : "Not checked";
			case "Error":
				return reason.StartsWith("Access denied", StringComparison.Ordinal) ? "Access denied" : "Failed";
			default:
				return Status ?? "";
			}
		}
	}

	public string GridStatus
	{
		get
		{
			string reason = Reason ?? "";
			switch (Status)
			{
			case "Matched":
				return Prior == "Skipped" ? "Existing file — verified" : (Prior == "Copied" ? "Copied — verified" : "Identical — verified");
			case "Mismatch":
				return Prior == "Skipped" ? "Existing file DIFFERS" : (Prior == "Copied" ? "Copied — DIFFERS" : "Contents differ");
			case "Copied":
				return Backup != null ? "Replaced older file" : (Directory ? "Folder created" : "Copied");
			case "Skipped":
				return "Existing file — skipped";
			case "Protected newer":
				return "Newer on destination — kept";
			case "Missing":
				return "Missing on destination";
			case "Changed":
				return "Changed during the job";
			case "Not reached":
				return "Not reached";
			case "Unchecked":
				return reason.StartsWith("Scan failed", StringComparison.Ordinal) ? "Could not be read" : "Not checked";
			case "Error":
				return reason.StartsWith("Access denied", StringComparison.Ordinal) ? "Access denied" : "Failed";
			default:
				return DisplayStatus;
			}
		}
	}

	private bool OnDestination(string reason)
	{
		try
		{
			return !string.IsNullOrEmpty(Target) && reason.IndexOf(LongPath.ForIO(Path.GetDirectoryName(Target)), StringComparison.OrdinalIgnoreCase) >= 0;
		}
		catch
		{
			return false;
		}
	}

	public string Explanation
	{
		get
		{
			string reason = Reason ?? "";
			string previous = Backup != null ? " The previous destination version was kept as " + Path.GetFileName(Backup) + "." : "";
			switch (Status)
			{
			case "Matched":
				if (reason.StartsWith("Repair recheck", StringComparison.Ordinal))
				{
					return "Already identical on the destination, so nothing was copied (SHA-256 checked).";
				}
				if (Prior == "Skipped")
				{
					return "Already on the destination with the same size and date, so it was not copied again. Both copies were read and are identical (SHA-256).";
				}
				if (Prior == "Copied")
				{
					return (reason.StartsWith("SHA-256 matched: destination read back", StringComparison.Ordinal) ? "Copied, then the destination copy was read back from the NAS/disk and is identical to the source (SHA-256)." : "Copied, then both copies were read and are identical (SHA-256).") + previous;
				}
				return "Both copies were read and are identical (SHA-256).";
			case "Mismatch":
				return (Prior == "Skipped" ? "Same size and date as the source, but the contents are different. " : (Prior == "Copied" ? "Copied, but the copy does not match the source. " : "The source and destination contents are different. ")) + "Use Repair to copy it again.";
			case "Copied":
				if (Directory)
				{
					return "Folder created on the destination.";
				}
				return (Backup != null ? "The destination had an older version; it was replaced." + previous : "New file copied to the destination.") + (FastMode ? " Contents not checked (Fast Copy)." : " Contents are checked next.");
			case "Skipped":
				if (reason.StartsWith("Completed checkpoint", StringComparison.Ordinal))
				{
					return "Already copied and checked by an earlier run (SHA-256 matched again).";
				}
				return "Already on the destination with the same size and date" + (reason.Contains("tolerance") ? " (dates within the allowed difference)" : "") + ", so it was not copied." + (FastMode ? " Contents not checked (Fast Copy)." : " Contents are checked next.");
			case "Protected newer":
				return "The destination copy is newer than the source, so it was left in place.";
			case "Missing":
				return "Not found on the destination.";
			case "Changed":
				return "The file changed while ClearCopy was working on it, so the result can't be trusted. Run the job again. (" + reason + ")";
			case "Not reached":
				return "The job stopped before this item.";
			case "Unchecked":
				if (reason.StartsWith("Scan failed", StringComparison.Ordinal))
				{
					return "Windows would not let ClearCopy open this during the scan. Use Fix access… if it is on the source. (" + reason.Substring(Math.Min(reason.Length, 13)) + ")";
				}
				return reason;
			case "Error":
				if (reason.StartsWith("Access denied", StringComparison.Ordinal))
				{
					return "Windows refused access" + (OnDestination(reason) ? " on the destination (check permissions or read-only files there)." : " on the source; use Fix access… to fix and retry.") + " (" + reason + ")";
				}
				return reason;
			default:
				return reason;
			}
		}
	}
}
