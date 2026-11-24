using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace ClearCopy;

public static class Diagnostics
{
	public static string Run(string parent, CancellationToken token)
	{
		string text = Path.Combine(Path.GetFullPath(parent), "ClearCopy-disposable-test-" + Guid.NewGuid().ToString("N"));
		StringBuilder report = new StringBuilder();
		int passed = 0;
		Action<bool, string> action = (bool ok, string name) =>
		{
			report.AppendLine((ok ? "PASS " : "FAIL ") + name);
			if (ok)
			{
				passed++;
			}
		};
		try
		{
			CopyEngine.SafePath(parent);
			Directory.CreateDirectory(text);
			string text2 = Path.Combine(text, "source");
			string text3 = Path.Combine(text, "target");
			Directory.CreateDirectory(text2);
			Directory.CreateDirectory(text3);
			string text4 = Path.Combine(text2, "sample.txt");
			File.WriteAllText(text4, "Disposable diagnostic data");
			CopyEngine copyEngine = new CopyEngine();
			copyEngine.RetryDelayMs = 10;
			CopyEngine copyEngine2 = copyEngine;
			List<Entry> plan = copyEngine2.Plan(new string[1] { text2 }, text3, token, folderContents: true);
			Result result = copyEngine2.Run(plan, ExistingRule.Replace, verify: true, compareOnly: false, token);
			action(result.Copied == 1 && result.Matched == 1 && result.Clean, "copy and SHA-256 verification");
			result = copyEngine2.Run(copyEngine2.Plan(new string[1] { text2 }, text3, token, folderContents: true), ExistingRule.Replace, verify: true, compareOnly: true, token);
			action(result.Matched == 1 && result.Clean, "read-only comparison");
			string text5 = text2;
			while (text5.Length < 440)
			{
				text5 = Path.Combine(text5, "long-folder-12345678901234567890");
			}
			Directory.CreateDirectory(LongPath.ForIO(text5));
			File.WriteAllText(LongPath.ForIO(Path.Combine(text5, "long-file.txt")), "Long path diagnostic");
			result = copyEngine2.Run(copyEngine2.Plan(new string[1] { text2 }, text3, token, folderContents: true), ExistingRule.Replace, verify: true, compareOnly: false, token);
			action(result.Matched == 2 && result.Errors == 0, "long-path read/copy/verify on selected filesystem");
			using (new FileStream(Path.Combine(text3, "sample.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
			{
				copyEngine2.Retries = 0;
				result = copyEngine2.Run(copyEngine2.Plan(new string[1] { text4 }, text3, token), ExistingRule.Replace, verify: true, compareOnly: true, token);
				action(result.Errors > 0 && !result.Clean, "locked file reported as unreadable, not missing or matched");
			}
			copyEngine2.BeforeCopy = (string param0) =>
			{
				throw new IOException("Simulated disk full / write failure");
			};
			result = copyEngine2.Run(copyEngine2.Plan(new string[1] { text4 }, text3, token), ExistingRule.Replace, verify: true, compareOnly: false, token);
			action(result.Errors > 0 && File.ReadAllText(Path.Combine(text3, "sample.txt")) == "Disposable diagnostic data", "write failure preserves existing destination (simulated)");
			report.AppendLine(passed + "/5 checks passed.");
			report.AppendLine("This tests only a unique disposable subfolder. The locked-file and long-path checks use the selected filesystem. Disk-full/write failure is simulated; no network disconnect is forced. A real disconnect/reconnect test and production metadata/permission checks are still required separately.");
		}
		catch (Exception ex)
		{
			report.AppendLine("STOPPED: " + ex.Message);
		}
		finally
		{
			try
			{
				if (Directory.Exists(text))
				{
					CopyEngine.SafePath(text);
					Directory.Delete(LongPath.ForIO(text), recursive: true);
				}
				report.AppendLine("Disposable test folder cleaned up.");
			}
			catch (Exception ex2)
			{
				report.AppendLine("Cleanup incomplete; inspect only this test folder: " + text + " | " + ex2.Message);
			}
		}
		return report.ToString();
	}
}
