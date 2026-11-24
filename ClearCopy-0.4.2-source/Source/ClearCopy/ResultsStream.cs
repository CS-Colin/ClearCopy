using System;
using System.IO;
using System.Text;

namespace ClearCopy;

// 0.4: writes every result row to <log>.results.csv as the job runs, so a job
// with hundreds of thousands of files does not need to keep them all in memory.
public sealed class ResultsStream : IDisposable
{
	private readonly object sync = new object();
	private readonly string path;
	private StreamWriter writer;

	public string Path => path;

	public long Rows { get; private set; }

	public string Warning { get; private set; }

	public bool Failed => Warning != null;

	public ResultsStream(string path)
	{
		this.path = path;
		Open();
	}

	private void Open()
	{
		try
		{
			FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 65536);
			writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 65536);
			writer.WriteLine(Jobs.CsvHeader);
			Rows = 0;
		}
		catch (Exception ex)
		{
			Fail(ex);
		}
	}

	public void Write(ItemResult row)
	{
		lock (sync)
		{
			if (writer == null)
			{
				return;
			}
			try
			{
				writer.WriteLine(Jobs.CsvLine(row));
				Rows++;
			}
			catch (Exception ex)
			{
				Fail(ex);
			}
		}
	}

	// The sync flow restarts the list before its final comparison.
	public void Reset()
	{
		lock (sync)
		{
			if (Failed)
			{
				return;
			}
			Close();
			Open();
		}
	}

	public void Flush()
	{
		lock (sync)
		{
			try
			{
				writer?.Flush();
			}
			catch (Exception ex)
			{
				Fail(ex);
			}
		}
	}

	private void Fail(Exception ex)
	{
		if (Warning == null)
		{
			Warning = "Full results list incomplete: " + ex.Message;
		}
		try
		{
			writer?.Dispose();
		}
		catch
		{
		}
		writer = null;
	}

	private void Close()
	{
		if (writer == null)
		{
			return;
		}
		try
		{
			writer.Flush();
			writer.Dispose();
		}
		catch (Exception ex)
		{
			Fail(ex);
		}
		writer = null;
	}

	public void Dispose()
	{
		lock (sync)
		{
			Close();
		}
	}
}
