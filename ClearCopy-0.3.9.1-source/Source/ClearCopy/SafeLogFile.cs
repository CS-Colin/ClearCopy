using System;
using System.IO;
using System.Threading;

namespace ClearCopy;

public sealed class SafeLogFile : IDisposable
{
	private TextWriter writer;
	private readonly object sync = new object();
	private readonly Timer flushTimer;
	private int pendingLines;
	private int pendingCharacters;

	public bool Failed { get; private set; }

	public string Warning { get; private set; }

	public SafeLogFile(TextWriter target)
	{
		writer = target;
		if (target != null) flushTimer = new Timer(_ => Flush(), null, 1000, 1000);
	}

	public static SafeLogFile Open(string path)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			return new SafeLogFile(new StreamWriter(path));
		}
		catch (Exception ex)
		{
			SafeLogFile safeLogFile = new SafeLogFile(null);
			safeLogFile.Fail(ex);
			return safeLogFile;
		}
	}

	private void Fail(Exception ex)
	{
		Failed = true;
		Warning = "Disk log incomplete: " + ex.Message;
	}

	public void WriteLine(string line)
	{
		lock (sync)
		{
		if (writer == null)
		{
			return;
		}
		try
		{
			writer.WriteLine(line);
			pendingLines++;
			pendingCharacters += (line ?? "").Length;
			if (pendingLines >= 64 || pendingCharacters >= 65536 || line != null && (line.Contains("ERROR ") || line.Contains("CANCELLED") || line.Contains("RESULT ")))
				FlushCore();
		}
		catch (Exception ex)
		{
			Fail(ex);
			try
			{
				writer.Dispose();
			}
			catch
			{
			}
			writer = null;
		}
		}
	}

	private void FlushCore()
	{
		if (writer == null || pendingLines == 0) return;
		writer.Flush();
		pendingLines = pendingCharacters = 0;
	}

	public void Flush()
	{
		lock (sync)
		{
			try { FlushCore(); }
			catch (Exception ex)
			{
				Fail(ex);
				try { writer?.Dispose(); } catch { }
				writer = null;
			}
		}
	}

	public void Dispose()
	{
		flushTimer?.Dispose();
		lock (sync)
		{
		if (writer == null)
		{
			return;
		}
		try
		{
			writer.Dispose();
		}
		catch (Exception ex)
		{
			Fail(ex);
		}
		finally
		{
			writer = null;
		}
		}
	}
}
