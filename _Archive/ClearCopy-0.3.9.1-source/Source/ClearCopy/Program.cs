using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace ClearCopy;

public class Program
{
	[STAThread]
	public static void Main(string[] args)
	{
		RuntimeSetup.EnableLongPaths();
		if (args.Length == 2 && args[0] == "--helper-native-check")
		{
			PasteHelper.NativeCheck(args[1]);
			return;
		}
		if (args.Length == 2 && args[0] == "--chooser-check")
		{
			PasteHelper.ChooserCheck(args[1]);
			return;
		}
		if (args.Length == 1 && args[0] == "--install-machine")
		{
			try
			{
				SelfInstall.InstallFile(SelfInstall.CurrentPath, SelfInstall.MachinePath);
				return;
			}
			catch (Exception ex)
			{
				MessageBox.Show("Program Files installation failed: " + ex.Message, "ClearCopy installation");
				Environment.ExitCode = 1;
				return;
			}
		}
		if (args.Length == 1 && args[0] == "--paste-helper")
		{
			PasteHelper.Run();
			return;
		}
		if (args.Length == 3 && args[0] == "--diagnostic-test")
		{
			File.WriteAllText(args[2], Diagnostics.Run(args[1], CancellationToken.None));
			return;
		}
		if (args.Length == 3 && args[0] == "--network-browser-test")
		{
			try
			{
				NetworkFolderBrowser.SmokeTest(args[1], args[2]);
				return;
			}
			catch (Exception ex2)
			{
				File.WriteAllText(args[2], ex2.ToString());
				Environment.ExitCode = 1;
				return;
			}
		}
		if (args.Length == 4 && args[0] == "--folder-picker-test")
		{
			new Application();
			try
			{
				FolderPicker.SelectionTest(args[1], args[2], args[3]);
				return;
			}
			catch (Exception ex3)
			{
				File.WriteAllText(args[3] + ".txt", ex3.ToString());
				Environment.ExitCode = 1;
				return;
			}
		}
		if (args.Length == 3 && args[0] == "--install-paste-menu")
		{
			try
			{
				string text = PasteIntegration.Install(args[1], args[2]);
				File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[2]), "Paste-menu-install.txt"), "Installed ClearCopy in TeraCopy's paste chooser.\r\nBackup: " + text);
				return;
			}
			catch (Exception ex4)
			{
				MessageBox.Show(ex4.Message, "ClearCopy menu setup");
				Environment.ExitCode = 1;
				return;
			}
		}
		Application application = new Application();
		application.Run(new MainWindow(args));
	}
}
