using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ClearCopy;

// Last line of defence for unexpected errors. Writes a crash report next to the
// logs, asks the open window to save its job checkpoint (marked Stopped, so it
// can be resumed) and flush its log, tells the user where the report is, and
// then closes. Errors inside a running copy are already handled by the job
// itself; this catches anything that would otherwise close ClearCopy silently.
public static class CrashHandler
{
	public static bool Interactive = true;

	public static string LogDirectory;

	private static int handling;

	public static void Install(Application application)
	{
		AppDomain.CurrentDomain.UnhandledException += (object s, UnhandledExceptionEventArgs e) =>
		{
			Handle(e.ExceptionObject as Exception, "background thread", exit: true);
		};
		application.DispatcherUnhandledException += (object s, DispatcherUnhandledExceptionEventArgs e) =>
		{
			e.Handled = true;
			Handle(e.Exception, "window", exit: true);
		};
		TaskScheduler.UnobservedTaskException += (object s, UnobservedTaskExceptionEventArgs e) =>
		{
			// Not fatal: record it and carry on.
			e.SetObserved();
			Record(e.Exception, "background task (not fatal)");
		};
	}

	// Returns the crash report path (or null). exit: false is for tests.
	public static string Handle(Exception ex, string source, bool exit)
	{
		if (Interlocked.Exchange(ref handling, 1) == 1)
		{
			return null;
		}
		string report = null;
		try
		{
			report = Record(ex, source);
			string reason = "ClearCopy closed unexpectedly (" + source + "): " + (ex?.Message ?? "unknown error");
			try
			{
				MainWindow.Current?.EmergencySave(reason, report);
			}
			catch
			{
			}
			if (Interactive)
			{
				try
				{
					MessageBox.Show("ClearCopy hit an unexpected error and has to close.\n\n" + (ex?.Message ?? "Unknown error") + "\n\nIf a job was running, its progress was saved: load the job (or start the same copy again) to resume.\n\nCrash report:\n" + (report ?? "could not be written"), "ClearCopy", MessageBoxButton.OK, MessageBoxImage.Error);
				}
				catch
				{
				}
			}
		}
		finally
		{
			if (exit)
			{
				Environment.Exit(1);
			}
			Interlocked.Exchange(ref handling, 0);
		}
		return report;
	}

	public static string Record(Exception ex, string source)
	{
		try
		{
			string folder = LogDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearCopy", "Logs");
			Directory.CreateDirectory(folder);
			string path = Path.Combine(folder, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".txt");
			StringBuilder text = new StringBuilder();
			text.AppendLine("ClearCopy crash report");
			text.AppendLine("Time:    " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
			text.AppendLine("Version: " + Assembly.GetExecutingAssembly().GetName().Version);
			text.AppendLine("Machine: " + Environment.MachineName + " (" + Environment.OSVersion + ", .NET " + Environment.Version + ")");
			text.AppendLine("Source:  " + source);
			text.AppendLine();
			text.AppendLine(ex?.ToString() ?? "No exception details.");
			File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
			return path;
		}
		catch
		{
			return null;
		}
	}
}
