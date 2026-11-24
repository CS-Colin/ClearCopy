using System.IO;

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
}
