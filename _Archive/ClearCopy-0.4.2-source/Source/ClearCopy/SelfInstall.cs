using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ClearCopy;

public static class SelfInstall
{
	public static string UserPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ClearCopy", "ClearCopy.exe");

	public static string MachinePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClearCopy", "ClearCopy.exe");

	public static string InstalledPath
	{
		get
		{
			if (!File.Exists(MachinePath))
			{
				return UserPath;
			}
			return MachinePath;
		}
	}

	public static string CurrentPath => Assembly.GetExecutingAssembly().Location;

	public static string IntegrationPath
	{
		get
		{
			if (!File.Exists(InstalledPath))
			{
				return CurrentPath;
			}
			return InstalledPath;
		}
	}

	// 0.4.2: missing version parts count as 0, so "0.4.1" and "0.4.1.0" are equal.
	public static Version Normalize(Version v)
	{
		return v == null ? new Version(0, 0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
	}

	public static string SetupButton(bool exists, Version installed, Version available, bool complete)
	{
		if (!exists)
		{
			return "Install";
		}
		installed = Normalize(installed);
		available = Normalize(available);
		if (available > installed)
		{
			return "Update";
		}
		if (!complete)
		{
			return "Finish setup";
		}
		return null;
	}

	public static void CompleteSetup(Action install, Action shortcut, Action integration, Action helper, Action commit, Action<string> progress)
	{
		progress("Installing ClearCopy…");
		install();
		progress("Creating Start-menu shortcut…");
		shortcut();
		progress("Connecting Explorer…");
		integration();
		progress("Starting the paste chooser…");
		helper();
		commit();
		progress("Installed. ClearCopy's main window can close; the paste chooser runs at sign-in.");
	}

	public static void InstallFile(string source, string target)
	{
		source = Path.GetFullPath(source);
		target = Path.GetFullPath(target);
		if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		Directory.CreateDirectory(Path.GetDirectoryName(target));
		string text = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			using (FileStream fileStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				using FileStream fileStream2 = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None);
				fileStream.CopyTo(fileStream2);
				fileStream2.Flush(flushToDisk: true);
			}
			using (SHA256 sHA = SHA256.Create())
			{
				using FileStream inputStream = File.OpenRead(source);
				using FileStream inputStream2 = File.OpenRead(text);
				if (!sHA.ComputeHash(inputStream).SequenceEqual(sHA.ComputeHash(inputStream2)))
				{
					throw new IOException("Installed executable verification failed.");
				}
			}
			if (File.Exists(target))
			{
				File.Replace(text, target, null);
			}
			else
			{
				File.Move(text, target);
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

	public static void CreateShortcut()
	{
		string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ClearCopy.lnk");
		object obj = null;
		object obj2 = null;
		try
		{
			Type typeFromProgID = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true);
			obj = Activator.CreateInstance(typeFromProgID);
			obj2 = typeFromProgID.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, obj, new object[1] { text });
			Type type = obj2.GetType();
			type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, obj2, new object[1] { InstalledPath });
			type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, obj2, new object[1] { Path.GetDirectoryName(InstalledPath) });
			type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, obj2, new object[1] { InstalledPath + ",0" });
			type.InvokeMember("Description", BindingFlags.SetProperty, null, obj2, new object[1] { "ClearCopy — copy and compare files" });
			type.InvokeMember("Save", BindingFlags.InvokeMethod, null, obj2, null);
		}
		finally
		{
			if (obj2 != null && Marshal.IsComObject(obj2))
			{
				Marshal.FinalReleaseComObject(obj2);
			}
			if (obj != null && Marshal.IsComObject(obj))
			{
				Marshal.FinalReleaseComObject(obj);
			}
		}
	}
}
