using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace ClearCopy;

internal enum PasteChoice
{
	Cancel,
	Windows,
	ClearCopy
}

internal sealed class PastePreference
{
	private const string ValueName = "RememberedPasteChoice";
	internal static readonly PastePreference Current = new PastePreference(@"Software\ClearCopy");
	private readonly string registryPath;

	internal PastePreference(string registryPath)
	{
		this.registryPath = registryPath;
	}

	internal PasteChoice? Read()
	{
		using RegistryKey key = Registry.CurrentUser.OpenSubKey(registryPath);
		return key?.GetValue(ValueName) switch
		{
			"Windows" => PasteChoice.Windows,
			"ClearCopy" => PasteChoice.ClearCopy,
			_ => null
		};
	}

	internal void Reset()
	{
		using RegistryKey key = Registry.CurrentUser.OpenSubKey(registryPath, writable: true);
		key?.DeleteValue(ValueName, throwOnMissingValue: false);
	}

	internal PasteChoice Resolve(Func<(PasteChoice Choice, bool Remember)> ask, Action<string> report)
	{
		try
		{
			PasteChoice? remembered = Read();
			if (remembered.HasValue)
			{
				return remembered.Value;
			}
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
		{
			report("Could not read the remembered paste choice: " + ex.Message);
		}

		(PasteChoice choice, bool remember) = ask();
		if (remember && (choice == PasteChoice.Windows || choice == PasteChoice.ClearCopy))
		{
			try
			{
				using RegistryKey key = Registry.CurrentUser.CreateSubKey(registryPath);
				key.SetValue(ValueName, choice.ToString(), RegistryValueKind.String);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
			{
				report("Could not save the remembered paste choice: " + ex.Message);
			}
		}
		return choice;
	}
}
