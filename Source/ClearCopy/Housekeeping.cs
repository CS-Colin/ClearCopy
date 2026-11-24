using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ClearCopy;

public static class Housekeeping
{
	private const string LockHeader = "ClearCopy job lock v1";

	public static readonly TimeSpan RecentLimit = TimeSpan.FromHours(1.0);

	public static readonly TimeSpan PartialRecentLimit = TimeSpan.FromHours(24.0);

	private static readonly Regex BackupName = new Regex("^\\.clearcopy-replaced-[0-9a-f]{32}\\.bak$", RegexOptions.IgnoreCase);

	private static readonly Regex PartialName = new Regex("^\\.clearcopy-[0-9a-f]{32}\\.partial$", RegexOptions.IgnoreCase);

	public static bool IsBackupName(string name)
	{
		return BackupName.IsMatch(name ?? "");
	}

	public static bool IsPartialName(string name)
	{
		return PartialName.IsMatch(name ?? "");
	}

	public static string Size(long bytes)
	{
		double num = bytes;
		string[] array = new string[5] { "B", "KiB", "MiB", "GiB", "TiB" };
		int num2 = 0;
		while (num >= 1024.0 && num2 < array.Length - 1)
		{
			num /= 1024.0;
			num2++;
		}
		return num.ToString((num2 == 0) ? "0" : "0.0") + " " + array[num2];
	}

	public static IDisposable HoldJobLock(string jobFile, string destination)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jobFile)));
		FileStream fileStream = new FileStream(jobFile + ".active", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
		try
		{
			fileStream.SetLength(0L);
			byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes("ClearCopy job lock v1\n" + Path.GetFullPath(destination) + "\n");
			fileStream.Write(bytes, 0, bytes.Length);
			fileStream.Flush(flushToDisk: true);
			return fileStream;
		}
		catch
		{
			fileStream.Dispose();
			throw;
		}
	}

	public static IDisposable AcquireJobLock(string jobFile, string destination, TimeSpan wait, CancellationToken token)
	{
		IDisposable disposable = HoldJobLock(jobFile, destination);
		try
		{
			DateTime dateTime = DateTime.UtcNow + wait;
			string directoryName = Path.GetDirectoryName(Path.GetFullPath(jobFile));
			while (CleanupRunning(directoryName))
			{
				if (DateTime.UtcNow >= dateTime)
				{
					throw new IOException("Housekeeping is removing files, so this job was not started. Start it again when housekeeping has finished.");
				}
				if (token.WaitHandle.WaitOne(250))
				{
					token.ThrowIfCancellationRequested();
				}
			}
			return disposable;
		}
		catch
		{
			disposable.Dispose();
			throw;
		}
	}

	private static bool Held(string lockFile)
	{
		if (File.Exists(lockFile))
		{
			return LockedAgainstExclusive(lockFile);
		}
		return false;
	}

	private static bool LockedAgainstExclusive(string lockFile)
	{
		try
		{
			using (new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.None))
			{
				return false;
			}
		}
		catch (FileNotFoundException)
		{
			return false;
		}
		catch (DirectoryNotFoundException)
		{
			return false;
		}
		catch
		{
			return true;
		}
	}

	public static bool LockHeld(string jobFile)
	{
		return Held(jobFile + ".active");
	}

	private static string LockDestination(string lockFile)
	{
		try
		{
			using FileStream stream = new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using StreamReader streamReader = new StreamReader(stream, Encoding.UTF8);
			if (streamReader.ReadLine() != "ClearCopy job lock v1")
			{
				return null;
			}
			string text = streamReader.ReadLine();
			return (string.IsNullOrWhiteSpace(text) || !Path.IsPathRooted(text)) ? null : text;
		}
		catch
		{
			return null;
		}
	}

	public static IDisposable HoldCleanupLock(string jobsDirectory)
	{
		Directory.CreateDirectory(jobsDirectory);
		try
		{
			return new FileStream(Path.Combine(jobsDirectory, "housekeeping.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
		}
		catch (IOException)
		{
			throw new IOException("Another housekeeping removal is already in progress.");
		}
	}

	public static bool CleanupRunning(string jobsDirectory)
	{
		string text = Path.Combine(jobsDirectory, "housekeeping.lock");
		if (File.Exists(text))
		{
			return LockedAgainstExclusive(text);
		}
		return false;
	}

	private static JobRecord TryLoad(string file)
	{
		try
		{
			return Jobs.Load(file);
		}
		catch
		{
			return null;
		}
	}

	private static bool PreLockBuild(string build)
	{
		if (Version.TryParse(build ?? "", out var result))
		{
			return result < new Version(0, 3, 5);
		}
		return true;
	}

	public static List<ActiveJob> ActiveJobs(string jobsDirectory)
	{
		List<ActiveJob> list = new List<ActiveJob>();
		if (string.IsNullOrEmpty(jobsDirectory) || !Directory.Exists(jobsDirectory))
		{
			return list;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string item in Files(jobsDirectory, "*.clearcopy.xml.active", ".clearcopy.xml.active"))
		{
			if (Held(item))
			{
				string text = item.Substring(0, item.Length - ".active".Length);
				hashSet.Add(text);
				string text2 = LockDestination(item);
				if (text2 == null)
				{
					text2 = TryLoad(text)?.Destination;
				}
				list.Add((text2 != null) ? new ActiveJob
				{
					Id = Path.GetFileName(text),
					Destination = text2,
					Reason = "active job (lock held)"
				} : new ActiveJob
				{
					Id = Path.GetFileName(text),
					Unknown = true,
					Reason = "active job whose destination could not be read from its lock or job record"
				});
			}
		}
		foreach (string item2 in Files(jobsDirectory, "*.clearcopy.xml", ".clearcopy.xml"))
		{
			if (!hashSet.Contains(item2))
			{
				JobRecord jobRecord = TryLoad(item2);
				if (jobRecord == null)
				{
					list.Add(new ActiveJob
					{
						Id = Path.GetFileName(item2),
						Unknown = true,
						Reason = "job record that cannot be read (" + Path.GetFileName(item2) + "); it may belong to a running job. Remove or repair that record to re-enable cleanup"
					});
				}
				else if (jobRecord.State == "Running" && PreLockBuild(jobRecord.Build))
				{
					list.Add(new ActiveJob
					{
						Id = Path.GetFileName(item2),
						Destination = jobRecord.Destination,
						Reason = "job record says Running and was written by ClearCopy " + jobRecord.Build + ", which cannot prove it has stopped"
					});
				}
			}
		}
		return list;
	}

	public static ActiveJob Blocking(IList<ActiveJob> active, string path)
	{
		return active.FirstOrDefault((ActiveJob a) => a.Unknown) ?? active.FirstOrDefault((ActiveJob a) => Inside(path, a.Destination));
	}

	private static IEnumerable<string> Files(string folder, string pattern, string suffix)
	{
		return from f in Directory.GetFiles(folder, pattern)
			where f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
			select f;
	}

	private static string Full(string path)
	{
		return Path.GetFullPath(path).TrimEnd('\\') + "\\";
	}

	public static bool Inside(string path, string folder)
	{
		try
		{
			return !string.IsNullOrEmpty(folder) && Full(path).StartsWith(Full(folder), StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	private static bool InUse(string path)
	{
		try
		{
			using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
			{
				return false;
			}
		}
		catch (FileNotFoundException)
		{
			return false;
		}
		catch (DirectoryNotFoundException)
		{
			return false;
		}
		catch
		{
			return true;
		}
	}

	private static CleanupFile Snapshot(string path)
	{
		FileInfo fileInfo = new FileInfo(path);
		CleanupFile cleanupFile = new CleanupFile();
		cleanupFile.Path = fileInfo.FullName;
		cleanupFile.Length = fileInfo.Length;
		cleanupFile.WriteUtc = fileInfo.LastWriteTimeUtc;
		return cleanupFile;
	}

	// Automatic retention (0.3.9.2): keep the newest `keep` job log sets (a .log
	// plus its .results.csv/.issues.csv/.summary.txt/... companions), the newest
	// `keep` diagnostics reports, and paste-request lists for `pasteDays` days.
	// Anything modified within RecentLimit is never touched; saved jobs are never
	// touched. Deletion is permanent. Returns a one-line summary, or null.
	public static string ApplyRetention(string logsDirectory, string pasteDirectory, int keep, int pasteDays, DateTime nowUtc)
	{
		if (keep <= 0)
		{
			return null;
		}
		int sets = 0;
		int files = 0;
		long bytes = 0L;
		int failed = 0;
		Action<string> remove = (string path) =>
		{
			try
			{
				FileInfo info = new FileInfo(path);
				if (!info.Exists || nowUtc - info.LastWriteTimeUtc < RecentLimit)
				{
					return;
				}
				long length = info.Length;
				info.Delete();
				files++;
				bytes += length;
			}
			catch
			{
				failed++;
			}
		};
		if (!string.IsNullOrEmpty(logsDirectory) && Directory.Exists(logsDirectory))
		{
			List<FileInfo> all = new DirectoryInfo(logsDirectory).GetFiles().ToList();
			List<FileInfo> logs = all.Where((FileInfo f) => f.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)).OrderByDescending((FileInfo f) => f.LastWriteTimeUtc).ToList();
			foreach (FileInfo log in logs.Skip(keep))
			{
				List<FileInfo> set = all.Where((FileInfo f) => f.Name.Equals(log.Name, StringComparison.OrdinalIgnoreCase) || f.Name.StartsWith(log.Name + ".", StringComparison.OrdinalIgnoreCase)).ToList();
				if (set.Any((FileInfo f) => nowUtc - f.LastWriteTimeUtc < RecentLimit))
				{
					continue;
				}
				int before = files;
				foreach (FileInfo f in set)
				{
					remove(f.FullName);
				}
				if (files > before)
				{
					sets++;
				}
			}
			foreach (string prefix in new[] { "diagnostics-", "crash-" })
			{
				foreach (FileInfo report in all.Where((FileInfo f) => f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && f.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).OrderByDescending((FileInfo f) => f.LastWriteTimeUtc).Skip(keep))
				{
					remove(report.FullName);
				}
			}
		}
		int pasteRemoved = 0;
		if (!string.IsNullOrEmpty(pasteDirectory) && Directory.Exists(pasteDirectory) && pasteDays > 0)
		{
			foreach (FileInfo request in new DirectoryInfo(pasteDirectory).GetFiles("*.txt").Where((FileInfo f) => nowUtc - f.LastWriteTimeUtc > TimeSpan.FromDays(pasteDays)))
			{
				int before = files;
				remove(request.FullName);
				if (files > before)
				{
					pasteRemoved++;
				}
			}
		}
		if (files == 0 && failed == 0)
		{
			return null;
		}
		return "LOG RETENTION removed " + sets + " old log set(s) and " + pasteRemoved + " old paste request(s): " + files + " file(s), " + Size(bytes) + " (keeping the newest " + keep + " logs)" + ((failed > 0) ? ("; " + failed + " file(s) could not be removed") : "");
	}

	public static List<CleanupCandidate> ScanAppData(string appRoot, DateTime nowUtc)
	{
		List<CleanupCandidate> list = new List<CleanupCandidate>();
		string text = Path.Combine(appRoot, "PasteRequests");
		string text2 = Path.Combine(appRoot, "Logs");
		string text3 = Path.Combine(appRoot, "Jobs");
		List<ActiveJob> source = ActiveJobs(text3);
		if (Directory.Exists(text))
		{
			foreach (string item in Files(text, "*.txt", ".txt"))
			{
				CleanupCandidate cleanupCandidate = new CleanupCandidate();
				cleanupCandidate.Kind = CleanupKind.PasteRequest;
				cleanupCandidate.Path = item;
				cleanupCandidate.Detail = "Source list handed from the Ctrl+V chooser to a copy window";
				CleanupCandidate cleanupCandidate2 = cleanupCandidate;
				cleanupCandidate2.Files.Add(Snapshot(item));
				Finish(cleanupCandidate2, nowUtc, RecentLimit);
				list.Add(cleanupCandidate2);
			}
		}
		if (Directory.Exists(text2))
		{
			List<string> source2 = Directory.GetFiles(text2).Select(Path.GetFileName).ToList();
			foreach (string log in source2.Where((string n) => n.EndsWith(".log", StringComparison.OrdinalIgnoreCase)))
			{
				CleanupCandidate cleanupCandidate3 = new CleanupCandidate();
				cleanupCandidate3.Kind = CleanupKind.LogSet;
				cleanupCandidate3.Path = Path.Combine(text2, log);
				cleanupCandidate3.Detail = "Activity log with its summary/results/issues reports";
				CleanupCandidate cleanupCandidate4 = cleanupCandidate3;
				Func<string, bool> predicate = (string n) => n.Equals(log, StringComparison.OrdinalIgnoreCase) || n.StartsWith(log + ".", StringComparison.OrdinalIgnoreCase);
				foreach (string item2 in source2.Where(predicate))
				{
					cleanupCandidate4.Files.Add(Snapshot(Path.Combine(text2, item2)));
				}
				Finish(cleanupCandidate4, nowUtc, RecentLimit);
				list.Add(cleanupCandidate4);
			}
			foreach (string item3 in source2.Where((string n) => n.StartsWith("diagnostics-", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
			{
				CleanupCandidate cleanupCandidate5 = new CleanupCandidate();
				cleanupCandidate5.Kind = CleanupKind.LogSet;
				cleanupCandidate5.Path = Path.Combine(text2, item3);
				cleanupCandidate5.Detail = "Filesystem diagnostics report";
				CleanupCandidate cleanupCandidate6 = cleanupCandidate5;
				cleanupCandidate6.Files.Add(Snapshot(cleanupCandidate6.Path));
				Finish(cleanupCandidate6, nowUtc, RecentLimit);
				list.Add(cleanupCandidate6);
			}
		}
		if (Directory.Exists(text3))
		{
			foreach (string file in Files(text3, "*.clearcopy.xml", ".clearcopy.xml"))
			{
				JobRecord jobRecord = TryLoad(file);
				CleanupCandidate cleanupCandidate7 = new CleanupCandidate();
				cleanupCandidate7.Kind = CleanupKind.JobRecord;
				cleanupCandidate7.Path = file;
				cleanupCandidate7.JobDestination = jobRecord?.Destination;
				cleanupCandidate7.Detail = ((jobRecord == null) ? "Unreadable job record" : ("Saved " + jobRecord.State + " job → " + jobRecord.Destination + ". Deleting it removes the option to resume this job."));
				CleanupCandidate cleanupCandidate8 = cleanupCandidate7;
				cleanupCandidate8.Files.Add(Snapshot(file));
				ActiveJob activeJob = source.FirstOrDefault((ActiveJob a) => string.Equals(a.Id, Path.GetFileName(file), StringComparison.OrdinalIgnoreCase));
				if (LockHeld(file) || (activeJob != null && (!activeJob.Unknown || jobRecord != null)))
				{
					cleanupCandidate8.Excluded = "Excluded: " + ((activeJob != null) ? activeJob.Reason : "active job (lock held)");
				}
				Finish(cleanupCandidate8, nowUtc, RecentLimit);
				list.Add(cleanupCandidate8);
			}
		}
		return list;
	}

	public static List<CleanupCandidate> ScanDestination(string folder, IList<ActiveJob> active, IDictionary<string, string> backupOrigins, DateTime nowUtc, CancellationToken token)
	{
		List<CleanupCandidate> list = new List<CleanupCandidate>();
		folder = Path.GetFullPath(folder);
		CopyEngine.SafePath(folder);
		Stack<string> stack = new Stack<string>();
		stack.Push(folder);
		while (stack.Count > 0)
		{
			token.ThrowIfCancellationRequested();
			string path = stack.Pop();
			string[] fileSystemEntries;
			try
			{
				fileSystemEntries = Directory.GetFileSystemEntries(path);
			}
			catch (Exception ex)
			{
				list.Add(new CleanupCandidate
				{
					Kind = CleanupKind.Unreadable,
					Path = path,
					Excluded = "Not scanned: folder could not be read (" + ex.Message + ")"
				});
				continue;
			}
			string[] array = fileSystemEntries;
			foreach (string text in array)
			{
				FileAttributes attributes;
				try
				{
					attributes = File.GetAttributes(text);
				}
				catch
				{
					continue;
				}
				if ((attributes & FileAttributes.ReparsePoint) != 0)
				{
					continue;
				}
				if ((attributes & FileAttributes.Directory) != 0)
				{
					stack.Push(text);
					continue;
				}
				string fileName = Path.GetFileName(text);
				bool flag = IsBackupName(fileName);
				bool flag2 = IsPartialName(fileName);
				if (!flag && !flag2)
				{
					continue;
				}
				CleanupCandidate cleanupCandidate = new CleanupCandidate();
				cleanupCandidate.Kind = (flag ? CleanupKind.ReplacedBackup : CleanupKind.PartialTemp);
				cleanupCandidate.Path = text;
				CleanupCandidate cleanupCandidate2 = cleanupCandidate;
				try
				{
					cleanupCandidate2.Files.Add(Snapshot(text));
				}
				catch (Exception ex2)
				{
					cleanupCandidate2.Excluded = "Excluded: cannot read (" + ex2.Message + ")";
					list.Add(cleanupCandidate2);
					continue;
				}
				CleanupCandidate cleanupCandidate3 = cleanupCandidate2;
				string detail;
				if (flag)
				{
					detail = ((backupOrigins != null && backupOrigins.TryGetValue(text, out var value)) ? ("Previous version of " + value) : "Previous version kept by ClearCopy (original file not recorded in current logs)");
				}
				else
				{
					detail = "Unfinished temporary copy left by an interrupted job";
				}
				cleanupCandidate3.Detail = detail;
				ActiveJob activeJob = Blocking(active, text);
				if (activeJob != null)
				{
					cleanupCandidate2.Excluded = (activeJob.Unknown ? ("Excluded: cleanup is blocked by an " + activeJob.Reason) : ("Excluded: inside the destination of an " + activeJob.Reason));
				}
				DateTime createdUtc = DateTime.MinValue;
				if (flag2)
				{
					try
					{
						createdUtc = File.GetCreationTimeUtc(text);
					}
					catch
					{
					}
				}
				Finish(cleanupCandidate2, nowUtc, flag2 ? PartialRecentLimit : RecentLimit, createdUtc);
				list.Add(cleanupCandidate2);
			}
		}
		return list;
	}

	private static void Finish(CleanupCandidate c, DateTime nowUtc, TimeSpan recent)
	{
		Finish(c, nowUtc, recent, DateTime.MinValue);
	}

	private static void Finish(CleanupCandidate c, DateTime nowUtc, TimeSpan recent, DateTime createdUtc)
	{
		c.Bytes = c.Files.Sum((CleanupFile f) => f.Length);
		c.NewestUtc = ((c.Files.Count == 0) ? DateTime.MinValue : c.Files.Max((CleanupFile f) => f.WriteUtc));
		if (c.Excluded == null)
		{
			DateTime dateTime = ((createdUtc > c.NewestUtc) ? createdUtc : c.NewestUtc);
			if (nowUtc - dateTime < recent)
			{
				c.Excluded = "Excluded: changed less than " + ((recent.TotalHours >= 24.0) ? "24 hours" : "an hour") + " ago; it may belong to a running job";
			}
			else if (c.Files.Any((CleanupFile f) => InUse(f.Path)))
			{
				c.Excluded = "Excluded: open in another program";
			}
		}
	}

	public static Dictionary<string, string> BackupOrigins(string logsDirectory)
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (!Directory.Exists(logsDirectory))
		{
			return dictionary;
		}
		foreach (string item in Files(logsDirectory, "*.results.csv", ".results.csv"))
		{
			try
			{
				foreach (string item2 in File.ReadAllLines(item).Skip(1))
				{
					List<string> list = CsvCells(item2);
					if (list.Count >= 7 && !string.IsNullOrEmpty(list[6]) && !string.IsNullOrEmpty(list[2]))
					{
						dictionary[list[6]] = list[2] + " (job log " + Path.GetFileName(item).Replace(".results.csv", "") + ")";
					}
				}
			}
			catch
			{
			}
		}
		return dictionary;
	}

	public static List<string> CsvCells(string line)
	{
		List<string> list = new List<string>();
		StringBuilder stringBuilder = new StringBuilder();
		bool flag = false;
		for (int i = 0; i < line.Length; i++)
		{
			char c = line[i];
			if (flag)
			{
				if (c == '"')
				{
					if (i + 1 < line.Length && line[i + 1] == '"')
					{
						stringBuilder.Append('"');
						i++;
					}
					else
					{
						flag = false;
					}
				}
				else
				{
					stringBuilder.Append(c);
				}
				continue;
			}
			switch (c)
			{
			case '"':
				flag = true;
				break;
			case ',':
				list.Add(stringBuilder.ToString());
				stringBuilder.Clear();
				break;
			default:
				stringBuilder.Append(c);
				break;
			}
		}
		list.Add(stringBuilder.ToString());
		return list;
	}

	public static CleanupOutcome Remove(IEnumerable<CleanupCandidate> candidates, string jobsDirectory, Action<string> remove, DateTime nowUtc)
	{
		CleanupOutcome cleanupOutcome = new CleanupOutcome();
		using (HoldCleanupLock(jobsDirectory))
		{
			foreach (CleanupCandidate item in candidates.Where((CleanupCandidate x) => x.Selected).ToList())
			{
				string text = item.Excluded;
				bool destinationSide = item.Kind == CleanupKind.ReplacedBackup || item.Kind == CleanupKind.PartialTemp || item.Kind == CleanupKind.Unreadable;
				long num = 0L;
				bool flag = false;
				if (text == null)
				{
					foreach (CleanupFile file in item.Files)
					{
						text = Recheck(item, file, destinationSide, jobsDirectory);
						if (text != null)
						{
							break;
						}
					}
				}
				if (text != null)
				{
					cleanupOutcome.Skipped++;
					cleanupOutcome.Lines.Add("SKIPPED " + item.Path + " : " + text);
					continue;
				}
				foreach (CleanupFile file2 in item.Files)
				{
					string text2 = Recheck(item, file2, destinationSide, jobsDirectory);
					if (text2 != null)
					{
						flag = true;
						cleanupOutcome.Lines.Add("SKIPPED " + file2.Path + " : " + text2);
						continue;
					}
					try
					{
						remove(file2.Path);
						num += file2.Length;
						cleanupOutcome.Lines.Add("REMOVED " + file2.Path + " (" + Size(file2.Length) + ")");
					}
					catch (Exception ex)
					{
						flag = true;
						cleanupOutcome.Lines.Add("FAILED " + file2.Path + " : " + ex.Message);
					}
				}
				if (flag)
				{
					cleanupOutcome.Skipped++;
				}
				else
				{
					cleanupOutcome.Removed++;
				}
				cleanupOutcome.BytesRemoved += num;
			}
			return cleanupOutcome;
		}
	}

	private static string Recheck(CleanupCandidate c, CleanupFile f, bool destinationSide, string jobsDirectory)
	{
		string fileName = Path.GetFileName(f.Path);
		if ((c.Kind == CleanupKind.ReplacedBackup && !IsBackupName(fileName)) || (c.Kind == CleanupKind.PartialTemp && !IsPartialName(fileName)) || c.Kind == CleanupKind.Unreadable)
		{
			return "unexpected file name";
		}
		List<ActiveJob> list;
		try
		{
			list = ActiveJobs(jobsDirectory);
		}
		catch (Exception ex)
		{
			return "active jobs could not be checked (" + ex.Message + ")";
		}
		if (destinationSide)
		{
			ActiveJob activeJob = Blocking(list, f.Path);
			if (activeJob != null)
			{
				if (!activeJob.Unknown)
				{
					return "inside the destination of an " + activeJob.Reason + " (it may have started after the preview)";
				}
				return "cleanup blocked by an " + activeJob.Reason;
			}
		}
		if (c.Kind == CleanupKind.JobRecord && (LockHeld(c.Path) || list.Any((ActiveJob a) => string.Equals(a.Id, Path.GetFileName(c.Path), StringComparison.OrdinalIgnoreCase) && !a.Unknown)))
		{
			return "job became active after the preview";
		}
		try
		{
			CopyEngine.SafePath(f.Path);
		}
		catch (Exception ex2)
		{
			return ex2.Message;
		}
		Stamp stamp = Stamp.Read(f.Path);
		if (!stamp.Exists)
		{
			return "no longer exists";
		}
		if (stamp.Directory || stamp.Length != f.Length || stamp.Write != f.WriteUtc)
		{
			return "changed since the preview";
		}
		if (InUse(f.Path))
		{
			return "open in another program";
		}
		return null;
	}

	public static string WriteAudit(string logsDirectory, CleanupOutcome outcome, string header)
	{
		Directory.CreateDirectory(logsDirectory);
		string text = Path.Combine(logsDirectory, "housekeeping-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
		File.WriteAllLines(text, new string[2]
		{
			header,
			"Removed " + outcome.Removed + " item(s), " + Size(outcome.BytesRemoved) + "; skipped " + outcome.Skipped + "."
		}.Concat(outcome.Lines), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
		return text;
	}
}
