using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SystemColors = System.Windows.SystemColors;

namespace ClearCopy;

public sealed class PasteHelper : IDisposable
{
	internal sealed class KeyboardHookThread
	{
		private readonly HookProc callback;

		private readonly Thread thread;

		private readonly ManualResetEvent started = new ManualResetEvent(initialState: false);

		private uint threadId;

		private IntPtr hook;

		private Exception error;

		public uint ThreadId => threadId;

		public KeyboardHookThread(HookProc callback)
		{
			this.callback = callback;
			thread = new Thread(Loop)
			{
				IsBackground = true,
				Name = "ClearCopy keyboard hook",
				Priority = ThreadPriority.AboveNormal
			};
			thread.Start();
			if (!started.WaitOne(5000))
			{
				throw new IOException("The keyboard hook thread did not start within 5 seconds.");
			}
			if (error != null)
			{
				throw error;
			}
		}

		private void Loop()
		{
			Msg message;
			try
			{
				threadId = GetCurrentThreadId();
				PeekMessage(out message, IntPtr.Zero, 0u, 0u, 0u);
				hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0u);
				if (hook == IntPtr.Zero)
				{
					error = new Win32Exception(Marshal.GetLastWin32Error());
				}
			}
			catch (Exception ex)
			{
				error = ex;
			}
			finally
			{
				started.Set();
			}
			if (hook == IntPtr.Zero)
			{
				return;
			}
			try
			{
				while (GetMessage(out message, IntPtr.Zero, 0u, 0u) > 0)
				{
					TranslateMessage(ref message);
					DispatchMessage(ref message);
				}
			}
			finally
			{
				UnhookWindowsHookEx(hook);
				hook = IntPtr.Zero;
			}
		}

		public bool Stop(int timeoutMs)
		{
			if (threadId != 0)
			{
				PostThreadMessage(threadId, 18u, IntPtr.Zero, IntPtr.Zero);
			}
			bool result = thread.Join(timeoutMs);
			GC.KeepAlive(callback);
			return result;
		}
	}

	internal delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

	[ComImport]
	[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IComServiceProvider
	{
		[PreserveSig]
		int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
	}

	[ComImport]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	[Guid("000214E2-0000-0000-C000-000000000046")]
	private interface IShellBrowserWindow
	{
		[PreserveSig]
		int GetWindow(out IntPtr window);
	}

	private struct KeyData
	{
		public uint Key;

		public uint Scan;

		public uint Flags;

		public uint Time;

		public UIntPtr Extra;
	}

	private struct Rect
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private struct GuiInfo
	{
		public int Size;

		public int Flags;

		public IntPtr Active;

		public IntPtr Focus;

		public IntPtr Capture;

		public IntPtr MenuOwner;

		public IntPtr MoveSize;

		public IntPtr Caret;

		public Rect CaretRect;
	}

	private struct KeyboardInput
	{
		public ushort Key;

		public ushort Scan;

		public uint Flags;

		public uint Time;

		public UIntPtr Extra;
	}

	private struct MouseInput
	{
		public int X;

		public int Y;

		public uint Data;

		public uint Flags;

		public uint Time;

		public UIntPtr Extra;
	}

	[StructLayout(LayoutKind.Explicit)]
	private struct InputUnion
	{
		[FieldOffset(0)]
		public KeyboardInput Keyboard;

		[FieldOffset(0)]
		public MouseInput Mouse;
	}

	private struct Input
	{
		public uint Type;

		public InputUnion Data;
	}

	private struct Msg
	{
		public IntPtr Window;

		public uint Message;

		public IntPtr WParam;

		public IntPtr LParam;

		public uint Time;

		public int X;

		public int Y;
	}

	private const string HookCaveat = "Note: Windows can remove a keyboard hook without notice, for example under very heavy load. ClearCopy cannot detect that reliably. If Ctrl+V in Explorer starts behaving like normal Windows paste, right-click the ClearCopy tray icon and choose Restart helper.";

	private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

	private const string RunName = "ClearCopyPasteHelper";

	private readonly EventWaitHandle stop = new EventWaitHandle(initialState: false, EventResetMode.ManualReset, Identity + "-stop");

	private readonly EventWaitHandle ready = new EventWaitHandle(initialState: false, EventResetMode.ManualReset, Identity + "-ready");

	private readonly DispatcherTimer timer = new DispatcherTimer
	{
		Interval = TimeSpan.FromMilliseconds(200.0)
	};

	private readonly Dispatcher ui;

	private readonly HookProc callback;

	private KeyboardHookThread hookThread;

	private NotifyIcon tray;

	private HwndSource clipboardWindow;

	private string[] copied;

	private uint clipSequence;

	private string contextReason = "Waiting for an Explorer folder view.";

	private string queuedStatus;

	private bool restartRequested;

	private PasteTarget readyTarget;

	private PasteTarget pendingTarget;

	private readonly PasteShortcutState shortcut = new PasteShortcutState();

	private static readonly Guid TopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");

	public static string StatusPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearCopy", "Logs", "paste-helper-status.txt");

	public static bool Running
	{
		get
		{
			try
			{
				using EventWaitHandle eventWaitHandle = EventWaitHandle.OpenExisting(Identity + "-ready");
				return eventWaitHandle.WaitOne(0);
			}
			catch (WaitHandleCannotBeOpenedException)
			{
				return false;
			}
		}
	}

	private static string Identity => "Local\\ClearCopyPaste-" + WindowsIdentity.GetCurrent().User.Value;

	public static bool Enabled
	{
		get
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
			return registryKey != null && registryKey.GetValue("ClearCopyPasteHelper") != null;
		}
	}

	public static string StatusReport()
	{
		string text = (Running ? "Paste helper: running." : "Paste helper: NOT running. Enable Explorer Ctrl+V chooser to start it.");
		try
		{
			if (File.Exists(StatusPath))
			{
				text = text + "\n\nLast helper report:\n" + File.ReadAllText(StatusPath);
			}
		}
		catch (Exception ex)
		{
			text = text + "\nCannot read helper status: " + ex.Message;
		}
		return text + "\n\nNote: Windows can remove a keyboard hook without notice, for example under very heavy load. ClearCopy cannot detect that reliably. If Ctrl+V in Explorer starts behaving like normal Windows paste, right-click the ClearCopy tray icon and choose Restart helper.";
	}

	public static void Enable()
	{
		if (!File.Exists(SelfInstall.InstalledPath))
		{
			throw new IOException("Install ClearCopy on this PC first, then enable the chooser.");
		}
		using (RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
		{
			registryKey.SetValue("ClearCopyPasteHelper", "\"" + SelfInstall.InstalledPath + "\" --paste-helper");
		}
		RestartIfEnabled();
		DateTime dateTime = DateTime.UtcNow.AddSeconds(8.0);
		do
		{
			try
			{
				using EventWaitHandle eventWaitHandle = EventWaitHandle.OpenExisting(Identity + "-ready");
				if (eventWaitHandle.WaitOne(0))
				{
					return;
				}
			}
			catch (WaitHandleCannotBeOpenedException)
			{
			}
			Thread.Sleep(100);
		}
		while (DateTime.UtcNow < dateTime);
		throw new IOException("The paste chooser helper did not confirm startup. Check for a ClearCopy helper error message, then retry setup.");
	}

	public static void Disable()
	{
		using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true))
		{
			registryKey?.DeleteValue("ClearCopyPasteHelper", throwOnMissingValue: false);
		}
		Stop();
	}

	public static void Stop()
	{
		try
		{
			using (EventWaitHandle eventWaitHandle = EventWaitHandle.OpenExisting(Identity + "-stop"))
			{
				eventWaitHandle.Set();
			}
			using Mutex mutex = Mutex.OpenExisting(Identity + "-mutex");
			bool flag = false;
			try
			{
				try
				{
					flag = mutex.WaitOne(5000);
				}
				catch (AbandonedMutexException)
				{
					flag = true;
				}
				if (!flag)
				{
					throw new IOException("The paste helper is still busy. Close its chooser and retry.");
				}
			}
			finally
			{
				if (flag)
				{
					mutex.ReleaseMutex();
				}
			}
		}
		catch (WaitHandleCannotBeOpenedException)
		{
		}
	}

	public static void RestartIfEnabled()
	{
		if (Enabled)
		{
			using (RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
			{
				registryKey.SetValue("ClearCopyPasteHelper", "\"" + SelfInstall.InstalledPath + "\" --paste-helper");
			}
			ProcessStartInfo processStartInfo = new ProcessStartInfo(SelfInstall.InstalledPath, "--paste-helper");
			processStartInfo.UseShellExecute = true;
			Process.Start(processStartInfo);
		}
	}

	public static void Run()
	{
		bool flag = false;
		bool createdNew;
		using (Mutex mutex = new Mutex(initiallyOwned: true, Identity + "-mutex", out createdNew))
		{
			if (!createdNew)
			{
				return;
			}
			System.Windows.Application application = new System.Windows.Application();
			application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
			System.Windows.Application application2 = application;
			try
			{
				using PasteHelper pasteHelper = new PasteHelper();
				application2.Run();
				flag = pasteHelper.restartRequested;
			}
			catch (Exception ex)
			{
				System.Windows.MessageBox.Show("The Explorer Ctrl+V helper could not start: " + ex.Message, "ClearCopy");
			}
			finally
			{
				mutex.ReleaseMutex();
			}
		}
		if (flag)
		{
			try
			{
				ProcessStartInfo processStartInfo = new ProcessStartInfo(SelfInstall.InstalledPath, "--paste-helper");
				processStartInfo.UseShellExecute = true;
				Process.Start(processStartInfo);
			}
			catch (Exception ex2)
			{
				System.Windows.MessageBox.Show("The helper could not restart: " + ex2.Message + "\nOpen ClearCopy to start it again.", "ClearCopy");
			}
		}
	}

	public static void NativeCheck(string report)
	{
		uint currentThreadId = GetCurrentThreadId();
		HookProc obj = (int c, IntPtr m, IntPtr d) => CallNextHookEx(IntPtr.Zero, c, m, d);
		KeyboardHookThread keyboardHookThread = new KeyboardHookThread(obj);
		bool flag = keyboardHookThread.ThreadId != 0 && keyboardHookThread.ThreadId != currentThreadId;
		bool flag2;
		try
		{
			if (Marshal.SizeOf(typeof(Input)) != 40)
			{
				throw new Exception("Unexpected x64 input structure size");
			}
			if (Marshal.SizeOf(typeof(Msg)) != 48)
			{
				throw new Exception("Unexpected x64 MSG structure size");
			}
			if (!flag)
			{
				throw new Exception("Keyboard hook did not run on a dedicated thread");
			}
		}
		finally
		{
			flag2 = keyboardHookThread.Stop(3000);
			GC.KeepAlive(obj);
		}
		if (!flag2)
		{
			throw new Exception("Keyboard hook thread did not stop within 3 seconds");
		}
		File.WriteAllText(report, "PASS native keyboard hook registered on a dedicated message-loop thread; x64 input layout is 40 bytes, MSG 48 bytes; hook thread stopped and hook removed afterward. No keystrokes injected.");
	}

	public static void ChooserCheck(string directory)
	{
		Directory.CreateDirectory(directory);
		var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
		string registryPath = @"Software\ClearCopyChooserTest-" + Guid.NewGuid().ToString("N");
		var preference = new PastePreference(registryPath);

		void Check(bool passed, string message)
		{
			if (!passed)
			{
				throw new InvalidOperationException(message);
			}
		}

		void ReportFailure(string message) => throw new IOException(message);

		PasteChoice ClickOption(int index, bool remember, bool dark = false)
		{
			var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
			timer.Tick += (_, _) =>
			{
				timer.Stop();
				Window window = app.Windows.Cast<Window>().Single();
				var content = (ScrollViewer)window.Content;
				var panel = (StackPanel)content.Content;
				var checkbox = panel.Children.OfType<System.Windows.Controls.CheckBox>().Single();
				Check(checkbox.IsChecked == false, "Remember my choice must start unchecked.");
				checkbox.IsChecked = remember;
				if (index < 0)
				{
					window.Close();
					return;
				}
				window.UpdateLayout();
				var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
				bitmap.Render(content);
				var encoder = new PngBitmapEncoder { Frames = { BitmapFrame.Create(bitmap) } };
				using (FileStream stream = File.Create(Path.Combine(directory, "Chooser-" + (dark ? "Dark" : "Light") + ".png")))
				{
					encoder.Save(stream);
				}
				panel.Children.OfType<System.Windows.Controls.Button>().ElementAt(index)
					.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
			};
			return preference.Resolve(() =>
			{
				timer.Start();
				return ShowChooser(@"C:\Disposable destination", 1, new[] { @"C:\Source\Example file.txt" }, dark);
			}, ReportFailure);
		}

		try
		{
			Check(ClickOption(2, true) == PasteChoice.Cancel && preference.Read() == null, "Cancel saved a choice.");
			Check(ClickOption(-1, true) == PasteChoice.Cancel && preference.Read() == null, "Closing saved a choice.");
			Check(ClickOption(0, false) == PasteChoice.Windows && preference.Read() == null, "Unchecked choice was saved.");

			foreach (PasteChoice expected in new[] { PasteChoice.Windows, PasteChoice.ClearCopy })
			{
				Check(ClickOption(expected == PasteChoice.Windows ? 0 : 1, true, dark: expected == PasteChoice.ClearCopy) == expected,
					"The selected action was not returned.");
				var freshReader = new PastePreference(registryPath);
				Check(freshReader.Resolve(() => throw new InvalidOperationException("A remembered choice opened the chooser."), ReportFailure) == expected,
					"The saved choice did not survive a fresh read.");
				freshReader.Reset();
				Check(preference.Read() == null, "Reset did not clear the remembered choice.");
			}
			using (RegistryKey key = Registry.CurrentUser.CreateSubKey(registryPath))
			{
				key.SetValue("RememberedPasteChoice", "Cancel");
			}
			Check(ClickOption(2, false) == PasteChoice.Cancel && preference.Read() == null, "An invalid preference bypassed the chooser.");
			File.WriteAllText(Path.Combine(directory, "chooser-check.txt"),
				"PASS both copy choices, opt-in, persistence, bypass, Cancel, close, reset and invalid preference. No real paste was performed.");
		}
		finally
		{
			Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false);
			app.Shutdown();
		}
	}

	private void Report(string message)
	{
		queuedStatus = string.Concat(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), " | ClearCopy ", Assembly.GetExecutingAssembly().GetName().Version, "\r\n", message);
	}

	private void FlushStatus()
	{
		string text = queuedStatus;
		if (text == null)
		{
			return;
		}
		queuedStatus = null;
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(StatusPath));
			File.WriteAllText(StatusPath, text);
		}
		catch
		{
		}
	}

	public PasteHelper()
	{
		ui = Dispatcher.CurrentDispatcher;
		callback = Keyboard;
		hookThread = new KeyboardHookThread(callback);
		clipboardWindow = new HwndSource(new HwndSourceParameters("ClearCopy clipboard listener")
		{
			ParentWindow = new IntPtr(-3)
		});
		clipboardWindow.AddHook(ClipboardMessage);
		AddClipboardFormatListener(clipboardWindow.Handle);
		ContextMenuStrip contextMenuStrip = new ContextMenuStrip
		{
			Items = 
			{
				{
					"Open ClearCopy",
					(System.Drawing.Image)null,
					(EventHandler)((object param0, EventArgs param1) =>
					{
						Process.Start(SelfInstall.IntegrationPath);
					})
				},
				{
					"Paste helper status",
					(System.Drawing.Image)null,
					(EventHandler)((object param0, EventArgs param1) =>
					{
						System.Windows.MessageBox.Show(StatusReport(), "ClearCopy paste helper");
					})
				},
				{
					"Restart helper",
					(System.Drawing.Image)null,
					(EventHandler)((object param0, EventArgs param1) =>
					{
						restartRequested = true;
						System.Windows.Application.Current.Shutdown();
					})
				},
				{
					"Disable Ctrl+V chooser",
					(System.Drawing.Image)null,
					(EventHandler)((object param0, EventArgs param1) =>
					{
						Disable();
					})
				},
				{
					"Exit helper",
					(System.Drawing.Image)null,
					(EventHandler)((object param0, EventArgs param1) =>
					{
						System.Windows.Application.Current.Shutdown();
					})
				}
			}
		};
		using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClearCopy.ico"))
		{
			using Icon icon = new Icon(stream);
			tray = new NotifyIcon
			{
				Icon = (Icon)icon.Clone(),
				Text = "ClearCopy — Explorer Ctrl+V chooser",
				ContextMenuStrip = contextMenuStrip,
				Visible = true
			};
		}
		timer.Tick += (object param0, EventArgs param1) =>
		{
			if (stop.WaitOne(0))
			{
				System.Windows.Application.Current.Shutdown();
			}
			else
			{
				FlushStatus();
				RefreshContext();
			}
		};
		timer.Start();
		RefreshContext();
		ready.Set();
		Report("Helper started (keyboard hook on its own thread). Copy files in Explorer, click inside the destination's file list, and press Ctrl+V.");
		FlushStatus();
	}

	private IntPtr ClipboardMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (message == 797)
		{
			Dispatcher.CurrentDispatcher.BeginInvoke(new Action(RefreshContext));
		}
		return IntPtr.Zero;
	}

	private void RefreshContext()
	{
		if (Volatile.Read(ref pendingTarget) != null)
		{
			return;
		}
		try
		{
			IntPtr foregroundWindow = GetForegroundWindow();
			if (!ExplorerView(foregroundWindow))
			{
				Volatile.Write(ref readyTarget, null);
				contextReason = "Focus is outside a supported Explorer file list (window=" + ClassName(foregroundWindow) + ", focus=" + FocusClass(foregroundWindow) + "). Text fields keep normal paste.";
				return;
			}
			string text = ExplorerFolder(foregroundWindow);
			uint clipboardSequenceNumber = GetClipboardSequenceNumber();
			if (clipboardSequenceNumber != clipSequence || copied == null)
			{
				copied = null;
				System.Windows.IDataObject dataObject = System.Windows.Clipboard.GetDataObject();
				if (dataObject != null && dataObject.GetDataPresent(System.Windows.DataFormats.FileDrop))
				{
					bool cut = false;
					if (dataObject.GetData("Preferred DropEffect") is MemoryStream memoryStream)
					{
						byte[] array = memoryStream.ToArray();
						cut = array.Length >= 4 && (BitConverter.ToInt32(array, 0) & 2) != 0;
					}
					if (PasteRouting.CopiedFiles(hasFiles: true, cut))
					{
						copied = dataObject.GetData(System.Windows.DataFormats.FileDrop) as string[];
					}
				}
				clipSequence = clipboardSequenceNumber;
			}
			bool flag = !string.IsNullOrEmpty(text) && copied != null && copied.Length > 0;
			Volatile.Write(ref readyTarget, flag ? new PasteTarget
			{
				Window = foregroundWindow,
				Folder = text,
				Paths = copied.ToArray(),
				Sequence = clipboardSequenceNumber
			} : null);
			ref string reference = ref contextReason;
			string text2;
			if (string.IsNullOrEmpty(text))
			{
				text2 = "Explorer destination could not be resolved with certainty (virtual folder, or the active tab could not be identified).";
			}
			else
			{
				text2 = ((!flag) ? "Clipboard has no copied file list, or contains a cut/move operation." : "Ready for Explorer Ctrl+V.");
			}
			reference = text2;
		}
		catch (Exception ex)
		{
			Volatile.Write(ref readyTarget, null);
			copied = null;
			contextReason = "Explorer context could not be read: " + ex.GetBaseException().Message;
		}
	}

	private IntPtr Keyboard(int code, IntPtr message, IntPtr data)
	{
		try
		{
			if (code >= 0 && (message == (IntPtr)256 || message == (IntPtr)257 || message == (IntPtr)260 || message == (IntPtr)261))
			{
				KeyData keyData = (KeyData)Marshal.PtrToStructure(data, typeof(KeyData));
				if (shortcut.Process(keyData.Key, message == (IntPtr)256 || message == (IntPtr)260, (keyData.Flags & 0x10) != 0))
				{
					IntPtr foregroundWindow = GetForegroundWindow();
					PasteTarget pasteTarget = Volatile.Read(ref pendingTarget);
					if (pasteTarget != null)
					{
						return (pasteTarget.Window == foregroundWindow) ? ((IntPtr)1) : CallNextHookEx(IntPtr.Zero, code, message, data);
					}
					PasteTarget target = Volatile.Read(ref readyTarget);
					bool flag = target != null && target.Window == foregroundWindow;
					if (PasteRouting.InterceptPaste(target != null, flag, flag && ExplorerView(foregroundWindow), flag && GetClipboardSequenceNumber() == target.Sequence) && Interlocked.CompareExchange(ref pendingTarget, target, null) == null)
					{
						ui.BeginInvoke((Action)(() =>
						{
							Report("Ctrl+V detected. Opening chooser for " + target.Folder);
							Choose(target);
						}));
						return (IntPtr)1;
					}
					string text = ClassName(foregroundWindow);
					if (text == "CabinetWClass" || text == "ExploreWClass")
					{
						bool changed = target != null && GetClipboardSequenceNumber() != target.Sequence;
						ui.BeginInvoke((Action)(() =>
						{
							Report("Ctrl+V passed to Windows. " + contextReason + (changed ? " Clipboard changed before the helper refreshed." : ""));
						}));
					}
				}
			}
		}
		catch
		{
		}
		return CallNextHookEx(IntPtr.Zero, code, message, data);
	}

	private void Choose(PasteTarget target)
	{
		try
		{
			if (GetForegroundWindow() != target.Window)
			{
				Report("Paste cancelled because the foreground window changed.");
				return;
			}
			string a = ExplorerFolder(target.Window);
			if (!string.Equals(a, target.Folder, StringComparison.OrdinalIgnoreCase))
			{
				Report("Destination changed or became uncertain before the chooser opened; Ctrl+V passed to Windows.");
				NativePaste(target.Window, target.Paths);
				return;
			}
			PasteChoice choice = PastePreference.Current.Resolve(
				() => ShowChooser(target.Folder, target.Paths.Length, target.Paths),
				message => Trace.TraceWarning(message));
			switch (choice)
			{
			case PasteChoice.Windows:
				Report("Using Windows paste.");
				NativePaste(target.Window, target.Paths);
				break;
			case PasteChoice.ClearCopy:
			{
				string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearCopy", "PasteRequests");
				Directory.CreateDirectory(text);
				string text2 = Path.Combine(text, Guid.NewGuid().ToString("N") + ".txt");
				File.WriteAllLines(text2, target.Paths);
				ProcessStartInfo processStartInfo = new ProcessStartInfo(SelfInstall.IntegrationPath, "--from-list \"" + text2 + "\" \"" + target.Folder.TrimEnd('\\') + "\\.\" --start");
				processStartInfo.UseShellExecute = true;
				Process.Start(processStartInfo);
				Report("ClearCopy selected; launched copy for " + target.Folder);
				break;
			}
			default:
				Report("Paste cancelled in the chooser.");
				break;
			}
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show("Paste could not start: " + ex.Message + "\nNo source files were removed. You can retry using Explorer's right-click Paste.", "ClearCopy paste");
		}
		finally
		{
			Volatile.Write(ref readyTarget, null);
			Volatile.Write(ref pendingTarget, null);
		}
	}

	private static (PasteChoice Choice, bool Remember) ShowChooser(string folder, int count, string[] paths, bool? darkOverride = null)
	{
		bool dark = darkOverride ?? ReadChooserDarkMode();
		bool highContrast = SystemParameters.HighContrast;
		System.Windows.Media.Brush Ink(string light, string night) =>
			new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? night : light));

		var foreground = highContrast ? SystemColors.WindowTextBrush : Ink("#172B45", "#E6EDF6");
		var muted = highContrast ? foreground : Ink("#536780", "#A8B8CC");
		var surface = highContrast ? SystemColors.WindowBrush : Ink("#FFFFFF", "#172232");
		var outline = highContrast ? foreground : Ink("#CFDCEB", "#34455B");
		var accent = highContrast ? SystemColors.HighlightBrush : Ink("#1466C6", "#60A5FA");
		var hover = highContrast ? surface : Ink("#E3EEFC", "#263D59");
		PasteChoice choice = PasteChoice.Cancel;
		var window = new Window
		{
			Title = "Paste files — ClearCopy",
			Width = 510,
			MaxWidth = SystemParameters.WorkArea.Width,
			MaxHeight = SystemParameters.WorkArea.Height,
			SizeToContent = SizeToContent.Height,
			ResizeMode = ResizeMode.NoResize,
			WindowStartupLocation = WindowStartupLocation.CenterScreen,
			Background = highContrast ? surface : Ink("#EDF3FA", "#0E1623"),
			Foreground = foreground,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13,
			UseLayoutRounding = true
		};

		var panel = new StackPanel { Margin = new Thickness(24) };
		window.Content = new ScrollViewer { Content = panel, Background = window.Background, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
		panel.Children.Add(new TextBlock
		{
			Text = "How would you like to paste?",
			FontSize = 24,
			FontWeight = FontWeights.SemiBold,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0, 0, 0, 12)
		});
		string destination = "To: " + folder;
		string selection = paths == null ? null : string.Join(", ", paths.Take(3).Select(p => Path.GetFileName(p.TrimEnd('\\'))))
			+ (paths.Length > 3 ? ", …" : "");
		var details = new StackPanel();
		details.Children.Add(new TextBlock { Text = count == 1 ? "1 copied item" : count + " copied items", FontWeight = FontWeights.SemiBold });
		details.Children.Add(new TextBlock { Text = destination, Foreground = muted, TextWrapping = TextWrapping.Wrap, MaxHeight = 56, ToolTip = destination });
		if (!string.IsNullOrEmpty(selection))
		{
			details.Children.Add(new TextBlock { Text = selection, Foreground = muted, TextWrapping = TextWrapping.Wrap, MaxHeight = 38, ToolTip = selection });
		}
		panel.Children.Add(new Border
		{
			Child = details, Background = surface, BorderBrush = outline, BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 16)
		});

		window.Resources["PasteHover"] = hover;
		window.Resources["PasteAccent"] = accent;
		var optionTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
			<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type Button}">
				<Border x:Name="Card" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
					BorderThickness="1" CornerRadius="8" Padding="{TemplateBinding Padding}">
					<ContentPresenter/>
				</Border>
				<ControlTemplate.Triggers>
					<Trigger Property="IsMouseOver" Value="True">
						<Setter TargetName="Card" Property="Background" Value="{DynamicResource PasteHover}"/>
						<Setter TargetName="Card" Property="BorderBrush" Value="{DynamicResource PasteAccent}"/>
					</Trigger>
					<Trigger Property="IsKeyboardFocused" Value="True">
						<Setter TargetName="Card" Property="BorderBrush" Value="{DynamicResource PasteAccent}"/>
						<Setter TargetName="Card" Property="BorderThickness" Value="2"/>
					</Trigger>
					<Trigger Property="IsPressed" Value="True">
						<Setter TargetName="Card" Property="Opacity" Value=".8"/>
					</Trigger>
				</ControlTemplate.Triggers>
			</ControlTemplate>
			""");

		void AddOption(PasteChoice value, string name, string description)
		{
			var label = new StackPanel();
			label.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
			label.Children.Add(new TextBlock { Text = description, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
			var button = new System.Windows.Controls.Button
			{
				Content = label, Background = surface, BorderBrush = outline, Foreground = foreground,
				Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8),
				Cursor = System.Windows.Input.Cursors.Hand, Template = optionTemplate
			};
			System.Windows.Automation.AutomationProperties.SetName(button, name);
			System.Windows.Automation.AutomationProperties.SetHelpText(button, description);
			button.Click += (_, _) => { choice = value; window.Close(); };
			panel.Children.Add(button);
		}

		AddOption(PasteChoice.Windows, "Windows — normal copy", "Use the standard Windows file transfer.");
		AddOption(PasteChoice.ClearCopy, "ClearCopy — copy and verify", "Copy the files and verify their integrity.");

		var remember = new System.Windows.Controls.CheckBox
		{
			Content = "Remember my choice",
			Foreground = foreground,
			Margin = new Thickness(0, 9, 0, 4)
		};
		panel.Children.Add(remember);
		panel.Children.Add(new TextBlock
		{
			Text = "Reset your choice in ClearCopy → Explorer integration.",
			Foreground = muted, TextWrapping = TextWrapping.Wrap
		});
		var cancel = new System.Windows.Controls.Button
		{
			Content = "Cancel", IsCancel = true, HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
			Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 12, 0, 0),
			Background = surface, BorderBrush = outline, Foreground = foreground, Template = optionTemplate
		};
		cancel.Click += (_, _) => window.Close();
		panel.Children.Add(cancel);
		window.ShowDialog();
		return (choice, choice != PasteChoice.Cancel && remember.IsChecked == true);
	}

	private static bool ReadChooserDarkMode()
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\ClearCopy");
			return key?.GetValue("DarkMode") is int dark && dark == 1;
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
		{
			return false;
		}
	}

	private static void NativePaste(IntPtr window, string[] paths)
	{
		if (!SetForegroundWindow(window) || GetForegroundWindow() != window)
		{
			throw new IOException("Could not return focus to the original Explorer window.");
		}
		System.Windows.IDataObject dataObject = System.Windows.Clipboard.GetDataObject();
		bool cut = false;
		string[] current = null;
		if (dataObject != null && dataObject.GetDataPresent(System.Windows.DataFormats.FileDrop))
		{
			object data = dataObject.GetData("Preferred DropEffect");
			byte[] array = ((data is MemoryStream memoryStream) ? memoryStream.ToArray() : (data as byte[]));
			cut = array != null && array.Length >= 4 && (BitConverter.ToInt32(array, 0) & 2) != 0;
			current = dataObject.GetData(System.Windows.DataFormats.FileDrop) as string[];
		}
		if (!PasteRouting.SameCopiedFiles(paths, current, cut))
		{
			throw new IOException("The clipboard now contains a different file selection. Windows paste was cancelled; copy your intended files again.");
		}
		Input[] array2 = new Input[4]
		{
			InputKey(17, up: false),
			InputKey(86, up: false),
			InputKey(86, up: true),
			InputKey(17, up: true)
		};
		if (SendInput((uint)array2.Length, array2, Marshal.SizeOf(typeof(Input))) != array2.Length)
		{
			throw new IOException("Windows did not accept the native paste shortcut.");
		}
	}

	private static Input InputKey(ushort key, bool up)
	{
		return new Input
		{
			Type = 1u,
			Data = new InputUnion
			{
				Keyboard = new KeyboardInput
				{
					Key = key,
					Flags = (up ? 2u : 0u)
				}
			}
		};
	}

	private static IntPtr FocusWindow(IntPtr window)
	{
		GuiInfo info = new GuiInfo
		{
			Size = Marshal.SizeOf(typeof(GuiInfo))
		};
		uint windowThreadProcessId = GetWindowThreadProcessId(window, IntPtr.Zero);
		if (!GetGUIThreadInfo(windowThreadProcessId, ref info))
		{
			return IntPtr.Zero;
		}
		return info.Focus;
	}

	private static string FocusClass(IntPtr window)
	{
		return ClassName(FocusWindow(window));
	}

	private static bool ExplorerView(IntPtr window)
	{
		string text = ClassName(window);
		if (text != "CabinetWClass" && text != "ExploreWClass")
		{
			return false;
		}
		IntPtr intPtr = FocusWindow(window);
		string text2 = ClassName(intPtr);
		if (text2.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0 || text2.IndexOf("TextBox", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return false;
		}
		if (PasteRouting.ExplorerView(text, text2))
		{
			return true;
		}
		int num = 0;
		while (intPtr != IntPtr.Zero && num < 16)
		{
			if (ClassName(intPtr) == "SHELLDLL_DefView")
			{
				return true;
			}
			num++;
			intPtr = GetParent(intPtr);
		}
		return false;
	}

	private static string ClassName(IntPtr window)
	{
		StringBuilder stringBuilder = new StringBuilder(256);
		GetClassName(window, stringBuilder, stringBuilder.Capacity);
		return stringBuilder.ToString();
	}

	private static object Member(object item, string name)
	{
		return item.GetType().InvokeMember(name, BindingFlags.GetProperty, null, item, null);
	}

	private static void Release(object item)
	{
		if (item != null && Marshal.IsComObject(item))
		{
			Marshal.FinalReleaseComObject(item);
		}
	}

	private static List<IntPtr> TabWindows(IntPtr window)
	{
		List<IntPtr> list = new List<IntPtr>();
		IntPtr intPtr = IntPtr.Zero;
		for (int i = 0; i < 256; i++)
		{
			intPtr = FindWindowEx(window, intPtr, "ShellTabWindowClass", null);
			if (intPtr == IntPtr.Zero)
			{
				break;
			}
			list.Add(intPtr);
		}
		return list;
	}

	private static IntPtr ActiveTab(IntPtr window, List<IntPtr> tabs)
	{
		IntPtr intPtr = FocusWindow(window);
		int num = 0;
		while (intPtr != IntPtr.Zero && num < 32)
		{
			if (tabs.Contains(intPtr))
			{
				if (!IsWindowVisible(intPtr))
				{
					return IntPtr.Zero;
				}
				return intPtr;
			}
			num++;
			intPtr = GetParent(intPtr);
		}
		return IntPtr.Zero;
	}

	private static IntPtr BrowserWindow(object item)
	{
		if (!(item is IComServiceProvider comServiceProvider))
		{
			return IntPtr.Zero;
		}
		object result = null;
		try
		{
			Guid service = TopLevelBrowser;
			Guid riid = typeof(IShellBrowserWindow).GUID;
			if (comServiceProvider.QueryService(ref service, ref riid, out result) != 0 || result == null)
			{
				return IntPtr.Zero;
			}
			if (!(result is IShellBrowserWindow shellBrowserWindow) || shellBrowserWindow.GetWindow(out var window) != 0)
			{
				return IntPtr.Zero;
			}
			return window;
		}
		catch
		{
			return IntPtr.Zero;
		}
		finally
		{
			Release(result);
		}
	}

	private static string ExplorerFolder(IntPtr window)
	{
		List<IntPtr> list = TabWindows(window);
		IntPtr activeTab = ((list.Count > 0) ? ActiveTab(window, list) : IntPtr.Zero);
		List<ExplorerViewInfo> list2 = new List<ExplorerViewInfo>();
		object obj = null;
		object obj2 = null;
		try
		{
			obj = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true));
			obj2 = obj.GetType().InvokeMember("Windows", BindingFlags.InvokeMethod, null, obj, null);
			int num = Convert.ToInt32(Member(obj2, "Count"));
			for (int i = 0; i < num; i++)
			{
				object obj3 = null;
				object item = null;
				object item2 = null;
				object item3 = null;
				try
				{
					obj3 = obj2.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, obj2, new object[1] { i });
					if (obj3 == null || new IntPtr(Convert.ToInt64(Member(obj3, "HWND"))) != window)
					{
						continue;
					}
					ExplorerViewInfo explorerViewInfo = new ExplorerViewInfo();
					explorerViewInfo.Tab = ((list.Count > 0) ? BrowserWindow(obj3) : IntPtr.Zero);
					ExplorerViewInfo explorerViewInfo2 = explorerViewInfo;
					try
					{
						item = Member(obj3, "Document");
						item2 = Member(item, "Folder");
						item3 = Member(item2, "Self");
						string text = Convert.ToString(Member(item3, "Path"));
						if (Path.IsPathRooted(text) && !text.StartsWith("::"))
						{
							explorerViewInfo2.Path = text;
						}
					}
					catch
					{
						explorerViewInfo2.Path = null;
					}
					list2.Add(explorerViewInfo2);
				}
				finally
				{
					Release(item3);
					Release(item2);
					Release(item);
					Release(obj3);
				}
			}
		}
		finally
		{
			Release(obj2);
			Release(obj);
		}
		return PasteRouting.ResolveDestination(list.Count > 0, activeTab, list2);
	}

	public void Dispose()
	{
		ready.Reset();
		timer.Stop();
		if (clipboardWindow != null)
		{
			RemoveClipboardFormatListener(clipboardWindow.Handle);
			clipboardWindow.Dispose();
		}
		if (hookThread != null)
		{
			hookThread.Stop(3000);
			hookThread = null;
		}
		GC.KeepAlive(callback);
		if (tray != null)
		{
			tray.Visible = false;
			Icon icon = tray.Icon;
			tray.Dispose();
			icon.Dispose();
		}
		stop.Dispose();
		ready.Dispose();
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);

	[DllImport("user32.dll")]
	private static extern bool UnhookWindowsHookEx(IntPtr hook);

	[DllImport("user32.dll")]
	private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

	[DllImport("kernel32.dll", CharSet = CharSet.Auto)]
	private static extern IntPtr GetModuleHandle(string name);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	[DllImport("user32.dll")]
	private static extern int GetMessage(out Msg message, IntPtr window, uint min, uint max);

	[DllImport("user32.dll")]
	private static extern bool PeekMessage(out Msg message, IntPtr window, uint min, uint max, uint remove);

	[DllImport("user32.dll")]
	private static extern bool TranslateMessage(ref Msg message);

	[DllImport("user32.dll")]
	private static extern IntPtr DispatchMessage(ref Msg message);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr window);

	[DllImport("user32.dll")]
	private static extern IntPtr GetParent(IntPtr window);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr window);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);

	[DllImport("user32.dll")]
	private static extern uint GetClipboardSequenceNumber();

	[DllImport("user32.dll")]
	private static extern bool AddClipboardFormatListener(IntPtr window);

	[DllImport("user32.dll")]
	private static extern bool RemoveClipboardFormatListener(IntPtr window);

	[DllImport("user32.dll", CharSet = CharSet.Auto)]
	private static extern int GetClassName(IntPtr window, StringBuilder text, int count);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr process);

	[DllImport("user32.dll")]
	private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint SendInput(uint count, Input[] inputs, int size);
}
