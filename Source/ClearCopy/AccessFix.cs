using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace ClearCopy;

// 0.4.1: "Fix access". For source folders/files the account running ClearCopy
// cannot read (typically data from an old server whose permissions do not
// include this computer's administrators), take ownership and add one
// "read" entry for that account - the same change Explorer's "Continue"
// button makes - then let the job pick them up. The original permissions of
// every changed item are written to a file first.
public static class AccessFix
{
	public sealed class Outcome
	{
		public readonly List<string> Fixed = new List<string>();

		public readonly Dictionary<string, string> Failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		public int Changed;

		public bool Declined;

		public string ChangesFile;
	}

	public static readonly SecurityIdentifier Administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

	public static bool IsAccessDenied(Exception ex)
	{
		for (Exception e = ex; e != null; e = e.InnerException)
		{
			if (e is UnauthorizedAccessException || (e.HResult & 0xFFFF) == 5)
			{
				return true;
			}
		}
		return false;
	}

	public static string AccountName()
	{
		try
		{
			return WindowsIdentity.GetCurrent().Name;
		}
		catch
		{
			return Environment.UserName;
		}
	}

	public static bool IsElevated()
	{
		try
		{
			using WindowsIdentity identity = WindowsIdentity.GetCurrent();
			return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
		}
		catch
		{
			return false;
		}
	}

	// Can this account read the item? Folders: list them. Files: open for reading.
	// Only "access denied" counts; other errors (missing, locked, offline) do not.
	public static bool Denied(string path)
	{
		try
		{
			string io = LongPath.ForIO(path);
			if (Directory.Exists(io))
			{
				Directory.EnumerateFileSystemEntries(io).Take(1).Count();
			}
			else
			{
				using (new FileStream(io, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1))
				{
				}
			}
			return false;
		}
		catch (Exception ex)
		{
			return IsAccessDenied(ex);
		}
	}

	// Why the destination may refuse an item, or null when nothing is found.
	// Read-only check plus an open for writing (no data is changed).
	public static string DestinationProblem(string target)
	{
		if (string.IsNullOrEmpty(target))
		{
			return null;
		}
		try
		{
			string io = LongPath.ForIO(target);
			if (!File.Exists(io))
			{
				return null;
			}
			if ((File.GetAttributes(io) & FileAttributes.ReadOnly) != 0)
			{
				return "the destination file is read-only";
			}
			using (new FileStream(io, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1))
			{
			}
			return null;
		}
		catch (Exception ex)
		{
			return IsAccessDenied(ex) ? "the destination refuses changes to this file (NAS permissions or the file is open elsewhere)" : null;
		}
	}

	public static bool OnNetwork(string path)
	{
		try
		{
			if (path.StartsWith("\\\\", StringComparison.Ordinal))
			{
				return true;
			}
			return new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.Network;
		}
		catch
		{
			return true;
		}
	}

	// Drops paths that sit inside another listed folder.
	public static List<string> TopMost(IEnumerable<string> paths)
	{
		List<string> sorted = paths.Where((string p) => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string p) => p.Length).ToList();
		List<string> kept = new List<string>();
		foreach (string p in sorted)
		{
			if (!kept.Any((string k) => p.StartsWith(k.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
			{
				kept.Add(p);
			}
		}
		return kept;
	}

	// Runs the fix. When ClearCopy is not elevated, a second copy of ClearCopy is
	// started "as administrator" (one Windows Yes/No prompt for the whole list).
	public static Outcome Run(IList<string> paths, string changesFile, bool inProcess, Action<int, int, string> progress = null)
	{
		SecurityIdentifier grantee = WindowsIdentity.GetCurrent().User;
		if (inProcess || IsElevated())
		{
			return Apply(paths, grantee, changesFile, CancellationToken.None, progress);
		}
		string request = Path.Combine(Path.GetTempPath(), "ClearCopy-fix-access-" + Guid.NewGuid().ToString("N") + ".txt");
		string reply = request + ".result";
		List<string> lines = new List<string> { "SID=" + grantee.Value, "SAVE=" + changesFile };
		lines.AddRange(paths.Select((string p) => "PATH=" + p));
		File.WriteAllLines(request, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		Outcome outcome = new Outcome { ChangesFile = changesFile };
		try
		{
			using Process process = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--fix-access \"" + request + "\"")
			{
				UseShellExecute = true,
				Verb = "runas"
			});
			// The helper writes "index<TAB>count<TAB>path" to <request>.progress.
			while (!process.WaitForExit(300))
			{
				ReadProgress(request + ".progress", progress);
			}
			ReadProgress(request + ".progress", progress);
		}
		catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
		{
			outcome.Declined = true;
			foreach (string p in paths)
			{
				outcome.Failed[p] = "Administrator permission was not given";
			}
			return outcome;
		}
		finally
		{
			try
			{
				File.Delete(request);
				File.Delete(request + ".progress");
			}
			catch
			{
			}
		}
		if (!File.Exists(reply))
		{
			foreach (string p in paths)
			{
				outcome.Failed[p] = "The administrator helper did not report back";
			}
			return outcome;
		}
		foreach (string line in File.ReadAllLines(reply, Encoding.UTF8))
		{
			string[] parts = line.Split('\t');
			if (parts[0] == "OK" && parts.Length >= 2)
			{
				outcome.Fixed.Add(parts[1]);
			}
			else if (parts[0] == "FAIL" && parts.Length >= 3)
			{
				outcome.Failed[parts[1]] = parts[2];
			}
			else if (parts[0] == "CHANGED" && parts.Length >= 2)
			{
				int.TryParse(parts[1], out outcome.Changed);
			}
		}
		foreach (string p in paths)
		{
			if (!outcome.Fixed.Contains(p, StringComparer.OrdinalIgnoreCase) && !outcome.Failed.ContainsKey(p))
			{
				outcome.Failed[p] = "The administrator helper could not finish; see the changes file";
			}
		}
		try
		{
			File.Delete(reply);
		}
		catch
		{
		}
		return outcome;
	}

	private static void ReadProgress(string file, Action<int, int, string> progress)
	{
		if (progress == null)
		{
			return;
		}
		try
		{
			using FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
			string[] parts = (reader.ReadLine() ?? "").Split('\t');
			if (parts.Length >= 3 && int.TryParse(parts[0], out var index) && int.TryParse(parts[1], out var count))
			{
				progress(index, count, parts[2]);
			}
		}
		catch
		{
		}
	}

	// Entry point of the elevated helper: ClearCopy.exe --fix-access <request file>
	public static int Helper(string request)
	{
		string[] lines = File.ReadAllLines(request, Encoding.UTF8);
		SecurityIdentifier grantee = new SecurityIdentifier(lines.First((string l) => l.StartsWith("SID=")).Substring(4));
		string save = lines.First((string l) => l.StartsWith("SAVE=")).Substring(5);
		List<string> paths = lines.Where((string l) => l.StartsWith("PATH=")).Select((string l) => l.Substring(5)).ToList();
		DateTime last = DateTime.MinValue;
		Outcome outcome = Apply(paths, grantee, save, CancellationToken.None, (int index, int count, string path) =>
		{
			if ((DateTime.UtcNow - last).TotalMilliseconds < 250.0)
			{
				return;
			}
			last = DateTime.UtcNow;
			try
			{
				File.WriteAllText(request + ".progress", index + "\t" + count + "\t" + path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			}
			catch
			{
			}
		});
		List<string> reply = new List<string> { "CHANGED\t" + outcome.Changed };
		reply.AddRange(outcome.Fixed.Select((string p) => "OK\t" + p));
		reply.AddRange(outcome.Failed.Select((KeyValuePair<string, string> f) => "FAIL\t" + f.Key + "\t" + f.Value.Replace('\t', ' ').Replace("\r", " ").Replace("\n", " ")));
		File.WriteAllLines(request + ".result", reply, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		return 0;
	}

	public static Outcome Apply(IEnumerable<string> paths, SecurityIdentifier grantee, string changesFile, CancellationToken token, Action<int, int, string> progress = null)
	{
		List<string> list = paths.ToList();
		int index = 0;
		Action<string> tick = (string current) => progress?.Invoke(index, list.Count, current);
		Outcome outcome = new Outcome { ChangesFile = changesFile };
		EnablePrivilege("SeTakeOwnershipPrivilege");
		EnablePrivilege("SeRestorePrivilege");
		EnablePrivilege("SeBackupPrivilege");
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(changesFile)));
		bool newFile = !File.Exists(changesFile);
		using StreamWriter changes = new StreamWriter(changesFile, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
		if (newFile)
		{
			changes.WriteLine("ClearCopy - access changes (Fix access)");
			changes.WriteLine("Each entry: the item, its owner and permissions BEFORE the change (SDDL), and what was added.");
			changes.WriteLine("The original owner can only be recorded when Windows allowed it to be read.");
		}
		changes.WriteLine();
		changes.WriteLine("=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " on " + Environment.MachineName + "; read access added for " + Name(grantee));
		foreach (string path in list)
		{
			token.ThrowIfCancellationRequested();
			index++;
			tick(path);
			try
			{
				Fix(path, grantee, changes, outcome, token, tick);
				if (Denied(path))
				{
					throw new UnauthorizedAccessException("Still not readable after the change");
				}
				outcome.Fixed.Add(path);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				outcome.Failed[path] = ex.Message;
				changes.WriteLine("FAILED " + path + " : " + ex.Message);
			}
			changes.Flush();
		}
		return outcome;
	}

	private static void Fix(string path, SecurityIdentifier grantee, StreamWriter changes, Outcome outcome, CancellationToken token, Action<string> tick)
	{
		token.ThrowIfCancellationRequested();
		tick(path);
		string io = LongPath.ForIO(path);
		bool directory = Directory.Exists(io);
		string owner = null;
		string dacl;
		FileSystemSecurity security;
		try
		{
			security = Read(io, directory, AccessControlSections.Owner | AccessControlSections.Access);
			owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
		}
		catch (Exception ex) when (IsAccessDenied(ex))
		{
			// Take ownership (the owner may always read and change permissions).
			FileSystemSecurity ownerOnly = directory ? new DirectorySecurity() : new FileSecurity();
			ownerOnly.SetOwner(IsElevated() ? Administrators : grantee);
			Write(io, directory, ownerOnly);
			security = Read(io, directory, AccessControlSections.Access);
		}
		dacl = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
		changes.WriteLine((directory ? "FOLDER " : "FILE ") + path);
		changes.WriteLine("  owner before: " + (owner ?? "not readable (ownership taken)"));
		changes.WriteLine("  permissions before: " + dacl);
		FileSystemSecurity grant = Read(io, directory, AccessControlSections.Access);
		grant.AddAccessRule(new FileSystemAccessRule(grantee, FileSystemRights.ReadAndExecute, directory ? (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) : InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
		Write(io, directory, grant);
		changes.WriteLine("  added: read" + (directory ? " (this folder, subfolders and files that inherit)" : "") + " for " + Name(grantee));
		outcome.Changed++;
		if (!directory)
		{
			return;
		}
		// Children that do not inherit (protected permissions) may still be locked.
		foreach (string child in Directory.GetFileSystemEntries(io).Select(LongPath.ForDisplay))
		{
			token.ThrowIfCancellationRequested();
			if (Denied(child) || NeedsGrant(child, grantee))
			{
				Fix(child, grantee, changes, outcome, token, tick);
			}
			else if (Directory.Exists(LongPath.ForIO(child)))
			{
				Check(child, grantee, changes, outcome, token, tick);
			}
		}
	}

	private static void Check(string folder, SecurityIdentifier grantee, StreamWriter changes, Outcome outcome, CancellationToken token, Action<string> tick)
	{
		tick(folder);
		foreach (string child in Directory.GetFileSystemEntries(LongPath.ForIO(folder)).Select(LongPath.ForDisplay))
		{
			token.ThrowIfCancellationRequested();
			if (Denied(child) || NeedsGrant(child, grantee))
			{
				Fix(child, grantee, changes, outcome, token, tick);
			}
			else if (Directory.Exists(LongPath.ForIO(child)) && (File.GetAttributes(LongPath.ForIO(child)) & FileAttributes.ReparsePoint) == 0)
			{
				Check(child, grantee, changes, outcome, token, tick);
			}
		}
	}

	// Inside a fixed folder, an item that does not inherit permissions (protected)
	// will not receive the folder's new read entry. The helper may be able to read
	// it as administrator while the normal ClearCopy window still can't, so such
	// items are given the entry explicitly.
	private static bool NeedsGrant(string path, SecurityIdentifier grantee)
	{
		try
		{
			string io = LongPath.ForIO(path);
			bool directory = Directory.Exists(io);
			FileSystemSecurity security = Read(io, directory, AccessControlSections.Access);
			if (!security.AreAccessRulesProtected)
			{
				return false;
			}
			foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
			{
				if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Equals(grantee) && (rule.FileSystemRights & FileSystemRights.ReadData) == FileSystemRights.ReadData)
				{
					return false;
				}
			}
			return true;
		}
		catch
		{
			return true;
		}
	}

	private static FileSystemSecurity Read(string io, bool directory, AccessControlSections sections)
	{
		return directory ? (FileSystemSecurity)Directory.GetAccessControl(io, sections) : File.GetAccessControl(io, sections);
	}

	private static void Write(string io, bool directory, FileSystemSecurity security)
	{
		if (directory)
		{
			Directory.SetAccessControl(io, (DirectorySecurity)security);
		}
		else
		{
			File.SetAccessControl(io, (FileSecurity)security);
		}
	}

	private static string Name(SecurityIdentifier sid)
	{
		try
		{
			return ((NTAccount)sid.Translate(typeof(NTAccount))).Value + " (" + sid.Value + ")";
		}
		catch
		{
			return sid.Value;
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Luid
	{
		public uint Low;

		public int High;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct TokenPrivileges
	{
		public uint Count;

		public Luid Luid;

		public uint Attributes;
	}

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

	[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool LookupPrivilegeValue(string system, string name, out Luid luid);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returnLength);

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetCurrentProcess();

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(IntPtr handle);

	// Best effort: only an elevated administrator holds these privileges.
	private static void EnablePrivilege(string name)
	{
		IntPtr token = IntPtr.Zero;
		try
		{
			if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out token) || !LookupPrivilegeValue(null, name, out var luid))
			{
				return;
			}
			TokenPrivileges state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
			AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
		}
		catch
		{
		}
		finally
		{
			if (token != IntPtr.Zero)
			{
				CloseHandle(token);
			}
		}
	}
}
