using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClearCopy;

public sealed class Preflight
{
	public int Files;

	public int Directories;

	public int Existing;

	public int WillCopy;

	public int WillSkip;

	public int Unknown;

	public long SourceBytes;

	public long EstimatedBytes;

	public ulong? FreeBytes;

	public static Preflight Inspect(List<Entry> plan, ExistingRule rule, CancellationToken token, Action<string> progress)
	{
		return Inspect(plan, rule, token, progress, TimeSpan.Zero);
	}

	public static Preflight Inspect(List<Entry> plan, ExistingRule rule, CancellationToken token, Action<string> progress, TimeSpan skipTolerance)
	{
		Preflight preflight = new Preflight();
		foreach (Entry item in plan)
		{
			token.ThrowIfCancellationRequested();
			progress(item.Target);
			if (item.ScanError != null)
			{
				preflight.Unknown++;
				continue;
			}
			if (item.Directory)
			{
				preflight.Directories++;
				continue;
			}
			preflight.Files++;
			preflight.SourceBytes += item.Length;
			try
			{
				Stamp stamp = Stamp.Read(item.Target);
				if (stamp.Exists)
				{
					preflight.Existing++;
				}
				bool flag = stamp.Exists && CopyEngine.SameForSkip(item.Length, item.SourceState.Write, stamp.Length, stamp.Write, skipTolerance);
				bool flag2 = stamp.Exists && stamp.Write > item.SourceState.Write;
				if (flag2 || (rule != ExistingRule.Replace && flag))
				{
					preflight.WillSkip++;
					continue;
				}
				preflight.WillCopy++;
				preflight.EstimatedBytes += item.Length;
			}
			catch
			{
				preflight.Unknown++;
			}
		}
		if (plan.Count > 0)
		{
			try
			{
				string directoryName = Path.GetDirectoryName(plan[0].Target);
				while (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(LongPath.ForIO(directoryName)))
				{
					directoryName = Path.GetDirectoryName(directoryName);
				}
				if (!string.IsNullOrEmpty(directoryName) && GetDiskFreeSpaceEx(LongPath.ForIO(directoryName), out var available, out var _, out var _))
				{
					preflight.FreeBytes = available;
				}
			}
			catch
			{
			}
		}
		return preflight;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);
}
