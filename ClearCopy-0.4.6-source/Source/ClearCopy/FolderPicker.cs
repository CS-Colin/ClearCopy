using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClearCopy;

public sealed class FolderPicker : Window
{
	private readonly TextBox path = new TextBox
	{
		MinWidth = 350.0,
		VerticalContentAlignment = VerticalAlignment.Center
	};

	private readonly ListBox folders = new ListBox
	{
		DisplayMemberPath = "Name"
	};

	private readonly TextBlock selected = new TextBlock
	{
		TextWrapping = TextWrapping.Wrap,
		Margin = new Thickness(0.0, 8.0, 0.0, 8.0)
	};

	private string current;

	private int navigationVersion;

	public string SelectedPath { get; private set; }

	public FolderPicker(Window owner, string initial)
	{
		FolderPicker folderPicker = this;
		if (Application.Current.Resources.Contains(typeof(Window)))
		{
			SetResourceReference(FrameworkElement.StyleProperty, typeof(Window));
		}
		Owner = owner;
		Title = "Select folder — ClearCopy";
		Width = 740.0;
		Height = 510.0;
		MinWidth = 560.0;
		MinHeight = 350.0;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		DockPanel dockPanel = (DockPanel)(Content = new DockPanel
		{
			Margin = new Thickness(16.0)
		});
		StackPanel stackPanel = new StackPanel();
		DockPanel.SetDock(stackPanel, Dock.Top);
		dockPanel.Children.Add(stackPanel);
		stackPanel.Children.Add(new TextBlock
		{
			Text = "Click a folder once, then Select folder. Double-click to look inside.",
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		});
		DockPanel dockPanel3 = new DockPanel();
		stackPanel.Children.Add(dockPanel3);
		Button element = Button("Go", () =>
		{
			Navigate(path.Text);
		});
		DockPanel.SetDock(element, Dock.Right);
		dockPanel3.Children.Add(element);
		Button element2 = Button("Up", () =>
		{
			if (current != null)
			{
				DirectoryInfo parent = Directory.GetParent(current);
				if (parent != null)
				{
					Navigate(parent.FullName);
				}
			}
		});
		DockPanel.SetDock(element2, Dock.Right);
		dockPanel3.Children.Add(element2);
		dockPanel3.Children.Add(path);
		WrapPanel wrapPanel = new WrapPanel
		{
			Margin = new Thickness(0.0, 6.0, 0.0, 6.0)
		};
		stackPanel.Children.Add(wrapPanel);
		string[] logicalDrives = Directory.GetLogicalDrives();
		foreach (string text in logicalDrives)
		{
			string target = text;
			wrapPanel.Children.Add(Button(text, () =>
			{
				folderPicker.Navigate(target);
			}));
		}
		wrapPanel.Children.Add(Button("Network…", () =>
		{
			string text2 = NetworkFolderBrowser.Select(this);
			if (text2 != null)
			{
				SelectedPath = text2;
				DialogResult = true;
			}
		}));
		stackPanel.Children.Add(new TextBlock
		{
			Text = "Network: click Network… or enter a full share path such as \\\\server\\share. Select a shared folder inside the device.",
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		});
		StackPanel stackPanel2 = new StackPanel();
		DockPanel.SetDock(stackPanel2, Dock.Bottom);
		dockPanel.Children.Add(stackPanel2);
		stackPanel2.Children.Add(selected);
		StackPanel stackPanel3 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right
		};
		stackPanel2.Children.Add(stackPanel3);
		stackPanel3.Children.Add(Button("Select folder", Select));
		Button button = Button("Cancel", () =>
		{
			DialogResult = false;
		});
		button.IsCancel = true;
		stackPanel3.Children.Add(button);
		dockPanel.Children.Add(folders);
		folders.SelectionChanged += (object param0, SelectionChangedEventArgs param1) =>
		{
			ShowSelection();
		};
		folders.MouseDoubleClick += (object param0, MouseButtonEventArgs param1) =>
		{
			if (folders.SelectedItem is DirectoryInfo directoryInfo)
			{
				Navigate(directoryInfo.FullName);
			}
		};
		folders.KeyDown += (object s, KeyEventArgs e) =>
		{
			if (e.Key == Key.Return)
			{
				Select();
				e.Handled = true;
			}
		};
		path.KeyDown += (object s, KeyEventArgs e) =>
		{
			if (e.Key == Key.Return)
			{
				Navigate(path.Text);
				e.Handled = true;
			}
		};
		path.TextChanged += (object param0, TextChangedEventArgs param1) =>
		{
			ShowSelection();
		};
		Loaded += (object param0, RoutedEventArgs param1) =>
		{
			folderPicker.Navigate(string.IsNullOrWhiteSpace(initial) ? Environment.GetFolderPath(Environment.SpecialFolder.Personal) : initial);
		};
	}

	private Button Button(string label, Action action)
	{
		Button button = new Button();
		button.Content = label;
		button.Padding = new Thickness(12.0, 6.0, 12.0, 6.0);
		button.Margin = new Thickness(6.0, 0.0, 0.0, 0.0);
		Button button2 = button;
		button2.Click += (object param0, RoutedEventArgs param1) =>
		{
			try
			{
				action();
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, ex.Message, "Folder selection");
			}
		};
		return button2;
	}

	private async void Navigate(string value)
	{
		int version = ++navigationVersion;
		folders.IsEnabled = false;
		selected.Text = "Loading folder… " + value;
		try
		{
			int num = default;
			_ = num;
			_ = 0;
			try
			{
				string full = Path.GetFullPath(value);
				DirectoryInfo[] children = await Task.Run(() => new DirectoryInfo(full).GetDirectories().OrderBy((DirectoryInfo d) => d.Name, StringComparer.OrdinalIgnoreCase).ToArray());
				if (version == navigationVersion && IsVisible)
				{
					current = full;
					path.Text = full;
					folders.ItemsSource = children;
					folders.SelectedIndex = -1;
					ShowSelection();
				}
			}
			catch (Exception ex)
			{
				if (version == navigationVersion && IsVisible)
				{
					MessageBox.Show(this, ex.Message, "Cannot open folder");
				}
			}
		}
		finally
		{
			if (version == navigationVersion)
			{
				folders.IsEnabled = true;
			}
		}
	}

	private string Choice()
	{
		DirectoryInfo directoryInfo = folders.SelectedItem as DirectoryInfo;
		if (!(path.Text != current))
		{
			if (directoryInfo == null)
			{
				return current;
			}
			return directoryInfo.FullName;
		}
		return path.Text;
	}

	private void ShowSelection()
	{
		selected.Text = "Folder to select: " + Choice();
	}

	private async void Select()
	{
		string choice = Choice();
		int num = ((!string.IsNullOrWhiteSpace(choice) && await Task.Run(() => Directory.Exists(choice))) ? 1 : 0);
		bool exists = (byte)num != 0;
		if (IsVisible)
		{
			if (!exists)
			{
				MessageBox.Show(this, "Choose an existing folder or enter its full path.");
				return;
			}
			SelectedPath = Path.GetFullPath(choice);
			DialogResult = true;
		}
	}

	public static void SelectionTest(string parent, string expected, string screenshot)
	{
		FolderPicker picker = new FolderPicker(null, parent);
		picker.Loaded += (object param0, RoutedEventArgs param1) =>
		{
			DispatcherTimer timer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(500.0)
			};
			timer.Tick += (object obj, EventArgs e) =>
			{
				timer.Stop();
				picker.folders.SelectedItem = picker.folders.Items.Cast<DirectoryInfo>().First((DirectoryInfo d) => d.FullName == expected);
				picker.UpdateLayout();
				RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap((int)picker.ActualWidth, (int)picker.ActualHeight, 96.0, 96.0, PixelFormats.Pbgra32);
				renderTargetBitmap.Render(picker);
				PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder
				{
					Frames = { BitmapFrame.Create(renderTargetBitmap) }
				};
				using (FileStream stream = File.Create(screenshot))
				{
					pngBitmapEncoder.Save(stream);
				}
				picker.Select();
			};
			timer.Start();
		};
		bool? flag = picker.ShowDialog();
		if (flag != true || !flag.HasValue || picker.SelectedPath != expected)
		{
			throw new IOException("Folder selection returned the wrong path.");
		}
		File.WriteAllText(screenshot + ".txt", "PASS single-click selection + Select folder returned: " + picker.SelectedPath);
	}
}
