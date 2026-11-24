using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClearCopy;

public static class NetworkFolderBrowser
{
	[ComImport]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	[Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
	private interface IFileDialog
	{
		[PreserveSig]
		int Show(IntPtr owner);

		void SetFileTypes(uint count, IntPtr types);

		void SetFileTypeIndex(uint index);

		void GetFileTypeIndex(out uint index);

		void Advise(IntPtr events, out uint cookie);

		void Unadvise(uint cookie);

		void SetOptions(uint options);

		void GetOptions(out uint options);

		void SetDefaultFolder(IShellItem item);

		void SetFolder(IShellItem item);

		void GetFolder(out IShellItem item);

		void GetCurrentSelection(out IShellItem item);

		void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);

		void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);

		void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);

		void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);

		void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);

		void GetResult(out IShellItem item);

		void AddPlace(IShellItem item, int placement);

		void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);

		void Close(int result);

		void SetClientGuid(ref Guid guid);

		void ClearClientData();

		void SetFilter(IntPtr filter);
	}

	[ComImport]
	[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellItem
	{
		void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);

		void GetParent(out IShellItem parent);

		void GetDisplayName(uint format, out IntPtr name);

		void GetAttributes(uint mask, out uint attributes);

		void Compare(IShellItem other, uint hint, out int order);
	}

	private const uint FolderOptions = 2144u;

	private static readonly Guid ItemId = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
	private static extern void SHCreateItemFromParsingName(string name, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

	[DllImport("shell32.dll", PreserveSig = false)]
	private static extern void SHGetKnownFolderItem(ref Guid folder, uint flags, IntPtr token, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

	private static IFileDialog Create()
	{
		IFileDialog fileDialog = (IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")));
		try
		{
			fileDialog.GetOptions(out var options);
			fileDialog.SetOptions(options | 0x860);
			fileDialog.SetTitle("Select a network shared folder — ClearCopy");
			fileDialog.SetOkButtonLabel("Select folder");
			return fileDialog;
		}
		catch
		{
			Marshal.ReleaseComObject(fileDialog);
			throw;
		}
	}

	private static void OpenNetwork(IFileDialog dialog)
	{
		Guid folder = new Guid("D20BEEC4-5CA8-4905-AE3B-BF251EA09B53");
		Guid iid = ItemId;
		SHGetKnownFolderItem(ref folder, 0u, IntPtr.Zero, ref iid, out var item);
		try
		{
			dialog.SetFolder(item);
		}
		finally
		{
			Marshal.ReleaseComObject(item);
		}
	}

	public static string Select(Window owner)
	{
		IFileDialog fileDialog = Create();
		try
		{
			OpenNetwork(fileDialog);
			int num = fileDialog.Show(new WindowInteropHelper(owner).Handle);
			if (num == -2147023673)
			{
				return null;
			}
			Marshal.ThrowExceptionForHR(num);
			fileDialog.GetResult(out var item);
			try
			{
				return Name(item, 2147844096u);
			}
			finally
			{
				Marshal.ReleaseComObject(item);
			}
		}
		finally
		{
			Marshal.ReleaseComObject(fileDialog);
		}
	}

	private static string Name(IShellItem item, uint format)
	{
		item.GetDisplayName(format, out var name);
		try
		{
			return Marshal.PtrToStringUni(name);
		}
		finally
		{
			Marshal.FreeCoTaskMem(name);
		}
	}

	public static void SmokeTest(string folder, string report)
	{
		IFileDialog fileDialog = Create();
		try
		{
			fileDialog.GetOptions(out var options);
			if ((options & 0x860) != 2144)
			{
				throw new IOException("Folder-only options were not applied.");
			}
			Guid iid = ItemId;
			SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iid, out var item);
			try
			{
				fileDialog.SetFolder(item);
			}
			finally
			{
				Marshal.ReleaseComObject(item);
			}
			fileDialog.GetFolder(out item);
			try
			{
				if (!string.Equals(Name(item, 2147844096u).TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
				{
					throw new IOException("Wrong local navigation result.");
				}
			}
			finally
			{
				Marshal.ReleaseComObject(item);
			}
			OpenNetwork(fileDialog);
			fileDialog.GetFolder(out item);
			string text;
			try
			{
				text = Name(item, 0u);
			}
			finally
			{
				Marshal.ReleaseComObject(item);
			}
			File.WriteAllText(report, "PASS native folder-only options, local folder navigation and Windows Network namespace: " + text + "\r\nReal SMB share selection has not been tested.");
		}
		finally
		{
			Marshal.ReleaseComObject(fileDialog);
		}
	}
}
