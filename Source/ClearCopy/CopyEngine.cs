using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ClearCopy;

public sealed class CopyEngine
{
	// Shared with the worker engines of a multi-file run (0.4.6), so Pause stops all of them.
	public ManualResetEventSlim Gate = new ManualResetEventSlim(initialState: true);

	public Action<Update> Progress = (Update param0) =>
	{
	};

	public Action<string> Log = (string param0) =>
	{
	};

	public Action<ItemResult> ItemCompleted = (ItemResult param0) =>
	{
	};

	public Action<string> BeforeCopy = (string param0) =>
	{
	};

	public Action<string> BeforeCommit = (string param0) =>
	{
	};

	public Action<string> BeforeRead = (string param0) =>
	{
	};

	public int Retries = 2;

	public int RetryDelayMs = 1000;

	// Network outages (share unreachable, connection reset, host unreachable)
	// are waited out instead of failing the file after two quick retries: waits
	// of 2, 4, 8, 16 then 30 s, for up to OutageWaitLimit, then the job stops
	// cleanly (NetworkOutageException) so it can be resumed.
	public TimeSpan OutageWaitLimit = TimeSpan.FromMinutes(10.0);

	public int OutageFirstWaitMs = 2000;

	public int OutageMaxWaitMs = 30000;

	private Update lastReport;

	public bool PreserveDetails;

	public bool PreservePermissions;

	public bool AuditAfter = true;

	// Null means protect newer destinations. A positive answer is single-use and
	// still subject to fresh source/destination checks before the commit.
	public Func<OverwriteConflict, bool> ConfirmNewerDestination;

	private byte[] scratch;
	private byte[] Scratch => scratch ?? (scratch = new byte[1048576]);
	private readonly HashSet<string> preparedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	public TimeSpan SkipTimeTolerance = TimeSpan.Zero;

	public IDictionary<string, string> ResumeHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	// On cancel, only the first NotReachedRowLimit unvisited files become result
	// rows (and log lines); the full list goes to NotReachedListPath when set.
	// Hundreds of thousands of rows used to freeze the window after Cancel.
	public int NotReachedRowLimit = 1000;

	// 0.4: problem rows are always kept in Result.Items; ordinary rows
	// (Copied/Skipped/Matched) only up to this many. Every row still goes to
	// ItemCompleted, which the window streams to the results CSV.
	public int KeepNormalRows = int.MaxValue;

	private int keptNormalRows;

	public string NotReachedListPath;

	public bool LogFailed;

	public string LogFailure;

	private long transferred;

	private long readBytes;

	private long workBase;

	private long workTotal;

	private long workHigh;

	private long scanBytes;

	private int scanFiles;

	private int scanFolders;

	private Result currentResult;

	private bool runActive;

	private bool runCopies;

	private bool runVerifies;

	private long runTotalBytes;

	private long doneBytes;

	private long curLength;

	private long curCopyDone;

	private long curHashDone;

	private bool curCopyFinished;

	public static bool SameForSkip(long sourceLength, DateTime sourceWrite, long targetLength, DateTime targetWrite, TimeSpan tolerance)
	{
		if (sourceLength != targetLength)
		{
			return false;
		}
		if (tolerance <= TimeSpan.Zero)
		{
			return sourceWrite == targetWrite;
		}
		return ((sourceWrite > targetWrite) ? (sourceWrite - targetWrite) : (targetWrite - sourceWrite)) <= tolerance;
	}

	public void Check(CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		Gate.Wait(token);
	}

	public void WriteLog(string line)
	{
		try
		{
			Log(line);
		}
		catch (Exception ex)
		{
			LogFailed = true;
			LogFailure = ex.Message;
			if (currentResult != null)
			{
				currentResult.LogIncomplete = true;
			}
		}
	}

	private void Report(Update u)
	{
		u.Transferred = transferred;
		u.ReadBytes = readBytes;
		if (u.Phase != "Scanning" && u.Phase != "Waiting for network")
		{
			workHigh = Math.Max(workHigh, u.WorkBytes);
			u.WorkBytes = workHigh;
			u.WorkTotal = workTotal;
		}
		if (runActive && u.Phase != "Scanning")
		{
			long num = Math.Max(0L, runTotalBytes - doneBytes);
			u.Copying = runCopies;
			u.Verifying = runVerifies;
			u.CopyLeft = (runCopies ? Math.Max(0L, num - (curCopyFinished ? curLength : Math.Min(curCopyDone, curLength))) : 0);
			u.VerifyLeft = (runVerifies ? Math.Max(0L, 2 * num - Math.Min(curHashDone, 2 * curLength)) : 0);
		}
		if (currentResult != null && u.Phase != "Scanning")
		{
			Result result = currentResult;
			u.Matched = result.Matched;
			u.Missing = result.Missing;
			u.Mismatched = result.Mismatched;
			u.Errors = result.Errors;
			u.Copied = result.Copied;
			u.Skipped = result.Skipped;
			u.ProtectedNewer = result.ProtectedNewer;
			u.NotReached = result.NotReached;
			u.Changed = result.Changed;
			u.Unchecked = result.Unchecked;
			u.MetadataDifferences = result.MetadataDifferences;
		}
		if (u.Phase != "Waiting for network")
		{
			lastReport = u;
		}
		Progress(u);
	}

	private static string Full(string path)
	{
		return Path.GetFullPath(path).TrimEnd('\\') + "\\";
	}

	private static bool Inside(string path, string parent)
	{
		return Full(path).StartsWith(Full(parent), StringComparison.OrdinalIgnoreCase);
	}

	public static void SafePath(string path)
	{
		string text = Path.GetFullPath(path);
		while (!string.IsNullOrEmpty(text))
		{
			FileAttributes? attributes = Stamp.ReadAttributes(text);
			if (attributes.HasValue && (attributes.Value & FileAttributes.ReparsePoint) != 0)
			{
				throw new IOException("Links/junctions are not followed: " + text);
			}
			text = Path.GetDirectoryName(text);
		}
	}

	// Per-run cache of ancestor folders already confirmed to exist and not be
	// links/junctions. SafePath re-reads every ancestor on every call, which on a
	// deep UNC path costs ~10 SMB round trips per check and ~6-8 checks per file.
	// Read-only checks (scanning, skip decisions, hashing, compare) re-read the
	// path itself and its immediate parent and trust higher ancestors confirmed
	// earlier in this run. Write checks (forWrite: true) always walk the whole
	// chain, exactly as SafePath does, so nothing that creates or changes
	// destination content relies on the cache. Cleared at the start of every
	// Plan/Run. Missing folders are never cached. The engine is sequential.
	private readonly HashSet<string> safeAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	public long PathAttributeReads;

	public void ResetPathCache()
	{
		safeAncestors.Clear();
	}

	public void CheckPath(string path, bool forWrite = false)
	{
		string text = Path.GetFullPath(path);
		List<string> confirmed = null;
		int level = 0;
		while (!string.IsNullOrEmpty(text))
		{
			// Level 0 is the path itself, level 1 its parent: always re-read.
			if (!forWrite && level >= 2 && safeAncestors.Contains(text))
			{
				// This folder and everything above it were confirmed earlier this run.
				break;
			}
			PathAttributeReads++;
			FileAttributes? attributes = Stamp.ReadAttributes(text);
			if (attributes.HasValue && (attributes.Value & FileAttributes.ReparsePoint) != 0)
			{
				throw new IOException("Links/junctions are not followed: " + text);
			}
			if (level >= 2 && attributes.HasValue && (attributes.Value & FileAttributes.Directory) != 0)
			{
				(confirmed ?? (confirmed = new List<string>())).Add(text);
			}
			text = Path.GetDirectoryName(text);
			level++;
		}
		// Only reached when no link was found on the whole chain. A missing
		// folder is never cached, so it is re-read once it has been created.
		if (confirmed != null)
		{
			foreach (string folder in confirmed)
			{
				safeAncestors.Add(folder);
			}
		}
	}

	private T Retry<T>(Func<T> operation, CancellationToken token, string path)
	{
		int num = 0;
		DateTime? outageStart = null;
		int outageWaits = 0;
		while (true)
		{
			Check(token);
			try
			{
				return operation();
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (IOException ex) when (NetworkOutageException.IsNetworkError(ex))
			{
				DateTime now = DateTime.UtcNow;
				if (!outageStart.HasValue)
				{
					outageStart = now;
				}
				TimeSpan waited = now - outageStart.Value;
				if (waited >= OutageWaitLimit)
				{
					WriteLog("NETWORK UNAVAILABLE " + path + " : gave up after " + NetworkOutageException.Describe(waited) + " : " + NetworkOutageException.Clean(ex.Message));
					throw new NetworkOutageException(path, waited, ex);
				}
				long delay = Math.Min((long)OutageMaxWaitMs, (long)OutageFirstWaitMs << Math.Min(outageWaits, 20));
				delay = Math.Max(1L, Math.Min(delay, (long)(OutageWaitLimit - waited).TotalMilliseconds + 1));
				outageWaits++;
				string note = "Network location unavailable (" + NetworkOutageException.Clean(ex.Message) + "). Retrying in " + Math.Max(1L, delay / 1000) + " s; waiting up to " + Math.Round(OutageWaitLimit.TotalMinutes) + " min in total before stopping the job. Pause and Cancel still work.";
				WriteLog("NETWORK WAIT " + path + " : attempt " + outageWaits + ", retrying in " + delay + " ms : " + NetworkOutageException.Clean(ex.Message));
				Update waiting = (lastReport != null) ? lastReport.Copy() : new Update();
				waiting.Phase = "Waiting for network";
				waiting.Path = path;
				waiting.Note = note;
				Report(waiting);
				if (token.WaitHandle.WaitOne((int)delay))
				{
					token.ThrowIfCancellationRequested();
				}
				continue;
			}
			catch (ChangedException)
			{
				throw;
			}
			catch (UnauthorizedAccessException)
			{
				throw;
			}
			catch (IOException ex4)
			{
				if (num >= Retries || ex4 is PathTooLongException || ex4 is FileNotFoundException || ex4 is DirectoryNotFoundException)
				{
					throw;
				}
				WriteLog("RETRY " + (num + 1) + " " + path + " : " + ex4.Message);
				if (token.WaitHandle.WaitOne(RetryDelayMs))
				{
					token.ThrowIfCancellationRequested();
				}
			}
			num++;
		}
	}

	public List<Entry> Plan(IEnumerable<string> paths, string destination, CancellationToken token, bool folderContents = false)
	{
		ResetPathCache();
		scanFiles = (scanFolders = 0);
		scanBytes = 0L;
		currentResult = null;
		Report(new Update
		{
			Phase = "Scanning",
			Path = destination
		});
		destination = Path.GetFullPath(destination);
		CheckPath(destination);
		List<string> roots = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		roots = roots.Where((string p) => !roots.Any((string other) => !string.Equals(p, other, StringComparison.OrdinalIgnoreCase) && Directory.Exists(LongPath.ForIO(other)) && Inside(p, other))).ToList();
		if (folderContents && roots.Count != 1)
		{
			throw new IOException("Folder contents mode requires exactly one source folder.");
		}
		CopyPlan copyPlan = new CopyPlan();
		copyPlan.Roots = roots.ToArray();
		copyPlan.Destination = destination;
		copyPlan.Contents = folderContents;
		CopyPlan copyPlan2 = copyPlan;
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string item in roots)
		{
			Check(token);
			string fileName = Path.GetFileName(item.TrimEnd('\\'));
			if (!folderContents && !item.StartsWith("\\\\", StringComparison.Ordinal) && Full(item) == Full(Path.GetPathRoot(item)))
			{
				throw new IOException("Select a folder within the drive, or use folder contents mode.");
			}
			string text = (folderContents ? destination : Path.Combine(destination, fileName));
			if (Inside(text, item) || Inside(item, text))
			{
				throw new IOException("Source and destination overlap: copying " + item + " to " + text + " would copy the folder into itself. Choose a destination outside the source.");
			}
			if (!hashSet.Add(Path.GetFullPath(text)))
			{
				throw new IOException("Two sources map to the same destination: " + text);
			}
			try
			{
				CheckPath(item);
				if (folderContents && !Stamp.Read(item).Directory)
				{
					throw new IOException("Folder contents mode requires a source folder.");
				}
			}
			catch (Exception ex)
			{
				copyPlan2.Add(new Entry
				{
					Source = item,
					Target = text,
					ScanError = ex.Message,
					Directory = true
				});
				continue;
			}
			Add(item, text, copyPlan2, hashSet, token, root: true);
		}
		return copyPlan2;
	}

	private void Add(string source, string target, List<Entry> entries, HashSet<string> targets, CancellationToken token, bool root = false)
	{
		Check(token);
		Entry entry = new Entry();
		entry.Source = source;
		entry.Target = target;
		Entry entry2 = entry;
		try
		{
			if (!root && !targets.Add(Path.GetFullPath(target)))
			{
				throw new IOException("Destination collision: " + target);
			}
			Stamp stamp = Retry(() => Stamp.Read(source), token, source);
			if (!stamp.Exists)
			{
				throw new FileNotFoundException("Source disappeared during scanning", source);
			}
			if ((stamp.Attributes & FileAttributes.ReparsePoint) != 0)
			{
				throw new IOException("Link/junction skipped: " + source);
			}
			entry2.Directory = stamp.Directory;
			entry2.Length = stamp.Length;
			entry2.SourceState = stamp;
			entries.Add(entry2);
			if (stamp.Directory)
			{
				scanFolders++;
			}
			else
			{
				scanFiles++;
				scanBytes += stamp.Length;
			}
			Report(new Update
			{
				Phase = "Scanning",
				Path = source,
				Total = scanFiles,
				Folders = scanFolders,
				TotalBytes = scanBytes
			});
			if (stamp.Directory)
			{
				string[] array = Retry(() => Directory.GetFileSystemEntries(LongPath.ForIO(source)).Select(LongPath.ForDisplay).ToArray(), token, source);
				string[] array2 = array;
				foreach (string text in array2)
				{
					Add(text, Path.Combine(target, Path.GetFileName(text)), entries, targets, token);
				}
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex2)
		{
			entry2.ScanError = ex2.Message;
			entry2.AccessDenied = AccessFix.IsAccessDenied(ex2);
			if (!entries.Contains(entry2))
			{
				entries.Add(entry2);
			}
			WriteLog("SCAN ERROR " + source + " : " + ex2.Message);
		}
	}

	// 0.4.1: rescans the given entries (folders after "Fix access", or folders
	// picked for a retry) and puts their contents in their place in the plan.
	public void RescanAll(List<Entry> plan, ICollection<Entry> entries, CancellationToken token)
	{
		if (entries.Count == 0)
		{
			return;
		}
		HashSet<Entry> set = new HashSet<Entry>(entries);
		List<Entry> list = new List<Entry>(plan.Count + entries.Count * 4);
		foreach (Entry entry in plan)
		{
			if (!set.Contains(entry))
			{
				list.Add(entry);
				continue;
			}
			Check(token);
			List<Entry> sub = new List<Entry>();
			Add(entry.Source, entry.Target, sub, new HashSet<string>(StringComparer.OrdinalIgnoreCase), token, root: true);
			list.AddRange(sub);
		}
		plan.Clear();
		plan.AddRange(list);
	}

	private void Outcome(Entry e, string status, string reason, string hash = null, string backup = null)
	{
		ItemResult itemResult = new ItemResult();
		itemResult.Source = e.Source;
		itemResult.Target = e.Target;
		itemResult.Directory = e.Directory;
		itemResult.Size = e.Length;
		itemResult.Status = status;
		itemResult.Reason = reason;
		itemResult.Hash = hash;
		itemResult.Backup = backup;
		ItemResult itemResult2 = itemResult;
		if (Jobs.IsIssue(itemResult2) || keptNormalRows++ < KeepNormalRows)
		{
			currentResult.Items.Add(itemResult2);
		}
		WriteLog(status.ToUpperInvariant() + " " + e.Target + " : " + reason + ((backup == null) ? "" : (" | previous version: " + backup)));
		try
		{
			ItemCompleted(itemResult2);
		}
		catch (Exception ex)
		{
			WriteLog("CHECKPOINT WARNING " + ex.Message);
			currentResult.LogIncomplete = true;
		}
	}

	public Result Run(List<Entry> plan, ExistingRule rule, bool verify, bool compareOnly, CancellationToken token)
	{
		return Run(plan, rule, compareOnly ? OperationMode.Compare : (verify ? OperationMode.VerifiedCopy : OperationMode.FastCopy), token);
	}

	public Result Run(List<Entry> plan, ExistingRule rule, OperationMode mode, CancellationToken token)
	{
		if (!Enum.IsDefined(typeof(OperationMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
		return RunCore(plan, rule, mode != OperationMode.FastCopy, mode == OperationMode.Compare, false, token);
	}

	// Callers select confirmed content issues; reclassification below also makes
	// stale reports safe: matched, inaccessible or changed files are never forced.
	public Result Repair(List<Entry> candidates, CancellationToken token)
	{
		return RunCore(candidates.Where(e => !e.Directory).ToList(), ExistingRule.Replace, true, false, true, token);
	}

	// 0.4.6: several files at once. Folders are created first (one worker), then
	// the files are handed out in small batches to FilesAtOnce worker engines,
	// each running the normal, unchanged copy/verify routine on its batch.
	// Folder dates/permissions and the final source re-check run once at the end.
	public int FilesAtOnce = 1;

	public int ChunkFiles = 64;

	public long ChunkBytes = 67108864L;

	private bool isWorker;

	private bool SkipFolderMetadata;

	private CopyEngine NewWorker(Action<Update> progress)
	{
		return new CopyEngine
		{
			isWorker = true,
			SkipFolderMetadata = true,
			AuditAfter = false,
			Gate = Gate,
			Progress = progress,
			Log = Log,
			ItemCompleted = ItemCompleted,
			BeforeCopy = BeforeCopy,
			BeforeCommit = BeforeCommit,
			BeforeRead = BeforeRead,
			ConfirmNewerDestination = ConfirmNewerDestination,
			ResumeHashes = ResumeHashes,
			Retries = Retries,
			RetryDelayMs = RetryDelayMs,
			OutageWaitLimit = OutageWaitLimit,
			OutageFirstWaitMs = OutageFirstWaitMs,
			OutageMaxWaitMs = OutageMaxWaitMs,
			PreserveDetails = PreserveDetails,
			PreservePermissions = PreservePermissions,
			SkipTimeTolerance = SkipTimeTolerance,
			KeepNormalRows = int.MaxValue,
			NotReachedRowLimit = int.MaxValue,
			NotReachedListPath = null,
			ParallelVerify = ParallelVerify,
			ParallelMinimumBytes = ParallelMinimumBytes,
			ParallelOnSameStorage = ParallelOnSameStorage,
			UncachedVerify = UncachedVerify
		};
	}

	private void MergeInto(Result target, Result r)
	{
		if (r == null)
		{
			return;
		}
		target.Copied += r.Copied;
		target.Skipped += r.Skipped;
		target.ProtectedNewer += r.ProtectedNewer;
		target.NotReached += r.NotReached;
		target.Errors += r.Errors;
		target.Matched += r.Matched;
		target.Missing += r.Missing;
		target.Mismatched += r.Mismatched;
		target.Changed += r.Changed;
		target.Unchecked += r.Unchecked;
		target.MetadataDifferences += r.MetadataDifferences;
		target.FoldersCreated += r.FoldersCreated;
		target.FoldersPresent += r.FoldersPresent;
		target.LogIncomplete |= r.LogIncomplete;
		foreach (ItemResult item in r.Items)
		{
			if (Jobs.IsIssue(item) || keptNormalRows++ < KeepNormalRows)
			{
				target.Items.Add(item);
			}
		}
	}

	private Result RunParallel(List<Entry> plan, ExistingRule rule, bool verify, bool compareOnly, CancellationToken token)
	{
		preparedDirectories.Clear();
		ResetPathCache();
		Result result = (currentResult = new Result());
		keptNormalRows = 0;
		result.LogIncomplete = LogFailed;
		List<Entry> files = plan.Where((Entry e) => !e.Directory && e.ScanError == null).ToList();
		List<Entry> folders = plan.Where((Entry e) => e.Directory || e.ScanError != null).ToList();
		long total = files.Sum((Entry e) => e.Length);
		int count = files.Count;
		long factor = compareOnly ? 2 : ((!verify) ? 1 : 3);
		workTotal = total * factor;
		result.PlannedFiles = count;
		result.PlannedFolders = plan.Count((Entry e) => e.Directory && e.ScanError == null);
		result.ScanFailures = plan.Count((Entry e) => e.ScanError != null);
		List<List<Entry>> chunks = new List<List<Entry>>();
		List<Entry> chunk = new List<Entry>();
		long chunkBytes = 0L;
		foreach (Entry file in files)
		{
			if (chunk.Count > 0 && (chunk.Count >= ChunkFiles || chunkBytes + file.Length > ChunkBytes))
			{
				chunks.Add(chunk);
				chunk = new List<Entry>();
				chunkBytes = 0L;
			}
			chunk.Add(file);
			chunkBytes += file.Length;
		}
		if (chunk.Count > 0)
		{
			chunks.Add(chunk);
		}
		int workers = Math.Max(1, Math.Min(FilesAtOnce, Math.Max(1, chunks.Count)));
		WriteLog("PARALLEL " + workers + " files at once; " + count + " files in " + chunks.Count + " batches");
		object sync = new object();
		Update[] live = new Update[workers];
		int lastWorker = 0;
		long finishedDone = 0L;
		long finishedBytes = 0L;
		long finishedTransferred = 0L;
		long finishedRead = 0L;
		long high = 0L;
		Action<int, Update> onUpdate = (int k, Update u) =>
		{
			Update c;
			lock (sync)
			{
				live[k] = u;
				lastWorker = k;
				c = new Update();
				Update shown = live[k];
				for (int i = 0; i < live.Length; i++)
				{
					if (live[i] != null && live[i].Phase == "Waiting for network")
					{
						shown = live[i];
					}
				}
				c.Phase = shown.Phase;
				c.Path = shown.Path;
				c.Note = shown.Note;
				c.FileBytes = shown.FileBytes;
				c.FileTotal = shown.FileTotal;
				c.Total = count;
				c.TotalBytes = total;
				c.WorkTotal = workTotal;
				long done = finishedDone;
				long bytes = finishedBytes;
				long transferredNow = finishedTransferred;
				long readNow = finishedRead;
				c.Copied = result.Copied;
				c.Skipped = result.Skipped;
				c.Matched = result.Matched;
				c.Missing = result.Missing;
				c.Mismatched = result.Mismatched;
				c.Errors = result.Errors;
				c.Changed = result.Changed;
				c.Unchecked = result.Unchecked;
				c.MetadataDifferences = result.MetadataDifferences;
				c.ProtectedNewer = result.ProtectedNewer;
				c.NotReached = result.NotReached;
				foreach (Update w in live)
				{
					if (w == null)
					{
						continue;
					}
					done += w.Done;
					bytes += w.Bytes;
					transferredNow += w.Transferred;
					readNow += w.ReadBytes;
					c.Copied += w.Copied;
					c.Skipped += w.Skipped;
					c.Matched += w.Matched;
					c.Missing += w.Missing;
					c.Mismatched += w.Mismatched;
					c.Errors += w.Errors;
					c.Changed += w.Changed;
					c.Unchecked += w.Unchecked;
					c.MetadataDifferences += w.MetadataDifferences;
					c.ProtectedNewer += w.ProtectedNewer;
					c.NotReached += w.NotReached;
				}
				c.Done = (int)Math.Min(count, done);
				c.Bytes = Math.Min(total, bytes);
				c.Transferred = transferredNow;
				c.ReadBytes = readNow;
				high = Math.Max(high, Math.Min(workTotal, finishedBytes * factor + live.Where((Update w) => w != null).Sum((Update w) => w.WorkBytes)));
				c.WorkBytes = high;
				c.Copying = !compareOnly;
				c.Verifying = compareOnly || verify;
				c.CopyLeft = c.Copying ? Math.Max(0L, total - c.Bytes) : 0;
				c.VerifyLeft = c.Verifying ? Math.Max(0L, 2 * (total - c.Bytes)) : 0;
			}
			Progress(c);
		};
		CopyEngine[] engines = new CopyEngine[workers];
		for (int i = 0; i < workers; i++)
		{
			int k = i;
			engines[i] = NewWorker((Update u) => onUpdate(k, u));
		}
		using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
		Exception failure = null;
		int next = 0;
		Action<CopyEngine, int, List<Entry>> runOne = (CopyEngine w, int k, List<Entry> work) =>
		{
			Result r = null;
			try
			{
				r = w.RunCore(work, rule, verify, compareOnly, repairOnly: false, stop.Token);
			}
			catch (Exception ex)
			{
				lock (sync)
				{
					bool userCancel = ex is OperationCanceledException && !(ex is NetworkOutageException);
					if (failure == null || (failure is OperationCanceledException && !(failure is NetworkOutageException) && !userCancel))
					{
						failure = ex;
					}
					MergeInto(result, w.currentResult);
					finishedTransferred += w.transferred;
					finishedRead += w.readBytes;
					live[k] = null;
				}
				stop.Cancel();
				return;
			}
			lock (sync)
			{
				MergeInto(result, r);
				finishedDone += work.Count((Entry e) => !e.Directory && e.ScanError == null);
				finishedBytes += work.Where((Entry e) => !e.Directory && e.ScanError == null).Sum((Entry e) => e.Length);
				finishedTransferred += w.transferred;
				finishedRead += w.readBytes;
				live[k] = null;
			}
		};
		if (folders.Count > 0)
		{
			runOne(engines[0], 0, folders);
		}
		if (failure == null)
		{
			Task[] tasks = new Task[workers];
			for (int i = 0; i < workers; i++)
			{
				int k = i;
				tasks[i] = Task.Run(() =>
				{
					while (true)
					{
						int index;
						lock (sync)
						{
							if (failure != null || stop.IsCancellationRequested || next >= chunks.Count)
							{
								return;
							}
							index = next++;
						}
						runOne(engines[k], k, chunks[index]);
					}
				});
			}
			Task.WaitAll(tasks);
		}
		foreach (CopyEngine w in engines)
		{
			ParallelVerifications += w.ParallelVerifications;
			SourceRereads += w.SourceRereads;
			DestinationOnlyChecks += w.DestinationOnlyChecks;
			UncachedReads += w.UncachedReads;
			PathAttributeReads += w.PathAttributeReads;
			LogFailed |= w.LogFailed;
			if (w.LogFailure != null)
			{
				LogFailure = w.LogFailure;
			}
		}
		result.FilesProcessed = (int)finishedDone;
		if (failure != null || token.IsCancellationRequested)
		{
			List<Entry> unvisited = chunks.Skip(Math.Min(next, chunks.Count)).SelectMany((List<Entry> c) => c).ToList();
			if (folders.Count > 0 && next == 0 && failure != null)
			{
				unvisited = files;
			}
			result.NotReached += unvisited.Count;
			foreach (Entry pending in unvisited.Take(Math.Max(0, NotReachedRowLimit)))
			{
				Outcome(pending, "Not reached", "Cancelled before this file was reached");
			}
			if (unvisited.Count > NotReachedRowLimit)
			{
				string listNote = "";
				if (!string.IsNullOrEmpty(NotReachedListPath))
				{
					try
					{
						using StreamWriter writer = new StreamWriter(NotReachedListPath, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
						writer.WriteLine("Files not reached before cancellation (" + unvisited.Count + "). Source -> destination:");
						foreach (Entry pending in unvisited)
						{
							writer.WriteLine(pending.Source + " -> " + pending.Target);
						}
						listNote = "; full list: " + NotReachedListPath;
					}
					catch (Exception ex)
					{
						listNote = "; full list could not be written: " + ex.Message;
					}
				}
				WriteLog("NOT REACHED " + (unvisited.Count - NotReachedRowLimit) + " more files beyond the first " + NotReachedRowLimit + " listed" + listNote);
			}
			Progress(new Update { Phase = "Cancelled", Done = (int)finishedDone, Total = count, Bytes = finishedBytes, TotalBytes = total, WorkBytes = high, WorkTotal = workTotal });
			if (failure == null || (failure is OperationCanceledException && !(failure is NetworkOutageException)))
			{
				throw new OperationCanceledException(token.IsCancellationRequested ? token : CancellationToken.None);
			}
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
		}
		foreach (Entry item in plan.Where((Entry e) => e.Directory && e.ScanError == null).Reverse())
		{
			ApplyOrCompareMetadata(item, compareOnly, result, token);
		}
		if (AuditAfter)
		{
			Audit(plan, token, (int)finishedDone, count, total);
		}
		result.LogIncomplete |= LogFailed;
		WriteLog($"RESULT copied={result.Copied}, skipped={result.Skipped}, errors={result.Errors}, matched={result.Matched}, missing={result.Missing}, mismatched={result.Mismatched}, changed={result.Changed}, unchecked={result.Unchecked}, metadata differences={result.MetadataDifferences}");
		return result;
	}

	private Result RunCore(List<Entry> plan, ExistingRule rule, bool verify, bool compareOnly, bool repairOnly, CancellationToken token)
	{
		if (FilesAtOnce > 1 && !isWorker && !repairOnly)
		{
			return RunParallel(plan, rule, verify, compareOnly, token);
		}
		preparedDirectories.Clear();
		ResetPathCache();
		foreach (Entry item in plan)
		{
			item.VerifiedSource = null;
			item.VerifiedTarget = null;
		}
		transferred = (readBytes = (workBase = (workHigh = 0L)));
		Result result = (currentResult = new Result());
		keptNormalRows = 0;
		uncachedFailed = false;
		result.LogIncomplete = LogFailed;
		long total = plan.Where((Entry entry) => !entry.Directory && entry.ScanError == null).Sum((Entry entry) => entry.Length);
		long bytes = 0L;
		int done = 0;
		int count = plan.Count((Entry entry) => !entry.Directory && entry.ScanError == null);
		workTotal = total * (compareOnly ? 2 : ((!verify) ? 1 : 3));
		runActive = true;
		runCopies = !compareOnly;
		runVerifies = compareOnly || verify;
		runTotalBytes = total;
		doneBytes = 0L;
		curLength = (curCopyDone = (curHashDone = 0L));
		curCopyFinished = true;
		result.PlannedFiles = count;
		result.PlannedFolders = plan.Count((Entry entry) => entry.Directory && entry.ScanError == null);
		result.ScanFailures = plan.Count((Entry entry) => entry.ScanError != null);
		int reachedCount = 0;
		Entry active = null;
		try
		{
		foreach (Entry e in plan)
		{
			Check(token);
			reachedCount++;
			active = e;
			if (e.ScanError != null)
			{
				result.Errors++;
				result.Unchecked++;
				Outcome(e, "Unchecked", "Scan failed: " + e.ScanError);
				active = null;
				continue;
			}
			if (e.Directory)
			{
				try
				{
					CheckPath(e.Target, forWrite: !compareOnly);
					Stamp stamp = Retry(() => Stamp.Read(e.Target), token, e.Target);
					if (stamp.Exists && !stamp.Directory)
						throw new IOException("Expected destination directory, found file");
					if (compareOnly)
					{
						if (!stamp.Exists)
						{
							result.Missing++;
							Outcome(e, "Missing", "Directory is absent");
							continue;
						}
						if (!stamp.Directory)
						{
							throw new IOException("Expected directory, found file");
						}
						result.FoldersPresent++;
					}
					else
					{
						Retry(() =>
						{
							if (!stamp.Exists) Directory.CreateDirectory(LongPath.ForIO(e.Target));
							preparedDirectories.Add(e.Target);
							return true;
						}, token, e.Target);
						if (stamp.Exists)
						{
							result.FoldersPresent++;
							continue;
						}
						result.FoldersCreated++;
						WriteLog("FOLDER CREATED " + e.Target);
					}
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex2)
				{
					result.Errors++;
					result.Unchecked++;
					Outcome(e, "Error", Describe(ex2));
				}
				continue;
			}
			curLength = e.Length;
			curCopyDone = 0L;
			curHashDone = 0L;
			curCopyFinished = compareOnly;
			Report(new Update
			{
				Phase = (compareOnly ? "Comparing" : "Copying"),
				Path = e.Source,
				Bytes = bytes,
				TotalBytes = total,
				Done = done,
				Total = count,
				WorkBytes = workBase,
				FileTotal = e.Length
			});
			bool flag = compareOnly;
			bool flag2 = compareOnly;
			bool protectedNewer = false;
			bool preserveNewerMetadata = false;
			bool contentMatched = false;
			string revalidatedHash = null;
			string backup = null;
			if (!compareOnly)
			{
				try
				{
					Stamp initial = Retry(() =>
					{
						CheckPath(e.Source);
						CheckPath(e.Target);
						return Stamp.Read(e.Target);
					}, token, e.Target);
					if (initial.Directory)
					{
						throw new IOException("Expected file, found destination directory");
					}
					Stamp stamp2 = Stamp.Read(e.Source);
					if (!stamp2.Exists)
					{
						throw new FileNotFoundException("Source no longer exists", e.Source);
					}
					if (stamp2.Directory || (e.SourceState != null && !e.SourceState.Same(stamp2)))
						throw new ChangedException("Source changed since scanning; rescan before writing");
					bool flag3 = initial.Exists && stamp2.Length == initial.Length && stamp2.Write == initial.Write;
					bool flag4 = initial.Exists && SameForSkip(stamp2.Length, stamp2.Write, initial.Length, initial.Write, SkipTimeTolerance);
					bool flag5 = initial.Exists && stamp2.Write < initial.Write;
					preserveNewerMetadata = flag5;
					bool flag6 = !repairOnly && verify && ResumeHashes.TryGetValue(e.Target, out var checkpoint) && initial.Exists && (revalidatedHash = Retry(() => PairHash(e, token, (long v, long n, string p, string phase) =>
					{
						Report(new Update
						{
							Phase = "Revalidating checkpoint",
							Path = p,
							Bytes = bytes,
							TotalBytes = total,
							Done = done,
							Total = count,
							WorkBytes = workBase,
							FileBytes = v,
							FileTotal = n
						});
					}), token, e.Target)) == checkpoint;
					if (!flag6) revalidatedHash = null;
					if (repairOnly && initial.Exists)
					{
						string currentHash = Retry(() => PairHash(e, token, (v, n, p, phase) => { Check(token); }), token, e.Target);
						if (!stamp2.Same(Stamp.Read(e.Source)) || !initial.Same(Stamp.Read(e.Target)))
							throw new ChangedException("Repair candidate changed during reclassification");
						if (currentHash != null)
						{
							result.Matched++;
							Outcome(e, "Matched", "Repair recheck: SHA-256 already matches; no content copied", currentHash);
							ApplyOrCompareMetadata(e, true, result, token);
							goto FileFinished;
						}
					}
					bool policySkip = !repairOnly && ((rule != ExistingRule.Replace && flag4) || (rule == ExistingRule.SkipSameAndOlder && flag5));
					if (flag5 && !flag6)
					{
						// A skip decision never asks to overwrite. Replace/repair may ask,
						// but absence or refusal of confirmation always protects the file.
						bool approved = !policySkip && ConfirmNewerDestination != null && ConfirmNewerDestination(new OverwriteConflict(e, stamp2, initial));
						if (approved) preserveNewerMetadata = false;
						Check(token);
						if (!stamp2.Same(Stamp.Read(e.Source)) || !initial.Same(Stamp.Read(e.Target)))
							throw new ChangedException("Source or destination changed during newer-file confirmation");
						if (!approved)
						{
							protectedNewer = true;
							policySkip = true;
							result.ProtectedNewer++;
						}
					}
					if (flag6 || policySkip)
					{
						if (flag6)
						{
							flag2 = true;
						}
						result.Skipped++;
						CopyEngine copyEngine = this;
						Entry e2 = e;
						string reason;
						if (flag6)
						{
							reason = "Completed checkpoint revalidated by SHA-256";
						}
						else
						{
							reason = ((flag4 && !flag3 && (rule != ExistingRule.SkipSameAndOlder || !flag5)) ? ("Policy skip: same size, modified times within the " + SkipTimeTolerance.TotalSeconds + " s skip tolerance; size/time is not content verification") : "Policy skip; size/time is not content verification");
						}
						copyEngine.Outcome(e2, protectedNewer ? "Protected newer" : "Skipped", protectedNewer ? "Newer destination protected; no replacement authorized" : reason);
						flag = true;
					}
					else
					{
						string initialHash = (initial.Exists ? Retry(() => ObservedHash(e.Target, "Checking existing destination", bytes, total, done, count, workBase, token), token, e.Target) : null);
						Retry(() =>
						{
							if (!initial.Same(Stamp.Read(e.Target)))
							{
								throw new ChangedException("Destination changed after overwrite decision");
							}
							BeforeCopy(e.Source);
							CopyOne(e, stamp2, initial, initialHash, bytes, total, done, count, token, out backup);
							return true;
						}, token, e.Target);
						result.Copied++;
						flag = true;
						flag2 = true;
						Outcome(e, "Copied", "Primary stream copied; last-write time preserved", null, backup);
					}
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (ChangedException ex4)
				{
					result.Changed++;
					result.Unchecked++;
					Outcome(e, "Changed", ex4.Message);
				}
				catch (Exception ex5)
				{
					result.Errors++;
					result.Unchecked++;
					Outcome(e, "Error", Describe(ex5));
				}
				curCopyFinished = true;
			}
			if (compareOnly || (verify && flag))
			{
				try
				{
					CheckPath(e.Source);
					CheckPath(e.Target);
					Stamp stamp3 = Retry(() => Stamp.Read(e.Target), token, e.Target);
					if (!stamp3.Exists)
					{
						result.Missing++;
						Outcome(e, "Missing", "Destination file is absent");
					}
					else
					{
						if (stamp3.Directory)
						{
							throw new IOException("Expected file, found directory");
						}
						long hashBase = workBase + (compareOnly ? 0 : e.Length);
						if (revalidatedHash != null && (!e.VerifiedSource.Same(Stamp.Read(e.Source)) || !e.VerifiedTarget.Same(Stamp.Read(e.Target))))
							throw new ChangedException("Checkpoint file changed after revalidation");
					lastCheckDestinationOnly = false;
					string text = revalidatedHash ?? Retry(() => PairHash(e, token, (long v, long size, string p, string phase) =>
						{
							curHashDone = Math.Min(v, 2 * e.Length);
							Report(new Update
							{
								Phase = phase,
								Path = p,
								Bytes = bytes,
								TotalBytes = total,
								Done = done,
								Total = count,
								WorkBytes = hashBase + Math.Min(v, 2 * e.Length),
								FileBytes = v,
								FileTotal = size
							});
						}), token, e.Target);
						if (text == null)
						{
							result.Mismatched++;
							Outcome(e, "Mismatch", "Primary file contents differ");
						}
						else
						{
							flag2 = true;
							contentMatched = true;
							result.Matched++;
							Outcome(e, "Matched", lastCheckDestinationOnly ? "SHA-256 matched: destination read back and compared with the source hash taken while copying" : "SHA-256 primary streams matched while both files were held read-only", text, backup);
						}
					}
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (ChangedException ex7)
				{
					result.Changed++;
					result.Unchecked++;
					Outcome(e, "Changed", ex7.Message);
				}
				catch (Exception ex8)
				{
					result.Errors++;
					result.Unchecked++;
					Outcome(e, "Error", Describe(ex8));
				}
			}
			if (flag && flag2 && !protectedNewer && !preserveNewerMetadata && (compareOnly || !verify || contentMatched))
			{
				ApplyOrCompareMetadata(e, compareOnly, result, token);
			}
		FileFinished:
			bytes += e.Length;
			done++;
			workBase += e.Length * (compareOnly ? 2 : ((!verify) ? 1 : 3));
			doneBytes = bytes;
			curLength = (curCopyDone = (curHashDone = 0L));
			curCopyFinished = true;
			Report(new Update
			{
				Phase = (compareOnly ? "Comparing" : "Copying"),
				Path = e.Target,
				Bytes = bytes,
				TotalBytes = total,
				Done = done,
				Total = count,
				WorkBytes = workBase,
				FileBytes = e.Length,
				FileTotal = e.Length
			});
			active = null;
		}
		}
		catch (OperationCanceledException)
		{
			if (active != null && !active.Directory)
			{
				result.Unchecked++;
				Outcome(active, "Unchecked", "Cancelled during operation; recheck current file before trusting it");
			}
			List<Entry> unvisited = plan.Skip(reachedCount).Where(e => !e.Directory).ToList();
			result.NotReached += unvisited.Count;
			foreach (Entry pending in unvisited.Take(Math.Max(0, NotReachedRowLimit)))
			{
				Outcome(pending, "Not reached", "Cancelled before this file was reached");
			}
			if (unvisited.Count > NotReachedRowLimit)
			{
				string listNote = "";
				if (!string.IsNullOrEmpty(NotReachedListPath))
				{
					try
					{
						using (StreamWriter writer = new StreamWriter(NotReachedListPath, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
						{
							writer.WriteLine("Files not reached before cancellation (" + unvisited.Count + "). Source -> destination:");
							foreach (Entry pending in unvisited)
							{
								writer.WriteLine(pending.Source + " -> " + pending.Target);
							}
						}
						listNote = "; full list: " + NotReachedListPath;
					}
					catch (Exception ex)
					{
						listNote = "; full list could not be written: " + ex.Message;
					}
				}
				WriteLog("NOT REACHED " + (unvisited.Count - NotReachedRowLimit) + " more files beyond the first " + NotReachedRowLimit + " listed" + listNote);
			}
			Report(new Update { Phase = "Cancelled", Done = done, Total = count, Bytes = bytes, TotalBytes = total, WorkBytes = workBase });
			throw;
		}
		result.FilesProcessed = done;
		if (!SkipFolderMetadata)
		{
			foreach (Entry item2 in plan.Where((Entry entry) => entry.Directory && entry.ScanError == null).Reverse())
			{
				ApplyOrCompareMetadata(item2, compareOnly, result, token);
			}
		}
		if (AuditAfter)
		{
			Audit(plan, token, done, count, total);
		}
		result.LogIncomplete |= LogFailed;
		runActive = false;
		WriteLog($"RESULT copied={result.Copied}, skipped={result.Skipped}, errors={result.Errors}, matched={result.Matched}, missing={result.Missing}, mismatched={result.Mismatched}, changed={result.Changed}, unchecked={result.Unchecked}, metadata differences={result.MetadataDifferences}");
		return result;
	}

	private static string Describe(Exception ex)
	{
		string text;
		if (ex is UnauthorizedAccessException)
		{
			text = "Access denied: ";
		}
		else if (ex is FileNotFoundException || ex is DirectoryNotFoundException)
		{
			text = "Source or folder unavailable: ";
		}
		else
		{
			text = ((ex is IOException) ? "File/network I/O failed or locked: " : "");
		}
		return text + ex.Message;
	}

	private void CopyOne(Entry e, Stamp approvedSource, Stamp initial, string initialHash, long prior, long total, int done, int count, CancellationToken token, out string backup)
	{
		backup = null;
		CheckPath(e.Target, forWrite: true);
		string parent = Path.GetDirectoryName(e.Target);
		// This tracks creation only, never path safety. CheckPath still runs above
		// and immediately before commit, even for already-prepared directories.
		if (!preparedDirectories.Contains(parent))
		{
			Directory.CreateDirectory(LongPath.ForIO(parent));
			preparedDirectories.Add(parent);
		}
		string text = Path.Combine(Path.GetDirectoryName(e.Target), ".clearcopy-" + Guid.NewGuid().ToString("N") + ".partial");
		try
		{
			using FileStream fileStream = new FileStream(LongPath.ForIO(e.Source), FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
			Stamp stamp = Stamp.Read(e.Source);
			if (!approvedSource.Same(stamp)) throw new ChangedException("Source changed after overwrite decision");
			long length = fileStream.Length;
			long num = 0L;
			// Tiny writes otherwise cause FileStream to allocate another 1 MiB
			// buffer per file. Keep large-file buffering and transfer chunks intact.
			int writeBufferSize = length < 1048576 ? 4096 : 1048576;
			e.CopyHash = null;
			e.CopyStamp = null;
			using SHA256 copyHash = SHA256.Create();
			using (FileStream fileStream2 = new FileStream(LongPath.ForIO(text), FileMode.CreateNew, FileAccess.Write, FileShare.None, writeBufferSize, FileOptions.SequentialScan))
			{
				byte[] array = Scratch;
				int num2;
				while ((num2 = fileStream.Read(array, 0, array.Length)) > 0)
				{
					Check(token);
					copyHash.TransformBlock(array, 0, num2, array, 0);
					fileStream2.Write(array, 0, num2);
					num += num2;
					transferred += num2;
					curCopyDone = num;
					Report(new Update
					{
						Phase = "Copying",
						Path = e.Source,
						Bytes = prior + num,
						TotalBytes = total,
						Done = done,
						Total = count,
						WorkBytes = workBase + Math.Min(num, e.Length),
						FileBytes = num,
						FileTotal = length
					});
				}
				fileStream2.Flush(flushToDisk: true);
			}
			if (num != length || !stamp.Same(Stamp.Read(e.Source)))
			{
				throw new ChangedException("Source changed while copying");
			}
			copyHash.TransformFinalBlock(new byte[0], 0, 0);
			string sourceHash = BitConverter.ToString(copyHash.Hash);
			File.SetLastWriteTimeUtc(LongPath.ForIO(text), stamp.Write);
			Check(token);
			BeforeCommit(e.Target);
			Check(token);
			if (!approvedSource.Same(Stamp.Read(e.Source)))
				throw new ChangedException("Source changed before commit; overwrite authorization no longer applies");
			CheckPath(e.Target, forWrite: true);
			if (!initial.Same(Stamp.Read(e.Target)))
			{
				throw new ChangedException("Destination changed during copy; existing destination left in place");
			}
			if (initial.Exists)
			{
				using (FileStream stream = new FileStream(LongPath.ForIO(e.Target), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1048576, FileOptions.SequentialScan))
				{
					if (HashStream(stream, token, (long a, long b) =>
					{
						Report(new Update
						{
							Phase = "Rechecking destination",
							Path = e.Target,
							Bytes = prior + e.Length,
							TotalBytes = total,
							Done = done,
							Total = count,
							WorkBytes = workBase + e.Length,
							FileBytes = a,
							FileTotal = b
						});
					}) != initialHash || !initial.Same(Stamp.Read(e.Target)))
					{
						throw new ChangedException("Destination changed before replacement; no overwrite performed");
					}
					backup = Path.Combine(Path.GetDirectoryName(e.Target), ".clearcopy-replaced-" + Guid.NewGuid().ToString("N") + ".bak");
					File.Replace(LongPath.ForIO(text), LongPath.ForIO(e.Target), LongPath.ForIO(backup));
				}
				if (ObservedHash(backup, "Checking previous version", prior + e.Length, total, done, count, workBase + e.Length, token) != initialHash)
				{
					throw new ChangedException("Concurrent replacement detected. Previous destination retained at " + backup);
				}
			}
			else
			{
				File.Move(LongPath.ForIO(text), LongPath.ForIO(e.Target));
			}
			if (runVerifies)
			{
				e.CopyHash = sourceHash;
				e.CopyStamp = stamp;
			}
		}
		finally
		{
			if (File.Exists(LongPath.ForIO(text)))
			{
				try
				{
					File.Delete(LongPath.ForIO(text));
				}
				catch (Exception ex)
				{
					WriteLog("TEMP CLEANUP WARNING " + text + " : " + ex.Message);
				}
			}
		}
	}

	public string Hash(string path, CancellationToken token)
	{
		CheckPath(path);
		BeforeRead(path);
		using FileStream stream = new FileStream(LongPath.ForIO(path), FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
		return HashStream(stream, token, (long a, long b) =>
		{
		});
	}

	private string ObservedHash(string path, string phase, long bytes, long total, int done, int count, long work, CancellationToken token)
	{
		CheckPath(path);
		BeforeRead(path);
		using FileStream stream = new FileStream(LongPath.ForIO(path), FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
		return HashStream(stream, token, (long value, long length) =>
		{
			Report(new Update
			{
				Phase = phase,
				Path = path,
				Bytes = bytes,
				TotalBytes = total,
				Done = done,
				Total = count,
				WorkBytes = work,
				FileBytes = value,
				FileTotal = length
			});
		});
	}

	private string HashStream(Stream stream, CancellationToken token, Action<long, long> progress, byte[] buffer = null)
	{
		using SHA256 sHA = SHA256.Create();
		byte[] array = buffer ?? Scratch;
		long num = 0L;
		progress(0L, stream.Length);
		int num2;
		while ((num2 = stream.Read(array, 0, array.Length)) > 0)
		{
			Check(token);
			sHA.TransformBlock(array, 0, num2, array, 0);
			num += num2;
			Interlocked.Add(ref readBytes, num2);
			progress(num, stream.Length);
		}
		sHA.TransformFinalBlock(new byte[0], 0, 0);
		return BitConverter.ToString(sHA.Hash);
	}

	// 0.4.4: when the source and destination are on different drives/shares,
	// both copies are read at the same time (one on a helper thread). Same
	// result as reading them one after the other, in about half the time.
	public bool ParallelVerify = true;

	public int ParallelVerifications;

	// Small files gain nothing from a second thread. Tests lower this and allow same-drive pairs.
	public long ParallelMinimumBytes = 4194304L;

	public bool ParallelOnSameStorage;

	private byte[] scratch2;

	public static bool SameStorage(string a, string b)
	{
		try
		{
			string ra = Path.GetPathRoot(Path.GetFullPath(a)).TrimEnd('\\');
			string rb = Path.GetPathRoot(Path.GetFullPath(b)).TrimEnd('\\');
			return string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return true;
		}
	}

	// 0.4.5: read the destination past the Windows cache during checks.
	public bool UncachedVerify = true;

	private bool uncachedFailed;

	// Counts of how checks were done (tests and the log).
	public int SourceRereads;

	public int DestinationOnlyChecks;

	public int UncachedReads;

	private bool lastCheckDestinationOnly;

	private Stream OpenForCheck(string path)
	{
		if (UncachedVerify && !uncachedFailed && UncachedReader.Supported)
		{
			try
			{
				UncachedReader reader = new UncachedReader(path);
				Interlocked.Increment(ref UncachedReads);
				return reader;
			}
			catch (IOException ex) when ((ex.HResult & 0xFFFF) == 87 || (ex.HResult & 0xFFFF) == 1)
			{
				// Some file systems refuse unbuffered reads; use normal reads for the rest of the job.
				uncachedFailed = true;
				WriteLog("VERIFY NOTE " + path + " : this location does not allow reads past the Windows cache (" + ex.Message.Trim() + "); using normal reads for the rest of the job");
			}
		}
		return new FileStream(LongPath.ForIO(path), FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
	}

	// progress(doneBytes, totalBytes, path, phase); doneBytes counts both files (0 .. 2 x length).
	private string PairHash(Entry e, CancellationToken token, Action<long, long, string, string> progress)
	{
		string copyHash = e.CopyHash;
		Stamp copyStamp = e.CopyStamp;
		e.CopyHash = null;
		e.CopyStamp = null;
		lastCheckDestinationOnly = false;
		if (copyHash != null && copyStamp != null)
		{
			// The source was hashed while it was copied, so only the destination is
			// read back. If the source changed since, fall through to reading both.
			Stamp now = Stamp.Read(e.Source);
			if (now.Same(copyStamp))
			{
				BeforeRead(e.Target);
				using Stream destination = OpenForCheck(e.Target);
				Stamp targetStamp = Stamp.Read(e.Target);
				long size = destination.Length;
				if (size != copyStamp.Length)
				{
					return null;
				}
				Interlocked.Increment(ref DestinationOnlyChecks);
				string destinationHash = HashStream(destination, token, (long v, long n) =>
				{
					progress(size + v, 2 * size, e.Target, "Checking destination copy");
				});
				if (!copyStamp.Same(Stamp.Read(e.Source)) || !targetStamp.Same(Stamp.Read(e.Target)))
				{
					throw new ChangedException("File changed during verification");
				}
				e.VerifiedSource = copyStamp;
				e.VerifiedTarget = targetStamp;
				lastCheckDestinationOnly = true;
				return (destinationHash == copyHash) ? copyHash : null;
			}
		}
		Interlocked.Increment(ref SourceRereads);
		BeforeRead(e.Source);
		BeforeRead(e.Target);
		using FileStream fileStream = new FileStream(LongPath.ForIO(e.Source), FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
		using Stream fileStream2 = OpenForCheck(e.Target);
		Stamp stamp = Stamp.Read(e.Source);
		Stamp stamp2 = Stamp.Read(e.Target);
		if (fileStream.Length != fileStream2.Length)
		{
			return null;
		}
		long length = fileStream.Length;
		string text;
		string text2;
		if (!ParallelVerify || length < ParallelMinimumBytes || (SameStorage(e.Source, e.Target) && !ParallelOnSameStorage))
		{
			text = HashStream(fileStream, token, (long v, long n) =>
			{
				progress(v, 2 * length, e.Source, "Hashing source");
			});
			text2 = HashStream(fileStream2, token, (long v, long n) =>
			{
				progress(length + v, 2 * length, e.Target, "Hashing destination");
			});
		}
		else
		{
			Interlocked.Increment(ref ParallelVerifications);
			long destinationDone = 0L;
			byte[] buffer = scratch2 ?? (scratch2 = new byte[1048576]);
			using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
			Task<string> destination = Task.Run(() => HashStream(fileStream2, stop.Token, (long v, long n) =>
			{
				Interlocked.Exchange(ref destinationDone, v);
			}, buffer));
			try
			{
				text = HashStream(fileStream, token, (long v, long n) =>
				{
					progress(v + Interlocked.Read(ref destinationDone), 2 * length, e.Source, "Verifying source and destination together");
				});
				while (!destination.Wait(200))
				{
					Check(token);
					progress(length + Interlocked.Read(ref destinationDone), 2 * length, e.Target, "Verifying source and destination together");
				}
				text2 = destination.GetAwaiter().GetResult();
			}
			catch
			{
				stop.Cancel();
				try
				{
					destination.Wait();
				}
				catch
				{
				}
				throw;
			}
		}
		if (!stamp.Same(Stamp.Read(e.Source)) || !stamp2.Same(Stamp.Read(e.Target)))
		{
			throw new ChangedException("File changed during verification");
		}
		e.VerifiedSource = stamp;
		e.VerifiedTarget = stamp2;
		return (text == text2) ? text : null;
	}

	private void ApplyOrCompareMetadata(Entry e, bool compare, Result r, CancellationToken token)
	{
		bool preserveDetails = PreserveDetails;
		bool preservePermissions = PreservePermissions;
		if (!preserveDetails && !preservePermissions)
		{
			return;
		}
		try
		{
			Check(token);
			CheckPath(e.Source);
			CheckPath(e.Target, forWrite: !compare);
			if (!Stamp.Read(e.Target).Exists)
			{
				return;
			}
			string text = null;
			if (preservePermissions)
			{
				text = (e.Directory ? Directory.GetAccessControl(LongPath.ForIO(e.Source), AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access) : File.GetAccessControl(LongPath.ForIO(e.Source), AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
			}
			if (compare)
			{
				Stamp stamp = Stamp.Read(e.Source);
				Stamp stamp2 = Stamp.Read(e.Target);
				bool flag = preserveDetails && (stamp.Write != stamp2.Write || stamp.Created != stamp2.Created || PortableAttributes(stamp.Attributes) != PortableAttributes(stamp2.Attributes));
				if (preservePermissions)
				{
					string text2 = (e.Directory ? Directory.GetAccessControl(LongPath.ForIO(e.Target), AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access) : File.GetAccessControl(LongPath.ForIO(e.Target), AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
					flag |= ComparableDacl(text) != ComparableDacl(text2);
				}
				if (flag)
				{
					r.MetadataDifferences++;
					Outcome(e, "Metadata difference", "Selected timestamp/attribute/access-permission policy differs; content result is separate");
				}
				return;
			}
			if (preserveDetails)
			{
				Stamp stamp3 = Stamp.Read(e.Source);
				if (e.Directory)
				{
					Directory.SetCreationTimeUtc(LongPath.ForIO(e.Target), stamp3.Created);
					Directory.SetLastWriteTimeUtc(LongPath.ForIO(e.Target), stamp3.Write);
				}
				else
				{
					File.SetCreationTimeUtc(LongPath.ForIO(e.Target), stamp3.Created);
					File.SetLastWriteTimeUtc(LongPath.ForIO(e.Target), stamp3.Write);
				}
				File.SetAttributes(LongPath.ForIO(e.Target), PortableAttributes(stamp3.Attributes));
			}
			if (preservePermissions)
			{
				if (e.Directory)
				{
					DirectorySecurity directorySecurity = new DirectorySecurity();
					directorySecurity.SetSecurityDescriptorSddlForm(text, AccessControlSections.Access);
					Directory.SetAccessControl(LongPath.ForIO(e.Target), directorySecurity);
				}
				else
				{
					FileSecurity fileSecurity = new FileSecurity();
					fileSecurity.SetSecurityDescriptorSddlForm(text, AccessControlSections.Access);
					File.SetAccessControl(LongPath.ForIO(e.Target), fileSecurity);
				}
			}
			if (e.VerifiedTarget != null)
			{
				e.VerifiedTarget = Stamp.Read(e.Target);
			}
			WriteLog("METADATA APPLIED " + e.Target + " | details=" + preserveDetails + " | DACL=" + preservePermissions + " | owner/audit rules/alternate streams not handled");
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex2)
		{
			r.Errors++;
			r.Unchecked++;
			Outcome(e, "Metadata error", Describe(ex2));
		}
	}

	// Windows sets the DACL auto-inherited control flags (AI, AR) whenever a DACL
	// is written through the current security APIs, so a copy of a source whose
	// DACL was written by older tools differs only in those flags. They do not
	// change who has access. The ACEs and the protected flag (P, inheritance
	// disabled) are still compared exactly.
	public static string ComparableDacl(string sddl)
	{
		if (string.IsNullOrEmpty(sddl) || !sddl.StartsWith("D:", StringComparison.Ordinal))
		{
			return sddl;
		}
		int open = sddl.IndexOf('(');
		string flags = (open < 0) ? sddl.Substring(2) : sddl.Substring(2, open - 2);
		string aces = (open < 0) ? "" : sddl.Substring(open);
		return "D:" + flags.Replace("AI", "").Replace("AR", "") + aces;
	}

	private static FileAttributes PortableAttributes(FileAttributes a)
	{
		FileAttributes fileAttributes = a & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed);
		if (fileAttributes != 0)
		{
			return fileAttributes;
		}
		return FileAttributes.Normal;
	}

	private void AuditVisit(string source, string target, Dictionary<string, Entry> dictionary, CancellationToken token, ref int visited, int done, int count, long total)
	{
		Check(token);
		if (visited++ % 256 == 0)
		{
			Report(new Update
			{
				Phase = "Rechecking source",
				Path = source,
				WorkBytes = workTotal,
				Bytes = total,
				TotalBytes = total,
				Done = done,
				Total = count
			});
		}
		Stamp stamp = null;
		string[] children = null;
		string error = null;
		try
		{
			stamp = Retry(() => Stamp.Read(source), token, source);
			if (!stamp.Exists)
			{
				throw new FileNotFoundException("Source disappeared during scanning", source);
			}
			if ((stamp.Attributes & FileAttributes.ReparsePoint) != 0)
			{
				throw new IOException("Link/junction skipped: " + source);
			}
			if (stamp.Directory)
			{
				children = Retry(() => Directory.GetFileSystemEntries(LongPath.ForIO(source)).Select(LongPath.ForDisplay).ToArray(), token, source);
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			error = ex.Message;
		}
		dictionary.TryGetValue(source, out var known);
		if (known != null)
		{
			known.SeenInAudit = true;
		}
		if (error != null)
		{
			currentResult.Unchecked++;
			currentResult.Errors++;
			Outcome(known ?? new Entry { Source = source, Target = target, Directory = stamp != null && stamp.Directory }, "Unchecked", "Final source rescan failed: " + error);
		}
		else if (known == null)
		{
			currentResult.Changed++;
			currentResult.Unchecked++;
			Outcome(new Entry { Source = source, Target = target, Directory = stamp.Directory, Length = stamp.Length, SourceState = stamp }, "Changed", "Added after initial scan; not included in this job");
		}
		else if (known.Directory != stamp.Directory || (!known.Directory && !known.SourceState.Same(stamp)))
		{
			currentResult.Changed++;
			Outcome(known, "Changed", "Source changed since initial scan");
		}
		if (children == null)
		{
			return;
		}
		foreach (string child in children)
		{
			AuditVisit(child, Path.Combine(target, Path.GetFileName(child)), dictionary, token, ref visited, done, count, total);
		}
	}

	private void Audit(List<Entry> plan, CancellationToken token, int done, int count, long total)
	{
		Dictionary<string, Entry> dictionary = plan.Where((Entry e) => e.SourceState != null).GroupBy((Entry e) => e.Source, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, Entry> g) => g.Key, (IGrouping<string, Entry> g) => g.First(), StringComparer.OrdinalIgnoreCase);
		if (plan is CopyPlan copyPlan)
		{
			// 0.4: walk the source tree and compare as we go, instead of building a
			// second full plan in memory. Same outcomes as before: unreadable items
			// are Unchecked, new items and changed files are Changed, and planned
			// items that are no longer there are reported as removed.
			currentResult.TreeRechecked = true;
			foreach (Entry value in dictionary.Values)
			{
				value.SeenInAudit = false;
			}
			int visited = 0;
			foreach (string root in copyPlan.Roots)
			{
				Check(token);
				string rootTarget = copyPlan.Contents ? copyPlan.Destination : Path.Combine(copyPlan.Destination, Path.GetFileName(root.TrimEnd('\\')));
				try
				{
					CheckPath(root);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					dictionary.TryGetValue(root, out var known);
					if (known != null)
					{
						known.SeenInAudit = true;
					}
					currentResult.Unchecked++;
					currentResult.Errors++;
					Outcome(new Entry { Source = root, Target = rootTarget, Directory = true }, "Unchecked", "Final source rescan failed: " + ex.Message);
					continue;
				}
				AuditVisit(root, rootTarget, dictionary, token, ref visited, done, count, total);
			}
			foreach (Entry item2 in dictionary.Values.Where((Entry e) => !e.SeenInAudit))
			{
				currentResult.Changed++;
				currentResult.Unchecked++;
				Outcome(item2, "Changed", "Source removed after initial scan");
			}
		}
		foreach (Entry item3 in plan.Where((Entry e) => e.VerifiedSource != null))
		{
			Check(token);
			try
			{
				if (!item3.VerifiedSource.Same(Stamp.Read(item3.Source)) || !item3.VerifiedTarget.Same(Stamp.Read(item3.Target)))
				{
					currentResult.Changed++;
					Outcome(item3, "Changed", "Source or destination changed after its hash verification");
				}
			}
			catch (Exception ex)
			{
				currentResult.Unchecked++;
				currentResult.Errors++;
				Outcome(item3, "Unchecked", "Final state check failed: " + ex.Message);
			}
		}
		Report(new Update
		{
			Phase = "Final checks complete",
			Path = "Rescan finished — this is not a filesystem snapshot",
			WorkBytes = workTotal,
			Bytes = total,
			TotalBytes = total,
			Done = done,
			Total = count
		});
	}
}
