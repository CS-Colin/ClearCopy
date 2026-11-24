using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClearCopy;

public static class PasteRouting
{
	public static bool SameCopiedFiles(string[] captured, string[] current, bool cut)
	{
		if (cut || captured == null || current == null || captured.Length == 0 || current.Length == 0)
		{
			return false;
		}
		try
		{
			Func<string, string> selector = (string path) =>
			{
				if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
				{
					throw new ArgumentException("Invalid clipboard path");
				}
				return Path.GetFullPath(path).TrimEnd('\\');
			};
			return new HashSet<string>(captured.Select(selector), StringComparer.OrdinalIgnoreCase).SetEquals(current.Select(selector));
		}
		catch
		{
			return false;
		}
	}

	public static bool ExplorerView(string windowClass, string focusClass)
	{
		if (windowClass == "CabinetWClass" || windowClass == "ExploreWClass")
		{
			if (!(focusClass == "DirectUIHWND") && !(focusClass == "SysListView32"))
			{
				return focusClass == "SHELLDLL_DefView";
			}
			return true;
		}
		return false;
	}

	public static bool CopiedFiles(bool hasFiles, bool cut)
	{
		if (hasFiles)
		{
			return !cut;
		}
		return false;
	}

	public static bool InterceptPaste(bool targetReady, bool sameForegroundWindow, bool focusInFileView, bool clipboardUnchanged)
	{
		if (targetReady && sameForegroundWindow && focusInFileView)
		{
			return clipboardUnchanged;
		}
		return false;
	}

	public static string ResolveDestination(bool windowHasTabs, IntPtr activeTab, IList<ExplorerViewInfo> views)
	{
		if (views == null || views.Count == 0)
		{
			return null;
		}
		if (!windowHasTabs)
		{
			if (views.Any((ExplorerViewInfo v) => v.Path == null))
			{
				return null;
			}
			List<string> list = views.Select((ExplorerViewInfo v) => v.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			if (list.Count != 1)
			{
				return null;
			}
			return list[0];
		}
		if (activeTab == IntPtr.Zero || views.Any((ExplorerViewInfo v) => v.Tab == IntPtr.Zero))
		{
			return null;
		}
		List<ExplorerViewInfo> list2 = views.Where((ExplorerViewInfo v) => v.Tab == activeTab).ToList();
		if (list2.Count != 1)
		{
			return null;
		}
		return list2[0].Path;
	}
}
