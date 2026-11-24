using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace ClearCopy;

public static class Jobs
{
	public static OperationMode GetMode(JobRecord job)
	{
		return job.Mode ?? (job.CompareOnly ? OperationMode.Compare : (job.Verify ? OperationMode.VerifiedCopy : OperationMode.FastCopy));
	}

	public static bool IsContentIssue(ItemResult row)
	{
		return !row.Directory && (row.Status == "Missing" || row.Status == "Mismatch");
	}

	public static ItemResult[] RepairableRows(IEnumerable<ItemResult> rows)
	{
		// Metadata is a separate dimension: its difference row must not hide a
		// confirmed content mismatch. Later changed/error/matched states do win.
		return rows.GroupBy(r => new { Source = (r.Source ?? "").ToUpperInvariant(), Target = (r.Target ?? "").ToUpperInvariant() })
			.Select(g => g.LastOrDefault(r => r.Status != "Metadata difference")).Where(r => r != null && IsContentIssue(r)).ToArray();
	}
	public static bool IsIssue(ItemResult row)
	{
		if (row.Status != "Matched" && row.Status != "Copied")
		{
			return row.Status != "Skipped";
		}
		return false;
	}

	public static List<Entry> DifferenceEntries(IEnumerable<Entry> plan, IEnumerable<ItemResult> rows)
	{
		ILookup<string, ItemResult> issues = RepairableRows(rows).ToLookup((ItemResult r) => r.Target, StringComparer.OrdinalIgnoreCase);
		return plan.Where((Entry e) => issues[e.Target].Any((ItemResult r) => string.Equals(r.Source, e.Source, StringComparison.OrdinalIgnoreCase))).ToList();
	}

	public static void WriteReport(string logFile, JobRecord job, string summary, string outcome, bool resultsStreamed = false)
	{
		if (string.IsNullOrEmpty(logFile))
		{
			throw new IOException("No log path is available.");
		}
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logFile)));
		ItemResult[] array = job.Completed.Where((ItemResult r) => r.Status != "Matched" && r.Status != "Copied" && r.Status != "Skipped").ToArray();
		// 0.4: the window streams every row to results.csv while the job runs;
		// job.Completed then only holds issues plus the first ordinary rows.
		if (!resultsStreamed)
		{
			Export(logFile + ".results.csv", job.Completed);
		}
		Export(logFile + ".issues.csv", array);
		using StreamWriter streamWriter = new StreamWriter(logFile + ".summary.txt", append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
		streamWriter.WriteLine("ClearCopy " + job.Build + " — " + job.State);
		streamWriter.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
		StreamWriter streamWriter2 = streamWriter;
		string value;
		if (job.Sync)
		{
			value = "REPAIR CONTENT DIFFERENCES + FINAL COMPARE";
		}
		else
		{
			value = (job.CompareOnly ? "COMPARE ONLY" : "COPY");
		}
		streamWriter2.WriteLine(value);
		streamWriter.WriteLine("Sources: " + string.Join("; ", job.Sources));
		streamWriter.WriteLine("Destination: " + job.Destination);
		streamWriter.WriteLine("Mode: " + GetMode(job) + "; only Matched rows certify SHA-256 equality. Copied/Skipped rows are not verification.");
		streamWriter.WriteLine("Mapping: " + (job.Contents ? "folder contents directly" : "selected names inside destination"));
		streamWriter.WriteLine(string.Concat("Policy: ", job.Rule, (job.SkipToleranceSeconds > 0) ? (" (skip-same treats modified times within " + job.SkipToleranceSeconds + " s as equal; change detection and SHA-256 are exact)") : " (exact modified-time matching)"));
		if (job.Sync)
		{
			streamWriter.WriteLine("Repair selects confirmed missing/content-mismatched files only and rechecks them. Newer destinations require per-file confirmation. Counts and issues below describe the final comparison; separate .sync-copy reports retain repair attempts and unresolved conflicts.");
		}
		streamWriter.WriteLine(summary);
		streamWriter.WriteLine(outcome);
		streamWriter.WriteLine("Paths with reported issues: " + array.Select((ItemResult r) => r.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count());
		streamWriter.WriteLine("Counts describe operations/checks; one path may have multiple result rows. For cancelled/stopped jobs, unprocessed paths are not certified.");
		ItemResult[] array2 = array;
		foreach (ItemResult itemResult in array2)
		{
			streamWriter.WriteLine();
			streamWriter.WriteLine(itemResult.Status + ": " + itemResult.Source + " -> " + itemResult.Target);
			streamWriter.WriteLine(itemResult.Reason);
		}
		streamWriter.WriteLine();
		streamWriter.WriteLine("All results: " + logFile + ".results.csv");
		streamWriter.WriteLine("Issues: " + logFile + ".issues.csv");
		streamWriter.WriteLine("Activity log: " + logFile);
	}

	public static void Save(string file, JobRecord job)
	{
		job.UpdatedUtc = DateTime.UtcNow;
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
		string text = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				new XmlSerializer(typeof(JobRecord)).Serialize(fileStream, job);
				fileStream.Flush(flushToDisk: true);
			}
			if (File.Exists(file))
			{
				File.Replace(text, file, null);
			}
			else
			{
				File.Move(text, file);
			}
		}
		finally
		{
			if (File.Exists(text))
			{
				File.Delete(text);
			}
		}
	}

	public static JobRecord Load(string file)
	{
		using XmlReader xmlReader = XmlReader.Create(file, new XmlReaderSettings
		{
			DtdProcessing = DtdProcessing.Prohibit,
			XmlResolver = null
		});
		JobRecord jobRecord = (JobRecord)new XmlSerializer(typeof(JobRecord)).Deserialize(xmlReader);
		if (jobRecord.Schema != 1 || jobRecord.Sources == null || jobRecord.Sources.Length == 0 || string.IsNullOrWhiteSpace(jobRecord.Destination))
		{
			throw new IOException("Unsupported or incomplete saved job.");
		}
		jobRecord.Selection = jobRecord.Selection ?? new List<ItemResult>();
		if (jobRecord.Mode.HasValue && !Enum.IsDefined(typeof(OperationMode), jobRecord.Mode.Value)) throw new IOException("Unsupported operating mode.");
		jobRecord.Mode = GetMode(jobRecord);
		jobRecord.CompareOnly = jobRecord.Mode == OperationMode.Compare;
		jobRecord.Verify = jobRecord.Mode != OperationMode.FastCopy;
		jobRecord.Completed = jobRecord.Completed ?? new List<ItemResult>();
		if (jobRecord.Selection.Any((ItemResult r) => string.IsNullOrWhiteSpace(r.Source) || string.IsNullOrWhiteSpace(r.Target) || !Path.IsPathRooted(r.Source) || !Path.IsPathRooted(r.Target)))
		{
			throw new IOException("Saved selected paths must use absolute source and destination paths.");
		}
		return jobRecord;
	}

	public static Dictionary<string, string> Hashes(JobRecord job)
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (ItemResult item in job.Completed.Where((ItemResult i) => i.Status == "Matched" && !string.IsNullOrEmpty(i.Hash)))
		{
			dictionary[item.Target] = item.Hash;
		}
		return dictionary;
	}

	public static string Explain(Result r, bool compareOnly)
	{
		List<string> list = new List<string>();
		if (r.PlannedFiles == 0)
		{
			if (r.ScanFailures > 0 && r.PlannedFolders == 0)
			{
				list.Add($"No files were processed: {r.ScanFailures} selected path(s) could not be scanned. See Issues for the reasons.");
			}
			else if (compareOnly)
			{
				list.Add($"Nothing to compare: the selection contained no files, only {r.PlannedFolders} folder(s). {r.FoldersPresent} present at the destination, {r.Missing} missing.");
			}
			else
			{
				list.Add($"Nothing to copy: the selection contained no files, only {r.PlannedFolders} folder(s). {r.FoldersCreated} created, {r.FoldersPresent} already present.");
			}
			if (r.ScanFailures > 0 && r.PlannedFolders > 0)
			{
				list.Add($"{r.ScanFailures} path(s) inside the selection could not be scanned; anything inside them was not processed.");
			}
			return string.Join(Environment.NewLine, list);
		}
		list.Add($"Selection: {r.PlannedFiles} file(s) in {r.PlannedFolders} folder(s)." + ((!compareOnly && r.FoldersCreated > 0) ? $" {r.FoldersCreated} folder(s) created." : ""));
		if (!compareOnly && r.Copied == 0 && r.Skipped > 0 && r.Skipped >= r.PlannedFiles)
		{
			list.Add($"All {r.PlannedFiles} file(s) were skipped by the overwrite policy or a revalidated checkpoint; no file contents were copied. Selected metadata may have been applied.");
		}
		else if (!compareOnly && r.Skipped > 0)
		{
			list.Add($"{r.Skipped} file(s) skipped by the overwrite policy or a revalidated checkpoint.");
		}
		if (r.ScanFailures > 0)
		{
			list.Add($"{r.ScanFailures} path(s) could not be scanned; anything inside them was not processed.");
		}
		if (r.Errors + r.Unchecked > 0)
		{
			list.Add($"{r.Errors} error(s), {r.Unchecked} unchecked result(s): see Issues for each path and reason.");
		}
		if (r.FilesProcessed < r.PlannedFiles)
		{
			list.Add($"{r.PlannedFiles - r.FilesProcessed} of {r.PlannedFiles} file(s) were not reached and are not certified.");
		}
		return string.Join(Environment.NewLine, list);
	}

	public static string ExplainStopped(Update last)
	{
		if (last == null)
		{
			return "Stopped before scanning reported progress; no files were processed.";
		}
		if (last.Phase == "Scanning")
		{
			return $"Stopped during scanning ({last.Total:N0} file(s) found so far); no files were copied or compared.";
		}
		if (last.Total <= 0)
		{
			return "Stopped before any file was processed.";
		}
		int num = Math.Max(0, last.Total - last.Done);
		return $"{last.Done:N0} of {last.Total:N0} file(s) were processed; {num:N0} were not reached and are not certified.";
	}

	private static string Cell(string value)
	{
		return "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
	}

	public static void Export(string path, IEnumerable<ItemResult> rows)
	{
		using StreamWriter streamWriter = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
		streamWriter.WriteLine(CsvHeader);
		foreach (ItemResult row in rows)
		{
			streamWriter.WriteLine(CsvLine(row));
		}
	}

	public const string CsvHeader = "Status,Source,Destination,Bytes,Reason,SHA256,PreviousVersion,DisplayStatus,VerificationState";

	public static string CsvLine(ItemResult row)
	{
		return string.Join(",", Cell(row.Status), Cell(row.Source), Cell(row.Target), row.Size.ToString(CultureInfo.InvariantCulture), Cell(row.Reason), Cell(row.Hash), Cell(row.Backup), Cell(row.DisplayStatus), Cell(row.VerificationState));
	}
}
