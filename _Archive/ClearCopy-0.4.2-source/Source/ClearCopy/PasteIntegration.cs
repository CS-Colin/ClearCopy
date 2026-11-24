using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ClearCopy;

public static class PasteIntegration
{
	public static string[] ReadList(string path)
	{
		byte[] array = File.ReadAllBytes(path);
		string text;
		using (StreamReader streamReader = new StreamReader(new MemoryStream(array), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true))
		{
			try
			{
				text = streamReader.ReadToEnd();
			}
			catch (DecoderFallbackException)
			{
				text = Encoding.Default.GetString(array);
			}
		}
		if (text.IndexOf('\0') >= 0 && array.Length > 1)
		{
			text = ((array[1] == 0) ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(array);
		}
		string[] array2 = (from s in text.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
			select s.Trim().Trim('"') into s
			where s.Length > 0
			select s).ToArray();
		if (array2.Length == 0)
		{
			throw new IOException("The paste chooser supplied an empty source list.");
		}
		if (array2.Any((string s) => !Path.IsPathRooted(s) || s.IndexOf('\0') >= 0))
		{
			throw new IOException("The paste chooser supplied an invalid source path.");
		}
		return array2;
	}

	public static string MenuText(string original, string executable)
	{
		if (executable.IndexOfAny(new char[3] { '\r', '\n', '"' }) >= 0)
		{
			throw new ArgumentException("Invalid executable path.");
		}
		string[] source = Regex.Split(original, "(?m)(?=^\\[\\d+\\]\\s*$)");
		string text = string.Concat(source.Where((string b) => !Regex.IsMatch(b, "(?im)^title=ClearCopy\\s*$")));
		int num = 0;
		foreach (Match item in Regex.Matches(text, "(?m)^\\[(\\d+)\\]"))
		{
			num = Math.Max(num, int.Parse(item.Groups[1].Value));
		}
		return text.TrimEnd('\r', '\n') + "\r\n[" + (num + 1) + "]\r\ntitle=ClearCopy\r\npath=" + executable + "\r\ncopy=\r\ncut=\r\nparameters=--from-list \"{list}\" \"{target}\\.\" --start\r\n";
	}

	public static string Install(string config, string executable)
	{
		if (!File.Exists(config))
		{
			throw new IOException("TeraCopy's PasteMenu.ini was not found. Use Explorer copy/paste once with TeraCopy first.");
		}
		string text;
		Encoding currentEncoding;
		using (StreamReader streamReader = new StreamReader(config, Encoding.Default, detectEncodingFromByteOrderMarks: true))
		{
			text = streamReader.ReadToEnd();
			currentEncoding = streamReader.CurrentEncoding;
		}
		string text2 = MenuText(text, executable);
		if (text2 == text)
		{
			return null;
		}
		return Save(config, text2, currentEncoding);
	}

	public static void Remove(string config)
	{
		if (File.Exists(config))
		{
			string text;
			Encoding currentEncoding;
			using (StreamReader streamReader = new StreamReader(config, Encoding.Default, detectEncodingFromByteOrderMarks: true))
			{
				text = streamReader.ReadToEnd();
				currentEncoding = streamReader.CurrentEncoding;
			}
			string text2 = string.Concat(from b in Regex.Split(text, "(?m)(?=^\\[\\d+\\]\\s*$)")
				where !Regex.IsMatch(b, "(?im)^title=ClearCopy\\s*$")
				select b);
			if (text2 != text)
			{
				Save(config, text2, currentEncoding);
			}
		}
	}

	private static string Save(string config, string text, Encoding encoding)
	{
		string text2 = config + ".clearcopy-" + Guid.NewGuid().ToString("N") + ".bak";
		string text3 = config + ".clearcopy-" + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.WriteAllText(text3, text, encoding);
			File.Replace(text3, config, text2);
			return text2;
		}
		finally
		{
			if (File.Exists(text3))
			{
				File.Delete(text3);
			}
		}
	}
}
