using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace ClearCopy;

public sealed class MainWindow : Window
{
	private sealed class Palette : INotifyPropertyChanged
	{
		private readonly Dictionary<string, Color> values = new Dictionary<string, Color>();

		public Color this[string name]
		{
			get
			{
				if (!values.TryGetValue(name, out var value))
				{
					return Colors.Transparent;
				}
				return value;
			}
		}

		public event PropertyChangedEventHandler PropertyChanged;

		public void Set(string name, Color value)
		{
			values[name] = value;
			if (PropertyChanged != null)
			{
				PropertyChanged(this, new PropertyChangedEventArgs("Item[]"));
			}
		}
	}

	private const string Styles = "\n<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>\n <Style TargetType='{x:Type Window}'><Setter Property='Background' Value='{DynamicResource Canvas}'/><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='FontFamily' Value='Segoe UI'/><Setter Property='Icon' Value='{DynamicResource AppIcon}'/></Style>\n <Style TargetType='{x:Type ProgressBar}'><Setter Property='Background' Value='{DynamicResource Field}'/><Setter Property='BorderBrush' Value='{DynamicResource Outline}'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ProgressBar}'><Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='3'><Grid x:Name='PART_Track' ClipToBounds='True'><Border x:Name='PART_Indicator' Background='{TemplateBinding Foreground}' HorizontalAlignment='Left' CornerRadius='2'/></Grid></Border><ControlTemplate.Triggers><Trigger Property='IsIndeterminate' Value='True'><Setter TargetName='PART_Indicator' Property='MinWidth' Value='80'/><Trigger.EnterActions><BeginStoryboard x:Name='Pulse'><Storyboard><DoubleAnimation Storyboard.TargetName='PART_Indicator' Storyboard.TargetProperty='Opacity' From='.25' To='1' Duration='0:0:0.7' AutoReverse='True' RepeatBehavior='Forever'/></Storyboard></BeginStoryboard></Trigger.EnterActions><Trigger.ExitActions><RemoveStoryboard BeginStoryboardName='Pulse'/></Trigger.ExitActions></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>\n <Style TargetType='{x:Type ScrollBar}'><Setter Property='Background' Value='{DynamicResource Field}'/><Setter Property='Width' Value='12'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ScrollBar}'><Grid Background='{TemplateBinding Background}'><Track x:Name='PART_Track' IsDirectionReversed='True' Orientation='{TemplateBinding Orientation}'><Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Focusable='False'/></Track.DecreaseRepeatButton><Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='{x:Type Thumb}'><Border Background='{DynamicResource Outline}' CornerRadius='4' Margin='2'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb><Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Focusable='False'/></Track.IncreaseRepeatButton></Track></Grid><ControlTemplate.Triggers><Trigger Property='Orientation' Value='Horizontal'><Setter Property='Width' Value='Auto'/><Setter Property='Height' Value='12'/><Setter TargetName='PART_Track' Property='IsDirectionReversed' Value='False'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>\n <Style TargetType='{x:Type TextBlock}'><Setter Property='Foreground' Value='{DynamicResource Ink}'/></Style>\n <Style TargetType='{x:Type Button}'>\n  <Setter Property='Background' Value='{DynamicResource Surface}'/><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='BorderBrush' Value='{DynamicResource Outline}'/><Setter Property='BorderThickness' Value='1'/><Setter Property='Cursor' Value='Hand'/>\n  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type Button}'><Border x:Name='Chrome' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='5' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='Background' Value='{DynamicResource Hover}'/><Setter TargetName='Chrome' Property='BorderBrush' Value='{DynamicResource Accent}'/></Trigger><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='{DynamicResource Accent}'/></Trigger><Trigger Property='IsPressed' Value='True'><Setter TargetName='Chrome' Property='Opacity' Value='.75'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter TargetName='Chrome' Property='Opacity' Value='.4'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>\n </Style>\n <Style TargetType='{x:Type TextBox}'><Setter Property='Background' Value='{DynamicResource Field}'/><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='CaretBrush' Value='{DynamicResource Ink}'/><Setter Property='BorderBrush' Value='{DynamicResource Outline}'/><Setter Property='SelectionBrush' Value='{DynamicResource Accent}'/><Setter Property='Padding' Value='6,4'/></Style>\n <Style TargetType='{x:Type ListBox}'><Setter Property='Background' Value='{DynamicResource Field}'/><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='BorderBrush' Value='{DynamicResource Outline}'/><Setter Property='Padding' Value='5'/></Style>\n <Style TargetType='{x:Type ListBoxItem}'><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='Padding' Value='4'/><Style.Triggers><Trigger Property='IsSelected' Value='True'><Setter Property='Background' Value='{DynamicResource Selected}'/></Trigger></Style.Triggers></Style>\n <Style TargetType='{x:Type CheckBox}'><Setter Property='Foreground' Value='{DynamicResource Ink}'/></Style>\n <Style TargetType='{x:Type Expander}'><Setter Property='Foreground' Value='{DynamicResource Ink}'/></Style>\n <Style TargetType='{x:Type ComboBox}'>\n  <Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='Background' Value='{DynamicResource Field}'/><Setter Property='BorderBrush' Value='{DynamicResource Outline}'/>\n  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ComboBox}'><Grid><ToggleButton Focusable='False' IsChecked='{Binding IsDropDownOpen,RelativeSource={RelativeSource TemplatedParent},Mode=TwoWay}'><ToggleButton.Template><ControlTemplate TargetType='{x:Type ToggleButton}'><Border Background='{DynamicResource Field}' BorderBrush='{DynamicResource Outline}' BorderThickness='1' CornerRadius='4'><DockPanel Margin='8,6'><TextBlock DockPanel.Dock='Right' Text='⌄' Margin='10,0,0,0'/><ContentPresenter Content='{Binding SelectionBoxItem,RelativeSource={RelativeSource AncestorType=ComboBox}}'/></DockPanel></Border></ControlTemplate></ToggleButton.Template></ToggleButton><Popup IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' Focusable='False'><Border Background='{DynamicResource Surface}' BorderBrush='{DynamicResource Outline}' BorderThickness='1' MinWidth='{Binding ActualWidth,RelativeSource={RelativeSource TemplatedParent}}'><ScrollViewer MaxHeight='300'><ItemsPresenter/></ScrollViewer></Border></Popup></Grid></ControlTemplate></Setter.Value></Setter>\n </Style>\n <Style TargetType='{x:Type ComboBoxItem}'><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='Padding' Value='8'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ComboBoxItem}'><Border x:Name='Row' Background='{DynamicResource Surface}' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Row' Property='Background' Value='{DynamicResource Hover}'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>\n <Style TargetType='{x:Type TabControl}'><Setter Property='Background' Value='{DynamicResource Surface}'/><Setter Property='BorderBrush' Value='{DynamicResource Outline}'/></Style>\n <Style TargetType='{x:Type TabItem}'><Setter Property='Foreground' Value='{DynamicResource Muted}'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type TabItem}'><Border x:Name='Tab' Background='{DynamicResource Canvas}' BorderBrush='Transparent' BorderThickness='0,0,0,3' Padding='12,9'><ContentPresenter ContentSource='Header'/></Border><ControlTemplate.Triggers><Trigger Property='IsSelected' Value='True'><Setter TargetName='Tab' Property='Background' Value='{DynamicResource Surface}'/><Setter TargetName='Tab' Property='BorderBrush' Value='{DynamicResource Accent}'/><Setter Property='Foreground' Value='{DynamicResource Ink}'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>\n <Style TargetType='{x:Type DataGrid}'><Setter Property='Background' Value='{DynamicResource Field}'/><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='BorderBrush' Value='{DynamicResource Outline}'/><Setter Property='RowBackground' Value='{DynamicResource Field}'/><Setter Property='AlternatingRowBackground' Value='{DynamicResource Surface}'/><Setter Property='HorizontalGridLinesBrush' Value='{DynamicResource Outline}'/><Setter Property='VerticalGridLinesBrush' Value='{DynamicResource Outline}'/><Setter Property='GridLinesVisibility' Value='Horizontal'/><Setter Property='RowHeaderWidth' Value='0'/></Style>\n <Style TargetType='{x:Type DataGridColumnHeader}'><Setter Property='Background' Value='{DynamicResource Surface}'/><Setter Property='Foreground' Value='{DynamicResource Muted}'/><Setter Property='Padding' Value='8,10'/><Setter Property='BorderBrush' Value='{DynamicResource Outline}'/><Setter Property='BorderThickness' Value='0,0,0,1'/></Style>\n <Style TargetType='{x:Type DataGridCell}'><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='Padding' Value='6'/><Setter Property='BorderThickness' Value='0'/><Style.Triggers><Trigger Property='IsSelected' Value='True'><Setter Property='Background' Value='{DynamicResource Selected}'/></Trigger></Style.Triggers></Style>\n <Style TargetType='{x:Type ToolTip}'><Setter Property='Background' Value='{DynamicResource Surface}'/><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='BorderBrush' Value='{DynamicResource Outline}'/></Style>\n</ResourceDictionary>";

	private const double MinTabsArea = 260.0;

	private const double MinJobPanel = 170.0;

	private static Palette palette;

	private bool darkMode;

	private bool appearanceTesting;

	private Button themeButton;

	private Button installButton;

	private bool installing;

	private readonly ListBox sources = new ListBox
	{
		Height = 88.0
	};

	private readonly TextBox destination = new TextBox
	{
		MinWidth = 150.0,
		VerticalContentAlignment = VerticalAlignment.Center
	};

	private readonly ComboBox rules = new ComboBox
	{
		Width = 330.0
	};

	private readonly ComboBox mode = new ComboBox
	{
		Margin = new Thickness(0.0, 10.0, 0.0, 8.0)
	};

	private readonly CheckBox askNewer = new CheckBox
	{
		Content = "Ask before replacing newer destinations (confirm each file)",
		IsChecked = false,
		Margin = new Thickness(0.0, 7.0, 0.0, 3.0)
	};

	private bool selectedRepair;

	private readonly CheckBox directCompare = new CheckBox
	{
		Content = "Use folder contents directly (applies to both Copy and Compare)",
		IsChecked = false,
		Margin = new Thickness(0.0, 5.0, 0.0, 3.0)
	};

	private readonly CheckBox details = new CheckBox
	{
		Content = "Preserve / compare creation time and file attributes",
		Margin = new Thickness(0.0, 7.0, 0.0, 3.0)
	};

	private readonly CheckBox permissions = new CheckBox
	{
		Content = "Preserve / compare NTFS access permissions (DACL)",
		Margin = new Thickness(0.0, 3.0, 0.0, 3.0)
	};

	private readonly CheckBox skipTolerance = new CheckBox
	{
		Content = "Skip same: treat modified times within 2 seconds as equal (FAT32 drives, some NAS units)",
		IsChecked = false,
		Margin = new Thickness(0.0, 7.0, 0.0, 3.0),
		ToolTip = "Off = exact matching (default). Applies only to the skip-same decision. Change detection, the destination-is-newer test and SHA-256 verification stay exact."
	};

	private readonly CheckBox reviewBeforeCopy = new CheckBox
	{
		Content = "Review copy plan before writing (wait for my confirmation)",
		Margin = new Thickness(0.0, 8.0, 0.0, 3.0)
	};

	private readonly TextBlock mappingPreview = new TextBlock
	{
		TextWrapping = TextWrapping.Wrap,
		FontSize = 12.0
	};

	private readonly ProgressBar bar = new ProgressBar
	{
		Height = 16.0,
		Maximum = 100.0
	};

	private readonly TextBlock status = new TextBlock
	{
		Text = "Ready — copy items in Explorer, then use Paste sources.",
		TextWrapping = TextWrapping.Wrap
	};

	private readonly TextBlock integrationStatus = new TextBlock
	{
		Text = "",
		TextWrapping = TextWrapping.Wrap,
		FontSize = 12.0,
		Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
	};

	private readonly TextBox log = new TextBox
	{
		IsReadOnly = true,
		AcceptsReturn = true,
		VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
		FontFamily = new FontFamily("Consolas"),
		FontSize = 12.0
	};

	private readonly List<Control> locked = new List<Control>();

	private readonly TextBlock phaseText = new TextBlock();

	private readonly TextBlock percentText = new TextBlock();

	private readonly TextBlock currentPath = new TextBlock();

	private readonly TextBlock metrics = new TextBlock();

	private readonly TextBlock counts = new TextBlock();

	// 0.3.9: each count is a link that opens Results filtered to those rows.
	private readonly WrapPanel countsPanel = new WrapPanel();

	private readonly Dictionary<string, Button> countButtons = new Dictionary<string, Button>();

	private string[] statusFilter;

	private readonly TextBlock statusFilterText = new TextBlock();

	private Button clearStatusFilter;

	// 0.4: large jobs. Every row goes to <log>.results.csv; the window (and the
	// job file) keep all issue rows but only the first resultRowLimit ordinary
	// rows, so a 900,000-file copy does not fill memory.
	private int resultRowLimit = 10000;

	private ResultsStream resultsStream;

	private string fullResultsPath;

	private int keptNormalRows;

	private long droppedRows;

	private Button openFullList;

	// 0.4.1 "Fix access". Tests answer the prompt with "fix", "skip" or "cancel"
	// and run the fix in this process instead of asking Windows for elevation.
	private Func<List<string>, string> accessPromptOverride;

	private bool accessFixInProcess;

	private readonly HashSet<string> accessSkipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	public int AccessFixRuns;

	// 0.3.9: compact view hides the tab area below the progress card.
	private Button detailsButton;

	private bool compactView;

	private double expandedHeight = 840.0;

	private RowDefinition pagesRow;

	private Border jobCardBorder;

	private Grid rootGrid;

	private readonly TextBlock fileLabel = new TextBlock();

	private readonly TextBlock jobSummary = new TextBlock();

	private readonly ProgressBar fileBar = new ProgressBar
	{
		Height = 7.0,
		Maximum = 100.0
	};

	private readonly TabControl pages = new TabControl();

	private Update pendingUpdate;

	private DateTime lastProgress = DateTime.UtcNow;

	private DispatcherTimer refresh;

	private readonly Queue<string> uiLog = new Queue<string>();

	private readonly TextBox differences = new TextBox
	{
		IsReadOnly = true,
		AcceptsReturn = true,
		VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
		HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
		FontFamily = new FontFamily("Consolas"),
		FontSize = 12.0,
		Margin = new Thickness(10.0)
	};

	private Button pause;

	private Button cancel;

	private CancellationTokenSource cancellation;

	private CopyEngine engine;

	private IDisposable jobLock;

	private bool starting;

	// Activity log display: paths under the job's destination are shown after
	// this root (the log file itself always keeps full paths).
	private volatile string displayRoot;

	// Time spent waiting for the network is left out of the speed/ETA estimate.
	private DateTime? networkWaitSince;

	private double networkWaitSeconds;

	private readonly TextBlock logRootLabel = new TextBlock();

	private readonly TextBlock differencesRootLabel = new TextBlock();

	// The window that owns the current job, for the crash handler.
	public static MainWindow Current;

	// Explorer Ctrl+V launches: count down before starting so settings can be
	// reviewed. Any click or key press in the window stops the countdown; the
	// job then starts only from the Start button.
	private DispatcherTimer autoStartTimer;

	private int autoStartRemaining;

	private bool autoStartCompareOnly;

	private int autoStartSeconds = 5;

	private string logPath;

	private string testLogDirectory;

	private string testSnapshotsDirectory;

	private bool scanSnapshot;

	private bool hashSnapshot;

	private SafeLogFile writer;

	private bool busy;

	private bool paused;

	private DateTime started;

	private double activeSeconds;

	private static SolidColorBrush Ink = MakeBrush("Ink");

	private static SolidColorBrush Muted = MakeBrush("Muted");

	private static SolidColorBrush Accent = MakeBrush("Accent");

	private static SolidColorBrush Surface = MakeBrush("Surface");

	private static SolidColorBrush Outline = MakeBrush("Outline");

	private static SolidColorBrush Canvas = MakeBrush("Canvas");

	private static SolidColorBrush Success = MakeBrush("Success");

	private static SolidColorBrush Failure = MakeBrush("Failure");

	private Result countSnapshot = new Result();

	private readonly BulkObservableCollection<ItemResult> results = new BulkObservableCollection<ItemResult>();

	private readonly Queue<ItemResult> rowQueue = new Queue<ItemResult>();

	private readonly DataGrid resultGrid = new DataGrid
	{
		IsReadOnly = true,
		AutoGenerateColumns = false,
		SelectionMode = DataGridSelectionMode.Extended,
		SelectionUnit = DataGridSelectionUnit.FullRow,
		EnableRowVirtualization = true,
		EnableColumnVirtualization = true,
		Margin = new Thickness(8.0)
	};

	private readonly TextBox resultFilter = new TextBox
	{
		MinWidth = 160.0,
		Margin = new Thickness(8.0, 5.0, 0.0, 5.0),
		ToolTip = "Search source, destination, status, or reason"
	};

	private readonly CheckBox issuesOnly = new CheckBox
	{
		Content = "Issues only",
		Margin = new Thickness(12.0, 9.0, 0.0, 4.0)
	};

	private JobRecord loadedJob;

	private JobRecord currentJob;

	private string currentJobFile;

	private DateTime lastCheckpoint;

	private List<Entry> selectedRetry;

	private ExistingRule? selectedRule;

	private bool automaticTest;

	private bool testCancelPreflight;

	private volatile string checkpointWarning;

	// Checkpoints are written on a background thread so a large job file never
	// pauses the copy worker. checkpointSync guards currentJob.Completed while
	// the worker appends and a snapshot is taken; checkpointWrite serialises
	// writes to the job file. Only the newest pending snapshot is written.
	private readonly object checkpointSync = new object();

	private readonly object checkpointWrite = new object();

	private readonly object checkpointQueue = new object();

	private JobRecord pendingCheckpoint;

	private string pendingCheckpointFile;

	private bool checkpointWriterRunning;

	private Task checkpointTask = Task.CompletedTask;

	public int CheckpointWrites;

	private Button syncButton;

	private ItemResult[] pendingSync;

	private string LogDirectory => testLogDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearCopy", "Logs");

	private string PasteConfig => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TeraCopy", "PasteMenu.ini");

	private string JobsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearCopy", "Jobs");

	private string AppDataRoot => Path.GetDirectoryName(Path.GetFullPath(LogDirectory).TrimEnd('\\'));

	private static SolidColorBrush MakeBrush(string name)
	{
		if (palette == null)
		{
			palette = new Palette();
		}
		SolidColorBrush solidColorBrush = new SolidColorBrush();
		BindingOperations.SetBinding(solidColorBrush, SolidColorBrush.ColorProperty, new Binding("[" + name + "]")
		{
			Source = palette
		});
		return solidColorBrush;
	}

	private static void SetColour(string name, string value)
	{
		palette.Set(name, Colour(value));
	}

	private static Color Colour(string value)
	{
		return (Color)ColorConverter.ConvertFromString(value);
	}

	private void InitializeAppearance()
	{
		string[] commandLineArgs = Environment.GetCommandLineArgs();
		appearanceTesting = Array.Exists(commandLineArgs, (string a) => a.StartsWith("--test") || a == "--smoke-test" || a == "--theme-preview");
		bool dark = false;
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\ClearCopy");
			dark = registryKey != null && Convert.ToInt32(registryKey.GetValue("DarkMode", 0)) == 1;
		}
		catch
		{
		}
		if (Array.IndexOf(commandLineArgs, "--dark") >= 0)
		{
			dark = true;
		}
		if (Array.IndexOf(commandLineArgs, "--light") >= 0)
		{
			dark = false;
		}
		using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClearCopy.ico"))
		{
			if (stream != null)
			{
				IconBitmapDecoder iconBitmapDecoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
				Icon = iconBitmapDecoder.Frames[iconBitmapDecoder.Frames.Count - 1];
			}
		}
		Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse("\n<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>\n <Style TargetType='{x:Type Window}'><Setter Property='Background' Value='{DynamicResource Canvas}'/><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='FontFamily' Value='Segoe UI'/><Setter Property='Icon' Value='{DynamicResource AppIcon}'/></Style>\n <Style TargetType='{x:Type ProgressBar}'><Setter Property='Background' Value='{DynamicResource Field}'/><Setter Property='BorderBrush' Value='{DynamicResource Outline}'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ProgressBar}'><Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='3'><Grid x:Name='PART_Track' ClipToBounds='True'><Border x:Name='PART_Indicator' Background='{TemplateBinding Foreground}' HorizontalAlignment='Left' CornerRadius='2'/></Grid></Border><ControlTemplate.Triggers><Trigger Property='IsIndeterminate' Value='True'><Setter TargetName='PART_Indicator' Property='MinWidth' Value='80'/><Trigger.EnterActions><BeginStoryboard x:Name='Pulse'><Storyboard><DoubleAnimation Storyboard.TargetName='PART_Indicator' Storyboard.TargetProperty='Opacity' From='.25' To='1' Duration='0:0:0.7' AutoReverse='True' RepeatBehavior='Forever'/></Storyboard></BeginStoryboard></Trigger.EnterActions><Trigger.ExitActions><RemoveStoryboard BeginStoryboardName='Pulse'/></Trigger.ExitActions></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>\n <Style TargetType='{x:Type ScrollBar}'><Setter Property='Background' Value='{DynamicResource Field}'/><Setter Property='Width' Value='12'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ScrollBar}'><Grid Background='{TemplateBinding Background}'><Track x:Name='PART_Track' IsDirectionReversed='True' Orientation='{TemplateBinding Orientation}'><Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Focusable='False'/></Track.DecreaseRepeatButton><Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='{x:Type Thumb}'><Border Background='{DynamicResource Outline}' CornerRadius='4' Margin='2'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb><Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Focusable='False'/></Track.IncreaseRepeatButton></Track></Grid><ControlTemplate.Triggers><Trigger Property='Orientation' Value='Horizontal'><Setter Property='Width' Value='Auto'/><Setter Property='Height' Value='12'/><Setter TargetName='PART_Track' Property='IsDirectionReversed' Value='False'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>\n <Style TargetType='{x:Type TextBlock}'><Setter Property='Foreground' Value='{DynamicResource Ink}'/></Style>\n <Style TargetType='{x:Type Button}'>\n  <Setter Property='Background' Value='{DynamicResource Surface}'/><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='BorderBrush' Value='{DynamicResource Outline}'/><Setter Property='BorderThickness' Value='1'/><Setter Property='Cursor' Value='Hand'/>\n  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type Button}'><Border x:Name='Chrome' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='5' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='Background' Value='{DynamicResource Hover}'/><Setter TargetName='Chrome' Property='BorderBrush' Value='{DynamicResource Accent}'/></Trigger><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='{DynamicResource Accent}'/></Trigger><Trigger Property='IsPressed' Value='True'><Setter TargetName='Chrome' Property='Opacity' Value='.75'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter TargetName='Chrome' Property='Opacity' Value='.4'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>\n </Style>\n <Style TargetType='{x:Type TextBox}'><Setter Property='Background' Value='{DynamicResource Field}'/><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='CaretBrush' Value='{DynamicResource Ink}'/><Setter Property='BorderBrush' Value='{DynamicResource Outline}'/><Setter Property='SelectionBrush' Value='{DynamicResource Accent}'/><Setter Property='Padding' Value='6,4'/></Style>\n <Style TargetType='{x:Type ListBox}'><Setter Property='Background' Value='{DynamicResource Field}'/><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='BorderBrush' Value='{DynamicResource Outline}'/><Setter Property='Padding' Value='5'/></Style>\n <Style TargetType='{x:Type ListBoxItem}'><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='Padding' Value='4'/><Style.Triggers><Trigger Property='IsSelected' Value='True'><Setter Property='Background' Value='{DynamicResource Selected}'/></Trigger></Style.Triggers></Style>\n <Style TargetType='{x:Type CheckBox}'><Setter Property='Foreground' Value='{DynamicResource Ink}'/></Style>\n <Style TargetType='{x:Type Expander}'><Setter Property='Foreground' Value='{DynamicResource Ink}'/></Style>\n <Style TargetType='{x:Type ComboBox}'>\n  <Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='Background' Value='{DynamicResource Field}'/><Setter Property='BorderBrush' Value='{DynamicResource Outline}'/>\n  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ComboBox}'><Grid><ToggleButton Focusable='False' IsChecked='{Binding IsDropDownOpen,RelativeSource={RelativeSource TemplatedParent},Mode=TwoWay}'><ToggleButton.Template><ControlTemplate TargetType='{x:Type ToggleButton}'><Border Background='{DynamicResource Field}' BorderBrush='{DynamicResource Outline}' BorderThickness='1' CornerRadius='4'><DockPanel Margin='8,6'><TextBlock DockPanel.Dock='Right' Text='⌄' Margin='10,0,0,0'/><ContentPresenter Content='{Binding SelectionBoxItem,RelativeSource={RelativeSource AncestorType=ComboBox}}'/></DockPanel></Border></ControlTemplate></ToggleButton.Template></ToggleButton><Popup IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' Focusable='False'><Border Background='{DynamicResource Surface}' BorderBrush='{DynamicResource Outline}' BorderThickness='1' MinWidth='{Binding ActualWidth,RelativeSource={RelativeSource TemplatedParent}}'><ScrollViewer MaxHeight='300'><ItemsPresenter/></ScrollViewer></Border></Popup></Grid></ControlTemplate></Setter.Value></Setter>\n </Style>\n <Style TargetType='{x:Type ComboBoxItem}'><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='Padding' Value='8'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ComboBoxItem}'><Border x:Name='Row' Background='{DynamicResource Surface}' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Row' Property='Background' Value='{DynamicResource Hover}'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>\n <Style TargetType='{x:Type TabControl}'><Setter Property='Background' Value='{DynamicResource Surface}'/><Setter Property='BorderBrush' Value='{DynamicResource Outline}'/></Style>\n <Style TargetType='{x:Type TabItem}'><Setter Property='Foreground' Value='{DynamicResource Muted}'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type TabItem}'><Border x:Name='Tab' Background='{DynamicResource Canvas}' BorderBrush='Transparent' BorderThickness='0,0,0,3' Padding='12,9'><ContentPresenter ContentSource='Header'/></Border><ControlTemplate.Triggers><Trigger Property='IsSelected' Value='True'><Setter TargetName='Tab' Property='Background' Value='{DynamicResource Surface}'/><Setter TargetName='Tab' Property='BorderBrush' Value='{DynamicResource Accent}'/><Setter Property='Foreground' Value='{DynamicResource Ink}'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>\n <Style TargetType='{x:Type DataGrid}'><Setter Property='Background' Value='{DynamicResource Field}'/><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='BorderBrush' Value='{DynamicResource Outline}'/><Setter Property='RowBackground' Value='{DynamicResource Field}'/><Setter Property='AlternatingRowBackground' Value='{DynamicResource Surface}'/><Setter Property='HorizontalGridLinesBrush' Value='{DynamicResource Outline}'/><Setter Property='VerticalGridLinesBrush' Value='{DynamicResource Outline}'/><Setter Property='GridLinesVisibility' Value='Horizontal'/><Setter Property='RowHeaderWidth' Value='0'/></Style>\n <Style TargetType='{x:Type DataGridColumnHeader}'><Setter Property='Background' Value='{DynamicResource Surface}'/><Setter Property='Foreground' Value='{DynamicResource Muted}'/><Setter Property='Padding' Value='8,10'/><Setter Property='BorderBrush' Value='{DynamicResource Outline}'/><Setter Property='BorderThickness' Value='0,0,0,1'/></Style>\n <Style TargetType='{x:Type DataGridCell}'><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='Padding' Value='6'/><Setter Property='BorderThickness' Value='0'/><Style.Triggers><Trigger Property='IsSelected' Value='True'><Setter Property='Background' Value='{DynamicResource Selected}'/></Trigger></Style.Triggers></Style>\n <Style TargetType='{x:Type ToolTip}'><Setter Property='Background' Value='{DynamicResource Surface}'/><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='BorderBrush' Value='{DynamicResource Outline}'/></Style>\n</ResourceDictionary>"));
		Application.Current.Resources["AppIcon"] = Icon;
		ApplyAppearance(dark, save: false);
		SourceInitialized += (object param0, EventArgs param1) =>
		{
			TintTitleBar();
		};
	}

	private void ApplyAppearance(bool dark, bool save)
	{
		darkMode = dark;
		SetColour("Ink", dark ? "#E6EDF6" : "#172B45");
		SetColour("Muted", dark ? "#A8B8CC" : "#536780");
		SetColour("Accent", dark ? "#60A5FA" : "#1466C6");
		SetColour("Surface", dark ? "#172232" : "#FFFFFF");
		SetColour("Outline", dark ? "#34455B" : "#CFDCEB");
		SetColour("Canvas", dark ? "#0E1623" : "#EDF3FA");
		SetColour("Success", dark ? "#6EE7B7" : "#14734F");
		SetColour("Failure", dark ? "#FF9D9D" : "#B42335");
		ResourceDictionary resources = Application.Current.Resources;
		resources["Ink"] = Ink;
		resources["Muted"] = Muted;
		resources["Accent"] = Accent;
		resources["Surface"] = Surface;
		resources["Outline"] = Outline;
		resources["Canvas"] = Canvas;
		resources["Field"] = new SolidColorBrush(Colour(dark ? "#101A28" : "#F9FBFE"));
		// The corner where a text box's two scrollbars meet is painted with the
		// system control colour; without this it shows as a white square in dark mode.
		resources[SystemColors.ControlBrushKey] = resources["Field"];
		resources["Hover"] = new SolidColorBrush(Colour(dark ? "#263D59" : "#E3EEFC"));
		resources["Selected"] = new SolidColorBrush(Colour(dark ? "#23496F" : "#D6E8FF"));
		resources["Primary"] = new SolidColorBrush(Colour(dark ? "#256BC4" : "#1466C6"));
		UpdateThemeLabel();
		TintTitleBar();
		if (!save || appearanceTesting)
		{
			return;
		}
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\ClearCopy");
			registryKey.SetValue("DarkMode", dark ? 1 : 0);
		}
		catch (Exception ex)
		{
			status.Text = "Theme changed, but could not save preference: " + ex.Message;
		}
	}

	private void UpdateThemeLabel()
	{
		if (themeButton != null)
		{
			themeButton.Content = new TextBlock
			{
				Text = "\ue82f",
				FontFamily = new FontFamily("Segoe MDL2 Assets"),
				FontSize = 16.0,
				VerticalAlignment = VerticalAlignment.Center
			};
			themeButton.ToolTip = darkMode ? "Switch to light mode" : "Switch to dark mode";
			AutomationProperties.SetName(themeButton, darkMode ? "Switch to light mode" : "Switch to dark mode");
		}
	}

	private void TintTitleBar()
	{
		IntPtr handle = new WindowInteropHelper(this).Handle;
		if (handle == IntPtr.Zero)
		{
			return;
		}
		try
		{
			int value = (darkMode ? 1 : 0);
			if (DwmSetWindowAttribute(handle, 20, ref value, 4) != 0)
			{
				DwmSetWindowAttribute(handle, 19, ref value, 4);
			}
		}
		catch
		{
		}
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

	private static object ButtonContent(string label, string glyph)
	{
		StackPanel stackPanel = new StackPanel();
		stackPanel.Orientation = Orientation.Horizontal;
		StackPanel stackPanel2 = stackPanel;
		stackPanel2.Children.Add(new TextBlock
		{
			Text = glyph,
			FontFamily = new FontFamily("Segoe MDL2 Assets"),
			FontSize = 14.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 7.0, 0.0)
		});
		stackPanel2.Children.Add(new TextBlock
		{
			Text = label,
			VerticalAlignment = VerticalAlignment.Center
		});
		return stackPanel2;
	}

	private static string ActionGlyph(string label)
	{
		if (label.Contains("Sync"))
		{
			return "\ue895";
		}
		if (label.Contains("Compare"))
		{
			return "\ue9d9";
		}
		if (label.Contains("copy") || label.Contains("Copy"))
		{
			return "\ue8c8";
		}
		if (label.Contains("Pause"))
		{
			return "\ue769";
		}
		if (label.Contains("Resume"))
		{
			return "\ue768";
		}
		if (label.Contains("Cancel") || label.Contains("Remove"))
		{
			return "\ue711";
		}
		if (label.Contains("Paste"))
		{
			return "\ue77f";
		}
		if (label.Contains("Save") || label.Contains("Export"))
		{
			return "\ue74e";
		}
		if (label.Contains("folder") || label.Contains("logs") || label.Contains("Load"))
		{
			return "\ue8b7";
		}
		if (label.Contains("files"))
		{
			return "\ue8a5";
		}
		if (label.Contains("Retry") || label.Contains("Repair"))
		{
			return "\ue72c";
		}
		return "\ue713";
	}

	private void RefreshInstallButton()
	{
		if (installButton == null)
		{
			return;
		}
		string text = "Install";
		try
		{
			bool flag = File.Exists(SelfInstall.MachinePath);
			Version version = (flag ? new Version(FileVersionInfo.GetVersionInfo(SelfInstall.MachinePath).FileVersion) : new Version(0, 0));
			Version version2 = Assembly.GetExecutingAssembly().GetName().Version;
			bool complete = false;
			using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\ClearCopy"))
			{
				complete = registryKey != null && string.Equals(Convert.ToString(registryKey.GetValue("CompleteMachineSetupVersion", "")), version.ToString(), StringComparison.OrdinalIgnoreCase);
			}
			text = SelfInstall.SetupButton(flag, version, version2, complete);
		}
		catch
		{
			text = "Install";
		}
		ShowInstallButton(text);
	}

	private void ShowInstallButton(string label)
	{
		installButton.Visibility = ((label == null) ? Visibility.Collapsed : Visibility.Visible);
		if (label != null)
		{
			installButton.Content = ButtonContent(label, "\ue896");
			AutomationProperties.SetName(installButton, label);
		}
	}

	private async void InstallProgramFiles()
	{
		if (busy || installing)
		{
			return;
		}
		installing = true;
		bool wasEnabled = false;
		foreach (Control item in locked)
		{
			item.IsEnabled = false;
		}
		try
		{
			int num = default;
			_ = num;
			_ = 0;
			try
			{
				wasEnabled = PasteHelper.Enabled;
				await Task.Run(() =>
				{
					SelfInstall.CompleteSetup(() =>
					{
						PasteHelper.Stop();
						if (!string.Equals(SelfInstall.CurrentPath, SelfInstall.MachinePath, StringComparison.OrdinalIgnoreCase))
						{
							using (Process process = Process.Start(new ProcessStartInfo(SelfInstall.CurrentPath, "--install-machine")
							{
								UseShellExecute = true,
								Verb = "runas"
							}))
							{
								process.WaitForExit();
								if (process.ExitCode != 0)
								{
									throw new IOException("Could not replace the installed EXE. Close other installed ClearCopy windows and retry.");
								}
							}
						}
					}, SelfInstall.CreateShortcut, () =>
					{
						bool configured = false;
						Dispatcher.Invoke(() =>
						{
							configured = ConfigureIntegration(repair: true);
						});
						if (!configured)
						{
							throw new IOException("Explorer integration could not be registered. Use Install / Finish setup to retry.");
						}
					}, PasteHelper.Enable, () =>
					{
						using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\ClearCopy");
						registryKey.SetValue("CompleteMachineSetupVersion", FileVersionInfo.GetVersionInfo(SelfInstall.MachinePath).FileVersion);
					}, (string message) =>
					{
						Dispatcher.Invoke(() =>
						{
							status.Text = message;
							installButton.Content = (message.StartsWith("Installed.") ? "Done" : "Installing…");
						});
					});
				});
				status.Foreground = Success;
			}
			catch (Win32Exception ex)
			{
				status.Foreground = Failure;
				status.Text = ((ex.NativeErrorCode == 1223) ? "Installation cancelled at the Windows administrator prompt. Click Install to try again." : ("Installation failed: " + ex.Message));
				if (wasEnabled)
				{
					try
					{
						PasteHelper.RestartIfEnabled();
						return;
					}
					catch
					{
						return;
					}
				}
			}
			catch (Exception ex2)
			{
				status.Foreground = Failure;
				status.Text = "Setup is incomplete: " + ex2.Message + " You can retry using the button above.";
				if (wasEnabled)
				{
					try
					{
						PasteHelper.RestartIfEnabled();
						return;
					}
					catch
					{
						return;
					}
				}
			}
		}
		finally
		{
			installing = false;
			foreach (Control item2 in locked)
			{
				item2.IsEnabled = true;
			}
			RefreshInstallButton();
		}
	}

	private void EnablePasteChooser()
	{
		PasteHelper.Enable();
		status.Text = "Explorer Ctrl+V chooser enabled. ClearCopy's main window can close; the helper runs at sign-in. Copy files, open the destination folder in Explorer and press Ctrl+V. Native right-click Paste is unchanged. If TeraCopy also handles paste, disable its default copy handler to avoid competing prompts.";
	}

	private void DisablePasteChooser()
	{
		PasteHelper.Disable();
		status.Text = "Explorer Ctrl+V chooser disabled; normal Windows pasting restored.";
	}

	private void InstallOnThisPc()
	{
		InstallProgramFiles();
	}

	public MainWindow(string[] args)
	{
		MainWindow mainWindow = this;
		Current = this;
		BuildLayout();
		if (!appearanceTesting)
		{
			Loaded += (object param0, RoutedEventArgs param1) =>
			{
				_ = mainWindow.ApplyLogRetentionAsync();
				try
				{
					using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\ClearCopy");
					if (registryKey != null && Convert.ToInt32(registryKey.GetValue("CompactView", 0)) == 1)
					{
						mainWindow.SetCompact(compact: true, save: false);
					}
				}
				catch
				{
				}
			};
		}
		if (args.Contains("--install-on-this-pc"))
		{
			Loaded += (object param0, RoutedEventArgs param1) =>
			{
				try
				{
					InstallOnThisPc();
				}
				catch (Exception ex3)
				{
					status.Text = "Installation could not finish: " + ex3.Message;
					MessageBox.Show(this, status.Text, "ClearCopy installation");
				}
			};
		}
		int themePreview = Array.IndexOf(args, "--theme-preview");
		if (themePreview >= 0 && args.Length > themePreview + 1)
		{
			Loaded += (object param0, RoutedEventArgs param1) =>
			{
				DispatcherTimer timer = new DispatcherTimer
				{
					Interval = TimeSpan.FromMilliseconds(500.0)
				};
				timer.Tick += (object obj, EventArgs e) =>
				{
					timer.Stop();
					try
					{
						Directory.CreateDirectory(args[themePreview + 1]);
						mainWindow.ApplyAppearance(dark: false, save: false);
						mainWindow.UpdateLayout();
						mainWindow.SavePreview(Path.Combine(args[themePreview + 1], "Light.png"));
						mainWindow.themeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
						if (!mainWindow.darkMode)
						{
							throw new Exception("Theme toggle failed");
						}
						mainWindow.UpdateLayout();
						mainWindow.SavePreview(Path.Combine(args[themePreview + 1], "Dark.png"));
						mainWindow.pages.SelectedIndex = 3;
						mainWindow.results.Add(new ItemResult
						{
							Source = "C:\\Source\\example.txt",
							Target = "D:\\Copy\\example.txt",
							Status = "Missing",
							Reason = "Destination file is absent"
						});
						mainWindow.UpdateLayout();
						mainWindow.SavePreview(Path.Combine(args[themePreview + 1], "Dark-results.png"));
						File.WriteAllText(Path.Combine(args[themePreview + 1], "theme-check.txt"), "Theme toggle, icon resource and dark results rendered successfully.");
					}
					catch (Exception ex3)
					{
						File.WriteAllText(Path.Combine(args[themePreview + 1], "theme-error.txt"), ex3.ToString());
					}
					mainWindow.Close();
				};
				timer.Start();
			};
		}
		if (args.Contains("--small-window"))
		{
			Width = 820.0;
			Height = 680.0;
		}
		int num = Array.IndexOf(args, "--test-snapshots");
		if (num >= 0 && args.Length > num + 1)
		{
			testSnapshotsDirectory = args[num + 1];
		}
		AllowDrop = true;
		DragOver += (object s, DragEventArgs e) =>
		{
			e.Effects = ((!busy && e.Data.GetDataPresent(DataFormats.FileDrop)) ? DragDropEffects.Copy : DragDropEffects.None);
			e.Handled = true;
		};
		Drop += (object s, DragEventArgs e) =>
		{
			if (!busy && e.Data.GetDataPresent(DataFormats.FileDrop))
			{
				AddSources((string[])e.Data.GetData(DataFormats.FileDrop));
			}
		};
		PreviewKeyDown += (object s, KeyEventArgs e) =>
		{
			if (!busy && e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && !(Keyboard.FocusedElement is TextBox))
			{
				Paste();
				e.Handled = true;
			}
		};
		Closing += (object s, CancelEventArgs e) =>
		{
			if (installing)
			{
				e.Cancel = true;
				status.Text = "Wait for installation to finish before closing.";
			}
			else if (busy)
			{
				e.Cancel = true;
				MessageBox.Show(this, "Cancel the job and wait for it to stop before closing.");
			}
		};
		if (args.Length >= 2 && args[0] == "--paste")
		{
			directCompare.IsChecked = false;
			destination.Text = args[1];
			Loaded += (object param0, RoutedEventArgs param1) =>
			{
				Paste();
			};
		}
		if (args.Length == 0 || args.Contains("--setup-explorer"))
		{
			Loaded += (object param0, RoutedEventArgs param1) =>
			{
				mainWindow.ConfigureIntegration(args.Contains("--setup-explorer"));
				try
				{
					if (PasteHelper.Enabled && !PasteHelper.Running)
					{
						PasteHelper.RestartIfEnabled();
					}
				}
				catch (Exception ex3)
				{
					TextBlock textBlock = mainWindow.integrationStatus;
					textBlock.Text = textBlock.Text + " Paste helper could not restart: " + ex3.Message;
				}
			};
		}
		if (args.Length >= 3 && args[0] == "--from-list")
		{
			try
			{
				string[] paths = PasteIntegration.ReadList(args[1]);
				directCompare.IsChecked = args.Contains("--contents");
				destination.Text = args[2];
				AddSources(paths);
				if (args.Contains("--start"))
				{
					Loaded += (object param0, RoutedEventArgs param1) =>
					{
						mainWindow.BeginAutoStart(args.Contains("--compare-only"));
					};
				}
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				Loaded += (object param0, RoutedEventArgs param1) =>
				{
					MessageBox.Show(mainWindow, "Cannot load copied items: " + ex2.Message);
				};
			}
		}
		int housekeepingIndex = Array.IndexOf(args, "--test-housekeeping");
		if (housekeepingIndex >= 0 && args.Length > housekeepingIndex + 2)
		{
			Loaded += (object param0, RoutedEventArgs param1) =>
			{
				mainWindow.OpenHousekeeping(args[housekeepingIndex + 1], args[housekeepingIndex + 2]);
				mainWindow.Close();
			};
		}
		int resumeIndex = Array.IndexOf(args, "--resume-job");
		if (resumeIndex >= 0 && args.Length > resumeIndex + 1)
		{
			Loaded += (object param0, RoutedEventArgs param1) =>
			{
				try
				{
					mainWindow.LoadDefinition(args[resumeIndex + 1]);
					if (args.Contains("--start"))
					{
						mainWindow.Start(mainWindow.loadedJob.CompareOnly);
					}
				}
				catch (Exception ex3)
				{
					mainWindow.status.Text = "Cannot load job: " + ex3.Message;
				}
			};
		}
		int reportIndex = Array.IndexOf(args, "--test-report");
		automaticTest = reportIndex >= 0 || args.Contains("--smoke-test");
		testCancelPreflight = args.Contains("--test-cancel-preflight");
		if (reportIndex >= 0 && args.Length > reportIndex + 1)
		{
			testLogDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[reportIndex + 1])), "logs");
		}
		if (reportIndex >= 0 && args.Length > reportIndex + 1)
		{
			Loaded += (object param0, RoutedEventArgs param1) =>
			{
				DispatcherTimer timer = new DispatcherTimer
				{
					Interval = TimeSpan.FromMilliseconds(100.0)
				};
				int ticks = 0;
				bool repaired = false;
				bool synced = false;
				timer.Tick += (object obj, EventArgs e) =>
				{
					if (!mainWindow.busy || ++ticks >= 300)
					{
						timer.Stop();
						mainWindow.FlushLog();
						mainWindow.DrainResults();
						if (args.Contains("--test-repair") && !repaired)
						{
							repaired = true;
							ItemResult[] array = mainWindow.results.Where((ItemResult r) => r.Status == "Missing" || r.Status == "Mismatch").ToArray();
							foreach (ItemResult value in array)
							{
								mainWindow.resultGrid.SelectedItems.Add(value);
							}
							mainWindow.RepairSelected();
							timer.Start();
						}
						else if (args.Contains("--test-sync") && !synced)
						{
							synced = true;
							if (!mainWindow.syncButton.IsEnabled)
							{
								throw new InvalidOperationException("Sync button should be enabled after differences");
							}
							mainWindow.syncButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
							timer.Start();
						}
						else
						{
							File.WriteAllText(args[reportIndex + 1], mainWindow.status.Text + Environment.NewLine + mainWindow.Summary(mainWindow.countSnapshot));
							File.WriteAllText(args[reportIndex + 1] + ".differences.txt", mainWindow.differences.Text);
							mainWindow.UpdateLayout();
							bool flag = mainWindow.pages.SelectedIndex == mainWindow.pages.Items.Count - 1;
							int num3 = 0;
							if (flag)
							{
								for (int num4 = 0; num4 < mainWindow.resultGrid.Items.Count; num4++)
								{
									if (mainWindow.resultGrid.ItemContainerGenerator.ContainerFromIndex(num4) is DataGridRow { IsVisible: not false } dataGridRow)
									{
										double y = dataGridRow.TranslatePoint(new Point(0.0, 0.0), mainWindow.resultGrid).Y;
										if (y >= 0.0 && y + dataGridRow.ActualHeight <= mainWindow.resultGrid.ActualHeight)
										{
											num3++;
										}
									}
								}
							}
							File.WriteAllText(args[reportIndex + 1] + ".layout.txt", string.Format("Window {0:0}x{1:0}; tabs area {2:0} px; results grid {3:0} px; about {4} row(s) visible of {5}{6}", mainWindow.ActualWidth, mainWindow.ActualHeight, mainWindow.pages.ActualHeight, mainWindow.resultGrid.ActualHeight, num3, mainWindow.results.Count, (flag && mainWindow.results.Count > 0 && num3 < Math.Min(3, mainWindow.results.Count)) ? " — LAYOUT WARNING: result rows hidden" : ""));
							Jobs.Export(args[reportIndex + 1] + ".csv", mainWindow.results);
							if (mainWindow.testSnapshotsDirectory != null)
							{
								mainWindow.SavePreview(Path.Combine(mainWindow.testSnapshotsDirectory, "Completed.png"));
							}
							if (mainWindow.busy)
							{
								mainWindow.cancellation.Cancel();
							}
							else
							{
								mainWindow.Close();
							}
						}
					}
				};
				timer.Start();
			};
		}
		if (args.Length < 2 || !(args[0] == "--smoke-test"))
		{
			return;
		}
		Loaded += (object param0, RoutedEventArgs param1) =>
		{
			DispatcherTimer timer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(500.0)
			};
			timer.Tick += (object obj, EventArgs e) =>
			{
				timer.Stop();
				RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap((int)mainWindow.ActualWidth, (int)mainWindow.ActualHeight, 96.0, 96.0, PixelFormats.Pbgra32);
				renderTargetBitmap.Render(mainWindow);
				PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder
				{
					Frames = { BitmapFrame.Create(renderTargetBitmap) }
				};
				using (FileStream stream = File.Create(args[1]))
				{
					pngBitmapEncoder.Save(stream);
				}
				mainWindow.Close();
			};
			timer.Start();
		};
	}

	private Button AddButton(Panel panel, string text, Action action, bool lockDuringJob)
	{
		Button button = new Button();
		button.Content = ButtonContent(text, ActionGlyph(text));
		button.Padding = new Thickness(10.0, 8.0, 10.0, 8.0);
		button.Margin = new Thickness(0.0, 4.0, 6.0, 4.0);
		Button button2 = button;
		AutomationProperties.SetName(button2, text);
		button2.Click += (object param0, RoutedEventArgs param1) =>
		{
			try
			{
				action();
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, ex.Message, "ClearCopy");
			}
		};
		panel.Children.Add(button2);
		if (lockDuringJob)
		{
			locked.Add(button2);
		}
		return button2;
	}

	private string PickFolder(string initial)
	{
		FolderPicker folderPicker = new FolderPicker(this, initial);
		if (folderPicker.ShowDialog() != true)
		{
			return null;
		}
		return folderPicker.SelectedPath;
	}

	private void AddSources(IEnumerable<string> paths)
	{
		foreach (string path in paths)
		{
			if (!sources.Items.Contains(path))
			{
				sources.Items.Add(path);
			}
		}
	}

	private void BeginAutoStart(bool compareOnly)
	{
		StopAutoStart(null);
		autoStartCompareOnly = compareOnly;
		autoStartRemaining = Math.Max(1, autoStartSeconds);
		PreviewMouseDown += AutoStartInterrupted;
		PreviewKeyDown += AutoStartInterrupted;
		autoStartTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(1.0)
		};
		autoStartTimer.Tick += (object s, EventArgs e) =>
		{
			autoStartRemaining--;
			if (autoStartRemaining > 0)
			{
				ShowAutoStartCountdown();
				return;
			}
			StopAutoStart(null);
			if (!busy && !starting)
			{
				Start(autoStartCompareOnly);
			}
		};
		ShowAutoStartCountdown();
		autoStartTimer.Start();
		try
		{
			Activate();
		}
		catch
		{
		}
	}

	private void ShowAutoStartCountdown()
	{
		object selected = mode.SelectedItem;
		string what = autoStartCompareOnly ? "Compare" : ((selected as ComboBoxItem)?.Content?.ToString() ?? selected?.ToString() ?? "The copy");
		phaseText.Text = "Starting in " + autoStartRemaining + " s…";
		status.Foreground = Ink;
		status.Text = what + " will start automatically. Click anywhere in this window (or press any key) to stop the countdown and change settings; then press Start selected mode.";
	}

	private void AutoStartInterrupted(object sender, EventArgs e)
	{
		StopAutoStart("Countdown stopped. Review the settings, then press Start selected mode when ready.");
	}

	private void StopAutoStart(string message)
	{
		PreviewMouseDown -= AutoStartInterrupted;
		PreviewKeyDown -= AutoStartInterrupted;
		if (autoStartTimer == null)
		{
			return;
		}
		autoStartTimer.Stop();
		autoStartTimer = null;
		if (message != null)
		{
			phaseText.Text = "Ready to copy or compare";
			status.Text = message;
		}
	}

	private bool AutoStartPending => autoStartTimer != null;

	private void Paste()
	{
		try
		{
			if (Clipboard.ContainsFileDropList())
			{
				AddSources(Clipboard.GetFileDropList().Cast<string>());
			}
			else
			{
				MessageBox.Show(this, "Copy files or folders in File Explorer first (Ctrl+C).");
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, "Clipboard unavailable: " + ex.Message);
		}
	}

	private void Append(string line)
	{
		string text = DateTime.Now.ToString("HH:mm:ss") + "  " + line;
		if (writer != null)
		{
			writer.WriteLine(text);
		}
		else if (!busy && !string.IsNullOrEmpty(logPath))
		{
			// 0.4.2: after a job (e.g. the Fix access check) the line also goes into
			// that job's log file; the activity log display is cleared by the next job.
			try
			{
				File.AppendAllText(logPath, text + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			}
			catch
			{
			}
		}
		string shown = (line != null && line.StartsWith("START ", StringComparison.Ordinal)) ? text : RelativeToRoot(text, displayRoot);
		lock (uiLog)
		{
			uiLog.Enqueue(shown);
		}
	}

	// Removes every occurrence of root (case-insensitive) so a line shows the
	// path after it. Display only.
	public static string RelativeToRoot(string text, string root)
	{
		if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(root))
		{
			return text;
		}
		int index = text.IndexOf(root, StringComparison.OrdinalIgnoreCase);
		if (index < 0)
		{
			return text;
		}
		StringBuilder builder = new StringBuilder(text.Length);
		int start = 0;
		while (index >= 0)
		{
			builder.Append(text, start, index - start);
			start = index + root.Length;
			index = text.IndexOf(root, start, StringComparison.OrdinalIgnoreCase);
		}
		builder.Append(text, start, text.Length - start);
		return builder.ToString();
	}

	private DockPanel WithRootLabel(TextBox box, TextBlock label)
	{
		label.Margin = new Thickness(10.0, 8.0, 10.0, 0.0);
		label.FontSize = 11.0;
		label.Foreground = Muted;
		label.TextTrimming = TextTrimming.CharacterEllipsis;
		label.Visibility = Visibility.Collapsed;
		label.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Text")
		{
			Source = label
		});
		DockPanel panel = new DockPanel();
		DockPanel.SetDock(label, Dock.Top);
		panel.Children.Add(label);
		panel.Children.Add(box);
		return panel;
	}

	private void SetDisplayRoot(string root)
	{
		displayRoot = string.IsNullOrEmpty(root) ? null : (root.TrimEnd('\\') + "\\");
		foreach (TextBlock label in new[] { logRootLabel, differencesRootLabel })
		{
			label.Text = (displayRoot == null) ? "" : ("Paths below are shown after the destination: " + displayRoot);
			label.Visibility = (displayRoot == null) ? Visibility.Collapsed : Visibility.Visible;
		}
	}

	private void TogglePause()
	{
		if (engine != null)
		{
			paused = !paused;
			if (paused)
			{
				activeSeconds += (DateTime.UtcNow - started).TotalSeconds;
				engine.Gate.Reset();
			}
			else
			{
				started = DateTime.UtcNow;
				engine.Gate.Set();
			}
			pause.Content = ButtonContent(paused ? "Resume" : "Pause", ActionGlyph(paused ? "Resume" : "Pause"));
			RenderProgress();
		}
	}

	private async void Start(bool compareOnly)
	{
		if (busy)
		{
			return;
		}
		bool explicitRepair = selectedRepair;
		selectedRepair = false;
		if (sources.Items.Count == 0 || string.IsNullOrWhiteSpace(destination.Text))
		{
			MessageBox.Show(this, "Add source items and choose a destination.");
			return;
		}
		string dest;
		string[] paths;
		try
		{
			dest = Path.GetFullPath(destination.Text);
			paths = sources.Items.Cast<string>().Select(Path.GetFullPath).ToArray();
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, "Invalid source or destination path: " + ex.Message, "ClearCopy");
			return;
		}
		if (starting)
		{
			return;
		}
		string jobId = Guid.NewGuid().ToString("N");
		string jobFile = Path.Combine(automaticTest ? Path.Combine(Path.GetDirectoryName(testLogDirectory), "jobs") : JobsDirectory, jobId + ".clearcopy.xml");
		starting = true;
		int num = default;
		IDisposable acquiredLock;
		try
		{
			_ = num;
			_ = 0;
			try
			{
				if (Housekeeping.CleanupRunning(Path.GetDirectoryName(jobFile)))
				{
					status.Text = "Waiting for Housekeeping to finish removing files…";
				}
				string lockDestination = dest;
				acquiredLock = await Task.Run(() => Housekeeping.AcquireJobLock(jobFile, lockDestination, TimeSpan.FromSeconds(60.0), CancellationToken.None));
			}
			catch (Exception ex2)
			{
				status.Foreground = Failure;
				status.Text = "Job not started: " + ex2.Message;
				MessageBox.Show(this, status.Text, "ClearCopy");
				return;
			}
		}
		finally
		{
			starting = false;
		}
		bool unchanged;
		try
		{
			unchanged = string.Equals(Path.GetFullPath(destination.Text), dest, StringComparison.OrdinalIgnoreCase) && sources.Items.Cast<string>().Select(Path.GetFullPath).SequenceEqual(paths, StringComparer.OrdinalIgnoreCase);
		}
		catch
		{
			unchanged = false;
		}
		if (!unchanged || busy)
		{
			acquiredLock.Dispose();
			if (!busy)
			{
				status.Text = "Job not started: the source or destination changed while waiting. Start it again.";
			}
			return;
		}
		ExistingRule rule = (ExistingRule)(((int?)selectedRule) ?? rules.SelectedIndex);
		selectedRule = null;
		OperationMode operationMode = compareOnly ? OperationMode.Compare : (OperationMode)mode.SelectedIndex;
		bool repairing = explicitRepair || operationMode == OperationMode.VerifiedCopy && loadedJob != null && loadedJob.RepairOnly && SameSavedSelection(paths, dest);
		if (repairing) operationMode = OperationMode.VerifiedCopy;
		compareOnly = operationMode == OperationMode.Compare;
		bool hash = operationMode != OperationMode.FastCopy;
		bool confirmNewer = askNewer.IsChecked == true;
		bool compareContents = directCompare.IsChecked == true && paths.Length == 1;
		bool reviewCopy = reviewBeforeCopy.IsChecked == true || testCancelPreflight;
		ItemResult[] syncRows = pendingSync;
		pendingSync = null;
		bool syncing = syncRows != null;
		busy = true;
		paused = false;
		foreach (Control item in locked)
		{
			item.IsEnabled = false;
		}
		pause.IsEnabled = true;
		cancel.IsEnabled = true;
		UpdateSyncButton();
		lock (uiLog)
		{
			uiLog.Clear();
		}
		log.Clear();
		differences.Clear();
		status.Foreground = Ink;
		bar.Value = 0.0;
		bar.IsIndeterminate = true;
		cancellation = new CancellationTokenSource();
		started = DateTime.UtcNow;
		activeSeconds = 0.0;
		networkWaitSince = null;
		networkWaitSeconds = 0.0;
		lock (this)
		{
			pendingUpdate = null;
		}
		lastProgress = DateTime.UtcNow;
		pages.SelectedIndex = 1;
		phaseText.Text = "Preparing job";
		percentText.Text = "…";
		fileBar.Value = 0.0;
		ShowCounts(new Result());
		TextBlock textBlock = jobSummary;
		string text;
		if (syncing)
		{
			text = "REPAIR CONTENT DIFFERENCES + COMPARE  •  ";
		}
		else
		{
			text = repairing ? "REPAIR + SHA-256  •  " : (compareOnly ? "COMPARE ONLY  •  " : operationMode == OperationMode.FastCopy ? "FAST COPY — NOT VERIFIED  •  " : "VERIFIED COPY  •  ");
		}
		textBlock.Text = text + ((paths.Length == 1) ? paths[0] : (paths.Length + " source items")) + " → " + dest;
		jobSummary.ToolTip = string.Join(Environment.NewLine, paths) + Environment.NewLine + "→ " + dest;
		List<Entry> retryEntries = selectedRetry;
		if (retryEntries == null && SameSavedSelection(paths, dest))
		{
			retryEntries = SavedEntries();
		}
		selectedRetry = null;
		if (retryEntries == null)
		{
			accessSkipped.Clear();
		}
		lock (rowQueue)
		{
			rowQueue.Clear();
		}
		results.Clear();
		keptNormalRows = 0;
		droppedRows = 0;
		fullResultsPath = null;
		UpdateFullListButton();
		ClearStatusFilter();
		checkpointWarning = null;
		currentJob = CaptureJob(compareOnly);
		currentJob.Mode = operationMode;
		currentJob.RepairOnly = repairing;
		currentJob.Verify = hash;
		bool inheritScope = (retryEntries != null || syncing) && loadedJob?.CompareSources != null;
		currentJob.CompareSources = inheritScope ? loadedJob.CompareSources : paths;
		currentJob.CompareContents = inheritScope ? loadedJob.CompareContents : compareContents;
		currentJob.Id = jobId;
		currentJob.Rule = rule;
		currentJob.State = "Running";
		currentJob.Sync = syncing;
		if (retryEntries != null)
		{
			currentJob.Selection = retryEntries.Select((Entry e) => new ItemResult
			{
				Source = e.Source,
				Target = e.Target,
				Directory = e.Directory,
				Size = e.Length,
				Status = "Selected"
			}).ToList();
			currentJob.Sources = retryEntries.Select((Entry e) => e.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		}
		currentJobFile = jobFile;
		jobLock = acquiredLock;
		SaveCheckpoint(force: true);
		try
		{
			_ = num;
			_ = 1;
			try
			{
				logPath = Path.Combine(LogDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".log");
				writer = SafeLogFile.Open(logPath);
				resultsStream = new ResultsStream(logPath + ".results.csv");
				fullResultsPath = resultsStream.Path;
				engine = new CopyEngine
				{
					KeepNormalRows = resultRowLimit,
					PreserveDetails = (details.IsChecked == true),
					PreservePermissions = (permissions.IsChecked == true),
					SkipTimeTolerance = TimeSpan.FromSeconds(currentJob.SkipToleranceSeconds)
				};
				engine.Log = Append;
				engine.NotReachedListPath = logPath + ".not-reached.txt";
				engine.ItemCompleted = QueueResult;
				if (confirmNewer)
					engine.ConfirmNewerDestination = conflict => Dispatcher.Invoke(() =>
						MessageBox.Show(this,
							"The destination is newer. Replace this specific file?\n\nSource: " + conflict.Source + "\nModified (UTC): " + conflict.SourceWriteUtc.ToString("O") + "\n\nDestination: " + conflict.Target + "\nModified (UTC): " + conflict.TargetWriteUtc.ToString("O") + "\n\nA previous-version backup will be retained. Changes before commit invalidate this confirmation.",
							"Confirm newer destination replacement", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
				if (loadedJob != null)
				{
					engine.ResumeHashes = Jobs.Hashes(loadedJob);
				}
				engine.Progress = (Update update2) =>
				{
					lock (this)
					{
						pendingUpdate = update2;
						lastProgress = DateTime.UtcNow;
					}
					if (testSnapshotsDirectory != null)
					{
						bool scan = !scanSnapshot && update2.Phase == "Scanning" && update2.Total > 0;
						bool hashing = !hashSnapshot && update2.Phase == "Hashing destination" && update2.FileBytes > 0 && update2.FileBytes < update2.FileTotal;
						if (scan || hashing)
						{
							scanSnapshot |= scan;
							hashSnapshot |= hashing;
							Dispatcher.Invoke(() =>
							{
								RenderProgress();
								SavePreview(Path.Combine(testSnapshotsDirectory, scan ? "Scanning.png" : "Comparing.png"));
								if (hashing)
								{
									TogglePause();
									if (phaseText.Text != "Paused")
									{
										throw new InvalidOperationException("Pause UI did not update.");
									}
									SavePreview(Path.Combine(testSnapshotsDirectory, "Paused.png"));
									TogglePause();
								}
							});
						}
					}
				};
				status.Text = "Scanning sources… Pause and cancel are available during scanning.";
				SetDisplayRoot(dest);
				Append(string.Concat("START ", syncing ? "SYNC DIFFERENCES + COMPARE" : (compareOnly ? "COMPARE ONLY" : "COPY"), " | mapping=", compareContents ? "folder contents directly" : "selected names inside destination", " | policy=", rule, " | skip-time-tolerance=", (currentJob.SkipToleranceSeconds > 0) ? (currentJob.SkipToleranceSeconds + "s") : "exact", " | SHA-256=", hash || compareOnly, " | destination=", dest));
				Result result = await Task.Run(() =>
				{
					bool folderContents = compareContents && Directory.Exists(paths[0]);
					List<Entry> plan;
					if (retryEntries == null)
					{
						plan = engine.Plan(paths, dest, cancellation.Token, folderContents);
					}
					else
					{
						plan = retryEntries;
						foreach (Entry item2 in plan)
						{
							try
							{
								item2.SourceState = Stamp.Read(item2.Source);
								if (!item2.SourceState.Exists)
								{
									throw new FileNotFoundException("Source unavailable", item2.Source);
								}
								item2.Directory = item2.SourceState.Directory;
								item2.Length = item2.SourceState.Length;
							}
							catch (Exception ex6)
							{
								item2.ScanError = ex6.Message;
							}
						}
					}
					if (retryEntries != null)
					{
						// 0.4.1: a retried folder is rescanned, so its contents are retried too.
						engine.RescanAll(plan, plan.Where((Entry e) => e.Directory && e.ScanError == null).ToList(), cancellation.Token);
					}
					Append("SCAN complete: " + plan.Count + " entries");
					OfferScanAccessFix(plan, engine, cancellation.Token);
					if (syncing)
					{
						List<Entry> remaining = Jobs.DifferenceEntries(plan, syncRows);
						Append("REPAIR rechecking " + remaining.Count + " content differences; newer destinations protected unless individually confirmed; then comparing the original scope.");
						if (reviewCopy)
						{
							Preflight preview = Preflight.Inspect(remaining, ExistingRule.Replace, cancellation.Token, (string path) =>
							{
								engine.Check(cancellation.Token);
							}, engine.SkipTimeTolerance);
							bool approved = false;
							JobRecord reviewJob = new JobRecord
							{
								Rule = ExistingRule.Replace,
								Details = currentJob.Details,
								Permissions = currentJob.Permissions
							};
							Dispatcher.Invoke(() =>
							{
								approved = ApprovePreflight(remaining, preview, reviewJob);
							});
							if (!approved)
							{
								throw new OperationCanceledException();
							}
						}
						engine.ResumeHashes.Clear();
						Result result2 = engine.Repair(remaining, cancellation.Token);
						JobRecord job = new JobRecord
						{
							Sources = paths,
							Destination = dest,
							Contents = compareContents,
							Rule = ExistingRule.Replace,
							State = (result2.Clean ? "Completed" : "Incomplete"),
							Completed = result2.Items
						};
						try
						{
							Jobs.WriteReport(logPath + ".sync-copy", job, Summary(result2), "Repair pass; see the main report for the final comparison.");
						}
						catch (Exception ex7)
						{
							checkpointWarning = "Sync copy report could not be saved: " + ex7.Message;
							Append(checkpointWarning);
						}
						Dispatcher.Invoke(() =>
						{
							DrainResults();
							results.Clear();
							lock (checkpointSync)
							{
								currentJob.Completed.Clear();
							}
							keptNormalRows = 0;
							droppedRows = 0;
							resultsStream?.Reset();
							differences.Clear();
						});
						ILookup<string, ItemResult> failedCopies = result2.Items.Where(Jobs.IsIssue).ToLookup((ItemResult r) => r.Target, StringComparer.OrdinalIgnoreCase);
						engine.ItemCompleted = (ItemResult row) =>
						{
							if (Jobs.IsIssue(row))
							{
								string[] array = failedCopies[row.Target].Select((ItemResult r) => r.Reason).Distinct().ToArray();
								if (array.Length > 0)
								{
									row.Reason = row.Reason + " | Sync attempt: " + string.Join("; ", array);
								}
							}
							QueueResult(row);
						};
						Append("SYNC final comparison of the original selection; remaining issues will be shown in Results.");
						if (retryEntries == null)
						{
							plan = engine.Plan(paths, dest, cancellation.Token, folderContents);
						}
						else
						{
							foreach (Entry item3 in plan)
							{
								item3.ScanError = null;
								try
								{
									item3.SourceState = Stamp.Read(item3.Source);
									if (!item3.SourceState.Exists)
									{
										throw new FileNotFoundException("Source unavailable", item3.Source);
									}
									item3.Directory = item3.SourceState.Directory;
									item3.Length = item3.SourceState.Length;
								}
								catch (Exception ex8)
								{
									item3.ScanError = ex8.Message;
								}
							}
						}
						Result result3 = engine.Run(plan, rule, verify: true, compareOnly: true, cancellation.Token);
						result3.Copied = result2.Copied;
						result3.Skipped = result2.Skipped;
						result3.ProtectedNewer = result2.ProtectedNewer;
						foreach (ItemResult issue in result2.Items.Where(Jobs.IsIssue))
						{
							result3.Items.Add(issue);
							QueueResult(issue);
						}
						result3.Errors += result2.Errors;
						result3.Unchecked += result2.Unchecked;
						result3.Changed += result2.Changed;
						result3.Mismatched += result2.Mismatched;
						result3.Missing += result2.Missing;
						result3.MetadataDifferences += result2.MetadataDifferences;
						result3.LogIncomplete |= result2.LogIncomplete;
						return result3;
					}
					if (!compareOnly && reviewCopy)
					{
						Preflight preview2 = Preflight.Inspect(plan, rule, cancellation.Token, (string path) =>
						{
							engine.Check(cancellation.Token);
							lock (this)
							{
								pendingUpdate = new Update
								{
									Phase = "Reviewing copy plan",
									Path = path
								};
								lastProgress = DateTime.UtcNow;
							}
						}, engine.SkipTimeTolerance);
						bool approved2 = false;
						Dispatcher.Invoke(() =>
						{
							approved2 = ApprovePreflight(plan, preview2, currentJob);
						});
						if (!approved2)
						{
							throw new OperationCanceledException();
						}
					}
					return repairing ? engine.Repair(plan, cancellation.Token) : engine.Run(plan, rule, operationMode, cancellation.Token);
				});
				RenderProgress();
				bar.IsIndeterminate = false;
				bar.Value = 100.0;
				percentText.Text = "100%";
				fileBar.IsIndeterminate = false;
				result.LogIncomplete |= writer.Failed;
				bool clean = result.Clean;
				bool comparedJob = compareOnly || syncing;
				bool nothing = clean && result.PlannedFiles == 0;
				string outcome;
				if (nothing)
				{
					outcome = (comparedJob ? "Nothing to compare — the selection contained no files" : "Nothing to copy — the selection contained no files");
				}
				else if (clean)
				{
					outcome = ((hash || compareOnly) ? "Scanned primary file contents matched when read" : "Copy finished — contents not verified");
				}
				else
				{
					outcome = "Finished with issues — review the failed paths and reasons in Results";
				}
				TextBlock textBlock2 = status;
				SolidColorBrush foreground;
				if (nothing)
				{
					foreground = Muted;
				}
				else
				{
					foreground = ((clean && (hash || compareOnly)) ? Success : Failure);
				}
				textBlock2.Foreground = foreground;
				TextBlock textBlock3 = phaseText;
				string text2;
				if (nothing)
				{
					text2 = (comparedJob ? "Nothing to compare" : "Nothing to copy");
				}
				else if (clean)
				{
					text2 = ((hash || compareOnly) ? "Verification complete" : "Copy complete");
				}
				else
				{
					text2 = "Completed with differences";
				}
				textBlock3.Text = text2;
				ShowCounts(result);
				string explanation = Jobs.Explain(result, comparedJob);
				Append("EXPLANATION " + explanation.Replace(Environment.NewLine, " | "));
				issuesOnly.IsChecked = result.Errors + result.Missing + result.Mismatched + result.Changed + result.Unchecked + result.MetadataDifferences + result.ProtectedNewer > 0;
				status.Text = outcome + "\n" + explanation + "\n" + (result.LogIncomplete ? "Audit log incomplete. " : "") + (result.TreeRechecked ? "Source selection rescanned. " : "Selected paths rechecked; full tree not rescanned. ") + "Not a snapshot. Owner, audit rules and alternate streams are outside scope.";
				if (syncing)
				{
					phaseText.Text = (clean ? "Repair and comparison complete" : "Repair finished — issues remain");
					status.Text = (clean ? "Repair completed; the final comparison matched." : "Unresolved issues remain in Results. Review newer-file conflicts, access, changes and metadata separately before another repair.") + "\n" + status.Text;
				}
				currentJob.State = (result.Clean ? "Completed" : "Incomplete");
			}
			catch (NetworkOutageException outage)
			{
				Append("NETWORK STOP — " + outage.Message);
				writer?.Flush();
				phaseText.Text = "Stopped — network unavailable";
				status.Foreground = Failure;
				status.Text = outage.Message;
				currentJob.State = "Stopped";
				currentJob.Warning = outage.Message;
			}
			catch (OperationCanceledException)
			{
				Append("CANCELLED — completed files retained; current temporary copy removed.");
				writer?.Flush();
				phaseText.Text = "Cancelled";
				status.Text = "Cancelled. Start the same job again to continue: files already copied are skipped (Verified Copy re-checks them first). No partial file is trusted.";
				currentJob.State = "Cancelled";
			}
			catch (Exception ex4)
			{
				Append("JOB ERROR " + ex4);
				writer?.Flush();
				phaseText.Text = "Job stopped";
				status.Text = "Stopped: " + ex4.Message;
				currentJob.State = "Stopped";
			}
		}
		finally
		{
			if (writer != null)
			{
				writer.Dispose();
				if (writer.Failed)
				{
					TextBlock textBlock4 = status;
					textBlock4.Text = textBlock4.Text + "\n" + writer.Warning;
					status.Foreground = Failure;
					currentJob.State = "Incomplete log";
					currentJob.Warning = writer.Warning;
				}
				writer = null;
			}
			SaveCheckpoint(force: true);
			if (jobLock != null)
			{
				try
				{
					jobLock.Dispose();
				}
				catch
				{
				}
				jobLock = null;
			}
			if (checkpointWarning != null)
			{
				TextBlock textBlock5 = status;
				textBlock5.Text = textBlock5.Text + "\n" + checkpointWarning;
			}
			DrainResults();
			if (currentJob.State != "Completed" && currentJob.State != "Incomplete")
			{
				Update update;
				lock (this)
				{
					update = pendingUpdate;
				}
				if (update != null)
				{
					ShowCounts(update);
				}
				if (currentJob.State == "Cancelled" || currentJob.State == "Stopped")
				{
					string text3 = Jobs.ExplainStopped(update);
					TextBlock textBlock6 = status;
					textBlock6.Text = textBlock6.Text + "\n" + text3;
					Append("EXPLANATION " + text3);
				}
			}
			bool streamed = false;
			if (resultsStream != null)
			{
				resultsStream.Dispose();
				streamed = !resultsStream.Failed;
				if (resultsStream.Failed)
				{
					TextBlock warnBlock = status;
					warnBlock.Text = warnBlock.Text + "\n" + resultsStream.Warning + (droppedRows > 0 ? " The report lists issues and the first " + resultRowLimit.ToString("N0") + " other rows." : "");
					Append("RESULTS WARNING " + resultsStream.Warning);
				}
				resultsStream = null;
			}
			UpdateFullListButton();
			try
			{
				Jobs.WriteReport(logPath, currentJob, Summary(countSnapshot), status.Text, streamed);
				TextBlock textBlock7 = status;
				textBlock7.Text = textBlock7.Text + "\nReport saved: " + logPath + ".summary.txt";
			}
			catch (Exception ex5)
			{
				TextBlock textBlock8 = status;
				textBlock8.Text = textBlock8.Text + "\nCould not save completion report: " + ex5.Message;
				status.Foreground = Failure;
			}
			if (currentJob.Selection.Count > 0)
			{
				loadedJob = currentJob;
				sources.Items.Clear();
				AddSources(currentJob.Sources);
				UpdateMappingPreview();
			}
			busy = false;
			_ = ApplyLogRetentionAsync();
			FlushLog();
			DrainResults();
			if (results.Count > 0)
			{
				pages.SelectedIndex = pages.Items.Count - 1;
			}
			foreach (Control item4 in locked)
			{
				item4.IsEnabled = true;
			}
			pause.IsEnabled = false;
			pause.Content = ButtonContent("Pause", ActionGlyph("Pause"));
			cancel.IsEnabled = false;
			bar.IsIndeterminate = false;
			fileBar.IsIndeterminate = false;
			cancellation.Dispose();
			cancellation = null;
			UpdateSyncButton();
			if (currentJob.State == "Completed" || currentJob.State == "Incomplete")
			{
				_ = Dispatcher.BeginInvoke(new Action(OfferEndAccessFix), DispatcherPriority.Background);
			}
		}
	}

	// One permanent file beside the logs (not part of a log set, so log retention never removes it).
	private string AccessChangesFile => Path.Combine(logPath != null ? Path.GetDirectoryName(logPath) : LogDirectory, "access-changes.txt");

	private static bool UnderAny(string path, IEnumerable<string> roots)
	{
		return roots.Any((string r) => string.Equals(path, r, StringComparison.OrdinalIgnoreCase) || path.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
	}

	// After scanning: offer to fix source folders that could not be opened.
	private void OfferScanAccessFix(List<Entry> plan, CopyEngine engine, CancellationToken token)
	{
		List<Entry> denied = plan.Where((Entry e) => e.AccessDenied && !accessSkipped.Contains(e.Source)).ToList();
		if (denied.Count == 0)
		{
			return;
		}
		// 0.4.2: locked folders mean this source has permission problems, so also
		// test-open the files the scan could list (local sources only; on a network
		// source this would cost a round trip per file).
		List<Entry> files = plan.Where((Entry e) => !e.Directory && e.ScanError == null && !AccessFix.OnNetwork(e.Source)).ToList();
		for (int i = 0; i < files.Count; i++)
		{
			if (i % 64 == 0)
			{
				engine.Check(token);
				SetSidePhase("Checking file access", i, files.Count, files[i].Source, "Locked folders were found, so ClearCopy is also checking which files can be opened.");
			}
			if (AccessFix.Denied(files[i].Source))
			{
				files[i].AccessDenied = true;
				denied.Add(files[i]);
			}
		}
		List<string> roots = AccessFix.TopMost(denied.Select((Entry e) => e.Source));
		Append("ACCESS DENIED during scan: " + roots.Count + " item(s) could not be opened");
		SetSidePhase("Waiting for your answer", 0, 0, roots[0], "Choose Fix access, Skip or Cancel in the window that opened.");
		string choice = "skip";
		Dispatcher.Invoke(() =>
		{
			choice = AskAccessFix(roots, atEnd: false);
		});
		if (choice == "cancel")
		{
			throw new OperationCanceledException();
		}
		if (choice != "fix")
		{
			foreach (string root in roots)
			{
				accessSkipped.Add(root);
			}
			Append("ACCESS skipped by user; these items stay listed as scan failures.");
			return;
		}
		AccessFix.Outcome outcome = RunAccessFix(roots);
		HashSet<string> done = new HashSet<string>(outcome.Fixed, StringComparer.OrdinalIgnoreCase);
		engine.RescanAll(plan, denied.Where((Entry e) => done.Contains(e.Source)).ToList(), token);
		Append("SCAN after access fix: " + plan.Count + " entries");
	}

	private AccessFix.Outcome RunAccessFix(List<string> roots)
	{
		AccessFix.Outcome outcome;
		try
		{
			SetSidePhase("Fixing access", 0, roots.Count, roots[0], "Taking ownership and adding read permission. Windows may ask for administrator permission.");
			outcome = AccessFix.Run(roots, AccessChangesFile, accessFixInProcess, (int index, int count, string current) =>
			{
				SetSidePhase("Fixing access", index, count, current, "Item " + index + " of " + count + ": taking ownership and adding read permission, including locked items inside.");
			});
		}
		catch (Exception ex)
		{
			outcome = new AccessFix.Outcome { ChangesFile = AccessChangesFile };
			foreach (string root in roots)
			{
				outcome.Failed[root] = ex.Message;
			}
		}
		Interlocked.Increment(ref AccessFixRuns);
		foreach (string path in outcome.Fixed)
		{
			Append("ACCESS FIXED " + path);
		}
		foreach (KeyValuePair<string, string> item in outcome.Failed)
		{
			Append("ACCESS FIX FAILED " + item.Key + " : " + item.Value);
			accessSkipped.Add(item.Key);
		}
		if (outcome.Declined)
		{
			Append("ACCESS fix cancelled: administrator permission was not given.");
		}
		else
		{
			Append("ACCESS " + outcome.Fixed.Count + " fixed, " + outcome.Failed.Count + " failed; " + outcome.Changed + " item(s) changed. Original permissions saved to " + outcome.ChangesFile);
		}
		return outcome;
	}

	private void SetSidePhase(string phase, int done, int total, string path, string note)
	{
		lock (this)
		{
			pendingUpdate = new Update { Phase = phase, Done = done, Total = total, Path = path, Note = note };
			lastProgress = DateTime.UtcNow;
		}
	}

	private static bool AccessRow(ItemResult r)
	{
		string reason = r.Reason ?? "";
		return (r.Status == "Error" || r.Status == "Unchecked") && (reason.StartsWith("Access denied", StringComparison.Ordinal) || reason.StartsWith("Scan failed", StringComparison.Ordinal));
	}

	// After a job: offer to fix source items that were refused while copying, then retry them.
	private void OfferEndAccessFix()
	{
		if (busy || currentJob == null)
		{
			return;
		}
		List<ItemResult> rows = currentJob.Completed.Where((ItemResult r) => AccessRow(r) && !UnderAny(r.Source ?? "", accessSkipped)).ToList();
		if (rows.Count > 0)
		{
			OfferAccessFixFor(rows, manual: false);
		}
	}

	// "Fix access…" in Results: the selected rows, or every access problem when nothing is selected.
	private void FixAccessSelected()
	{
		if (busy)
		{
			return;
		}
		List<ItemResult> rows = resultGrid.SelectedItems.Cast<ItemResult>().Where((ItemResult r) => r.Status == "Error" || r.Status == "Unchecked" || r.Status == "Missing").ToList();
		if (rows.Count == 0)
		{
			rows = (currentJob?.Completed ?? new List<ItemResult>()).Where(AccessRow).ToList();
		}
		if (rows.Count == 0)
		{
			MessageBox.Show(this, "Select the failed rows to fix, or run a job first. There are no access problems in the current results.", "Fix access");
			return;
		}
		OfferAccessFixFor(rows, manual: true);
	}

	private async void OfferAccessFixFor(List<ItemResult> rows, bool manual)
	{
		rows = rows.GroupBy((ItemResult r) => r.Source, StringComparer.OrdinalIgnoreCase).Select((IGrouping<string, ItemResult> g) => g.Last()).Take(20000).ToList();
		List<ItemResult> sourceLocked = new List<ItemResult>();
		List<ItemResult> readable = new List<ItemResult>();
		List<string> destination = new List<string>();
		await Task.Run(() =>
		{
			foreach (ItemResult r in rows)
			{
				if (AccessFix.Denied(r.Source))
				{
					sourceLocked.Add(r);
					continue;
				}
				string problem = AccessFix.DestinationProblem(r.Target);
				if (problem != null)
				{
					destination.Add(r.Target + " — " + problem);
				}
				else
				{
					readable.Add(r);
				}
			}
		});
		Append("ACCESS CHECK " + (manual ? "(Fix access button)" : "after job") + ": " + rows.Count + " item(s); source refuses " + sourceLocked.Count + "; destination problem " + destination.Count + "; readable now " + readable.Count + " (checked as " + AccessFix.AccountName() + (AccessFix.IsElevated() ? ", administrator" : "") + ")");
		foreach (string d in destination.Take(200))
		{
			Append("ACCESS DESTINATION " + d);
		}
		FlushLog();
		if (busy)
		{
			return;
		}
		if (sourceLocked.Count == 0)
		{
			if (manual)
			{
				string text = "None of these items is refused by the source any more." + Environment.NewLine + Environment.NewLine;
				if (destination.Count > 0)
				{
					text = text + "The destination refuses " + destination.Count + ":" + Environment.NewLine + string.Join(Environment.NewLine, destination.Take(20)) + (destination.Count > 20 ? Environment.NewLine + "…" : "") + Environment.NewLine + "ClearCopy does not change permissions on the destination; fix these on the NAS." + Environment.NewLine + Environment.NewLine;
				}
				if (readable.Count == 0)
				{
					MessageBox.Show(this, text.TrimEnd(), "Fix access");
					return;
				}
				if (MessageBox.Show(this, text + "Retry the " + readable.Count + " item(s) that can be read now?", "Fix access", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
				{
					RetryRows(readable);
				}
			}
			return;
		}
		List<string> roots = AccessFix.TopMost(sourceLocked.Select((ItemResult r) => r.Source));
		string extra = destination.Count > 0 ? "The destination also refuses " + destination.Count + " item(s); those are not changed (see the activity log)." : null;
		if (AskAccessFix(roots, atEnd: true, extra) != "fix")
		{
			foreach (string root in roots)
			{
				accessSkipped.Add(root);
			}
			return;
		}
		status.Text = "Fixing access for " + roots.Count + " item(s)…";
		AccessFix.Outcome outcome = await Task.Run(() => RunAccessFix(roots));
		FlushLog();
		List<ItemResult> retry = sourceLocked.Where((ItemResult r) => UnderAny(r.Source, outcome.Fixed)).Concat(manual ? readable : new List<ItemResult>()).ToList();
		if (retry.Count == 0)
		{
			status.Text = "Access could not be fixed" + (outcome.Declined ? " (administrator permission was not given)." : ". See the activity log for the reasons.");
			return;
		}
		RetryRows(retry);
	}

	private void RetryRows(List<ItemResult> rows)
	{
		if (busy)
		{
			return;
		}
		bool compare = currentJob != null && currentJob.CompareOnly;
		selectedRetry = rows.Select((ItemResult r) => new Entry
		{
			Source = r.Source,
			Target = r.Target,
			Directory = r.Directory,
			Length = r.Size
		}).ToList();
		Start(compare);
	}

	// Returns "fix", "skip" or "cancel".
	private string AskAccessFix(List<string> roots, bool atEnd, string extra = null)
	{
		if (accessPromptOverride != null)
		{
			return accessPromptOverride(roots);
		}
		if (automaticTest)
		{
			return "skip";
		}
		string account;
		try
		{
			account = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
		}
		catch
		{
			account = Environment.UserName;
		}
		string list = string.Join(Environment.NewLine, roots.Take(300)) + (roots.Count > 300 ? Environment.NewLine + "… and " + (roots.Count - 300) + " more" : "");
		string text = "Windows refused ClearCopy access to " + roots.Count + (roots.Count == 1 ? " source item" : " source items") + (atEnd ? " while copying" : " while scanning") + ":" + Environment.NewLine + Environment.NewLine + list + Environment.NewLine + Environment.NewLine
			+ "Fix access takes ownership of these items and adds read permission for " + account + " (the same change as Explorer's \"Continue\" button), including locked folders and files inside them. Windows asks for administrator permission once for the whole list." + Environment.NewLine + Environment.NewLine
			+ "This changes permissions on the source permanently. The owner and permissions of every changed item are saved first to:" + Environment.NewLine + AccessChangesFile + Environment.NewLine + Environment.NewLine
			+ (permissions.IsChecked == true ? "\"Preserve NTFS access permissions\" is ticked, so the added read entry will also be copied to the destination." + Environment.NewLine + Environment.NewLine : "")
			+ (extra != null ? extra + Environment.NewLine + Environment.NewLine : "")
			+ (atEnd ? "After fixing, ClearCopy retries just these items." : "Skip copies everything else; these items stay listed as errors.");
		string choice = "skip";
		Window dialog = new Window
		{
			Owner = this,
			Title = "Some source items can't be read",
			Width = 760.0,
			Height = Math.Min(560.0, SystemParameters.WorkArea.Height - 80.0),
			WindowStartupLocation = WindowStartupLocation.CenterOwner
		};
		DockPanel panel = new DockPanel { Margin = new Thickness(16.0) };
		dialog.Content = panel;
		StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0.0, 12.0, 0.0, 0.0) };
		DockPanel.SetDock(buttons, Dock.Bottom);
		panel.Children.Add(buttons);
		Action<string, string, bool> add = (string label, string value, bool isDefault) =>
		{
			Button b = new Button { Content = label, Padding = new Thickness(14.0, 7.0, 14.0, 7.0), Margin = new Thickness(8.0, 0.0, 0.0, 0.0), IsDefault = isDefault };
			b.Click += (object s, RoutedEventArgs e) =>
			{
				choice = value;
				dialog.DialogResult = true;
			};
			buttons.Children.Add(b);
		};
		add(atEnd ? "Fix access and retry" : "Fix access and continue", "fix", true);
		add(atEnd ? "Not now" : "Skip them", "skip", false);
		if (!atEnd)
		{
			add("Cancel job", "cancel", false);
		}
		panel.Children.Add(new TextBox
		{
			IsReadOnly = true,
			Text = text,
			TextWrapping = TextWrapping.Wrap,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			BorderThickness = new Thickness(0.0)
		});
		dialog.ShowDialog();
		return choice;
	}

	private bool ConfigureIntegration(bool repair)
	{
		try
		{
			using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\ClearCopy"))
			{
				if (!repair && registryKey != null && Convert.ToInt32(registryKey.GetValue("AutoSetupDisabled", 0)) == 1)
				{
					integrationStatus.Text = "Explorer integration is disabled. Click Repair Explorer integration to enable it.";
					return false;
				}
			}
			Shell(install: true, notify: false);
			using (RegistryKey registryKey2 = Registry.CurrentUser.CreateSubKey("Software\\ClearCopy"))
			{
				registryKey2.SetValue("AutoSetupDisabled", 0);
			}
			integrationStatus.Foreground = Success;
			integrationStatus.Text = "Explorer ready: ClearCopy can stay closed. Copy files, then right-click the destination → Paste with ClearCopy.";
		}
		catch (Exception ex)
		{
			integrationStatus.Foreground = Failure;
			integrationStatus.Text = "Explorer setup could not finish: " + ex.Message + " You can still browse and copy inside ClearCopy.";
			return false;
		}
		if (File.Exists(PasteConfig))
		{
			try
			{
				PasteIntegration.Install(PasteConfig, SelfInstall.IntegrationPath);
				integrationStatus.Text += " ClearCopy is also added to TeraCopy's paste chooser; close an old popup and paste again.";
			}
			catch (Exception ex2)
			{
				integrationStatus.Foreground = Failure;
				TextBlock textBlock = integrationStatus;
				textBlock.Text = textBlock.Text + " Paste chooser setup failed: " + ex2.Message;
			}
		}
		return true;
	}

	private void Shell(bool install, bool notify)
	{
		string[] array = new string[3] { "Software\\Classes\\Directory\\shell\\ClearCopy", "Software\\Classes\\Directory\\Background\\shell\\ClearCopy", "Software\\Classes\\Drive\\shell\\ClearCopy" };
		for (int i = 0; i < array.Length; i++)
		{
			if (install)
			{
				using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey(array[i]);
				registryKey.SetValue("", "Paste with ClearCopy");
				registryKey.SetValue("Icon", SelfInstall.IntegrationPath);
				using RegistryKey registryKey2 = registryKey.CreateSubKey("command");
				registryKey2.SetValue("", "\"" + SelfInstall.IntegrationPath + "\" --paste \"" + ((i == 1) ? "%V" : "%1") + "\\.\"");
			}
			else
			{
				Registry.CurrentUser.DeleteSubKeyTree(array[i], throwOnMissingSubKey: false);
			}
		}
		if (notify)
		{
			MessageBox.Show(this, install ? "Installed for your Windows account. Copy files in Explorer, then right-click the destination folder or its background and choose Paste with ClearCopy. On Windows 11 use Show more options. Keep this executable in its current location. Explorer Ctrl+V is unchanged." : "Explorer menu removed for your Windows account.");
		}
	}

	private Border Card(UIElement child)
	{
		Border border = new Border();
		border.Background = Surface;
		border.BorderBrush = Outline;
		border.BorderThickness = new Thickness(1.0);
		border.CornerRadius = new CornerRadius(10.0);
		border.Padding = new Thickness(16.0);
		border.Child = child;
		return border;
	}

	private TextBlock Label(string text)
	{
		TextBlock textBlock = new TextBlock();
		textBlock.Text = text;
		textBlock.FontWeight = FontWeights.SemiBold;
		textBlock.Foreground = Ink;
		textBlock.Margin = new Thickness(0.0, 0.0, 0.0, 7.0);
		return textBlock;
	}

	private void BuildLayout()
	{
		Title = "ClearCopy 0.4.2 — copy & compare";
		Width = 1080.0;
		Height = 840.0;
		MinWidth = 800.0;
		MinHeight = 650.0;
		InitializeAppearance();
		Background = Canvas;
		FontFamily = new FontFamily("Segoe UI");
		FontSize = 13.0;
		Foreground = Ink;
		Grid root = new Grid
		{
			Margin = new Thickness(12.0)
		};
		rootGrid = root;
		Content = root;
		root.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		root.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		pagesRow = new RowDefinition
		{
			Height = new GridLength(1.0, GridUnitType.Star)
		};
		root.RowDefinitions.Add(pagesRow);
		// 0.3.9: no title header; the version is in the window title bar. The
		// theme, details and install buttons sit at the top right of the job card.
		DockPanel header = new DockPanel
		{
			Margin = new Thickness(0.0),
			Visibility = Visibility.Collapsed
		};
		root.Children.Add(header);
		StackPanel tools = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(12.0, 0.0, 0.0, 0.0)
		};
		themeButton = new Button
		{
			Padding = new Thickness(9.0, 7.0, 9.0, 7.0),
			Margin = new Thickness(6.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Top,
			ToolTip = "Switch between light and dark mode"
		};
		themeButton.Click += (object param0, RoutedEventArgs param1) =>
		{
			ApplyAppearance(!darkMode, save: true);
		};
		UpdateThemeLabel();
		installButton = new Button
		{
			Padding = new Thickness(12.0, 7.0, 12.0, 7.0),
			Margin = new Thickness(0.0),
			VerticalAlignment = VerticalAlignment.Top,
			BorderBrush = Accent,
			ToolTip = "Install in Program Files, create the shortcut and enable the Explorer Ctrl+V chooser. Windows will request administrator approval."
		};
		installButton.SetResourceReference(Control.BackgroundProperty, "Selected");
		installButton.Click += (object param0, RoutedEventArgs param1) =>
		{
			InstallProgramFiles();
		};
		tools.Children.Add(installButton);
		locked.Add(installButton);
		RefreshInstallButton();
		detailsButton = new Button
		{
			Padding = new Thickness(9.0, 7.0, 9.0, 7.0),
			Margin = new Thickness(8.0, 5.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Bottom
		};
		detailsButton.Click += (object param0, RoutedEventArgs param1) =>
		{
			SetCompact(!compactView, save: true);
		};
		tools.Children.Add(themeButton);
		UpdateDetailsButton();
		integrationStatus.FontSize = 11.0;
		integrationStatus.TextWrapping = TextWrapping.Wrap;
		integrationStatus.Margin = new Thickness(0.0, 0.0, 0.0, 6.0);
		DockPanel dockPanel = new DockPanel();
		StackPanel stackPanel2 = new StackPanel();
		StackPanel stackPanel3 = new StackPanel();
		Border jobCard = Card(dockPanel);
		jobCardBorder = jobCard;
		DockPanel.SetDock(stackPanel2, Dock.Top);
		dockPanel.Children.Add(stackPanel2);
		Grid.SetRow(jobCard, 1);
		root.Children.Add(jobCard);
		jobCard.Margin = new Thickness(0.0, 0.0, 0.0, 12.0);
		DockPanel dockPanel2 = new DockPanel();
		stackPanel2.Children.Add(dockPanel2);
		percentText.Text = ""; // blank until a job starts (a lone dash looked like a stray line)
		percentText.FontSize = 26.0;
		percentText.FontWeight = FontWeights.SemiBold;
		percentText.Foreground = Accent;
		DockPanel.SetDock(tools, Dock.Right);
		dockPanel2.Children.Add(tools);
		DockPanel.SetDock(percentText, Dock.Right);
		dockPanel2.Children.Add(percentText);
		phaseText.Text = "Ready to copy or compare";
		phaseText.FontSize = 20.0;
		phaseText.FontWeight = FontWeights.SemiBold;
		dockPanel2.Children.Add(phaseText);
		jobSummary.Text = "Choose source items and a destination below.";
		jobSummary.Foreground = Muted;
		jobSummary.TextTrimming = TextTrimming.CharacterEllipsis;
		jobSummary.Margin = new Thickness(0.0, 3.0, 0.0, 8.0);
		stackPanel2.Children.Add(jobSummary);
		mode.Items.Add("Fast Copy — contents not verified");
		mode.Items.Add("Verified Copy — SHA-256 after copying or skipping");
		mode.Items.Add("Compare / Repair — independent SHA-256 comparison first");
		mode.SelectedIndex = (int)OperationMode.VerifiedCopy;
		mode.ToolTip = "Choose the mode before starting. Compare is read-only; repair actions are available in Results after comparison.";
		stackPanel2.Children.Add(mode);
		locked.Add(mode);
		bar.Height = 18.0;
		bar.Foreground = Accent;
		stackPanel2.Children.Add(bar);
		metrics.Text = "Elapsed 00:00:00   •   Progress appears here when a job starts";
		metrics.Foreground = Muted;
		metrics.Margin = new Thickness(0.0, 7.0, 0.0, 6.0);
		metrics.TextWrapping = TextWrapping.Wrap;
		metrics.ToolTip = "I/O speed includes copying and SHA-256 reads of both source and destination. Extra destination files are not audited.";
		stackPanel3.Children.Add(metrics);
		currentPath.Text = "No active file";
		currentPath.TextTrimming = TextTrimming.CharacterEllipsis;
		stackPanel3.Children.Add(currentPath);
		fileBar.Foreground = Accent;
		fileBar.Margin = new Thickness(0.0, 5.0, 0.0, 3.0);
		stackPanel3.Children.Add(fileBar);
		fileLabel.Text = "Current file";
		fileLabel.Foreground = Muted;
		fileLabel.FontSize = 11.0;
		stackPanel3.Children.Add(fileLabel);
		ShowCounts(new Result());
		countsPanel.Margin = new Thickness(0.0, 8.0, 0.0, 5.0);
		stackPanel3.Children.Add(countsPanel);
		status.Text = "Ready. Compare only never writes to the destination.";
		status.FontSize = 12.0;
		status.MaxHeight = 64.0;
		status.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Text")
		{
			Source = status
		});
		stackPanel3.Children.Add(status);
		Style style = new Style(typeof(TextBlock), TryFindResource(typeof(TextBlock)) as Style);
		Trigger trigger = new Trigger();
		trigger.Property = TextBlock.TextProperty;
		trigger.Value = "";
		Trigger trigger2 = trigger;
		trigger2.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
		style.Triggers.Add(trigger2);
		status.Style = style;
		WrapPanel wrapPanel = new WrapPanel();
		wrapPanel.Margin = new Thickness(0.0, 5.0, 0.0, 0.0);
		WrapPanel wrapPanel2 = wrapPanel;
		DockPanel bottomRow = new DockPanel();
		DockPanel.SetDock(bottomRow, Dock.Bottom);
		dockPanel.Children.Add(bottomRow);
		DockPanel.SetDock(detailsButton, Dock.Right);
		bottomRow.Children.Add(detailsButton);
		bottomRow.Children.Add(wrapPanel2);
		dockPanel.Children.Add(new ScrollViewer
		{
			Content = stackPanel3,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
		});
		root.SizeChanged += (object param0, SizeChangedEventArgs param1) =>
		{
			// In compact view the window sizes to the card, so the card must not
			// be limited by the window height (that would shrink it repeatedly).
			jobCard.MaxHeight = compactView ? double.PositiveInfinity : Math.Max(170.0, root.ActualHeight - jobCard.Margin.Bottom - 260.0);
		};
		Button button = AddButton(wrapPanel2, "Start selected mode", () =>
		{
			Start(compareOnly: false);
		}, lockDuringJob: true);
		button.FontWeight = FontWeights.SemiBold;
		button.SetResourceReference(Control.BackgroundProperty, "Selected");
		button.BorderBrush = Accent;
		Button button2 = AddButton(wrapPanel2, "Compare original scope", () =>
		{
			CompareWholeScope();
		}, lockDuringJob: true);
		button2.FontWeight = FontWeights.SemiBold;
		pause = AddButton(wrapPanel2, "Pause", TogglePause, lockDuringJob: false);
		pause.IsEnabled = false;
		cancel = AddButton(wrapPanel2, "Cancel", () =>
		{
			if (cancellation != null)
			{
				cancellation.Cancel();
				if (engine != null)
				{
					engine.Gate.Set();
				}
			}
		}, lockDuringJob: false);
		cancel.IsEnabled = false;
		AddButton(wrapPanel2, "Open logs", () =>
		{
			Directory.CreateDirectory(LogDirectory);
			Process.Start("explorer.exe", LogDirectory);
		}, lockDuringJob: false);
		Grid.SetRow(pages, 2);
		root.Children.Add(pages);
		StackPanel stackPanel4 = new StackPanel();
		stackPanel4.Margin = new Thickness(12.0);
		StackPanel stackPanel5 = stackPanel4;
		ScrollViewer scrollViewer = new ScrollViewer();
		scrollViewer.Content = stackPanel5;
		scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
		scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
		ScrollViewer content = scrollViewer;
		pages.Items.Add(new TabItem
		{
			Header = "Folders & options",
			Content = content
		});
		log.Margin = new Thickness(10.0);
		log.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
		log.TextWrapping = TextWrapping.NoWrap;
		pages.Items.Add(new TabItem
		{
			Header = "Activity log",
			Content = WithRootLabel(log, logRootLabel)
		});
		pages.Items.Add(new TabItem
		{
			Header = "Missing / mismatched / errors",
			Content = WithRootLabel(differences, differencesRootLabel)
		});
		SetupResults();
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		stackPanel5.Children.Add(grid);
		StackPanel stackPanel6 = new StackPanel();
		Border border = Card(stackPanel6);
		border.Margin = new Thickness(0.0, 0.0, 7.0, 0.0);
		grid.Children.Add(border);
		stackPanel6.Children.Add(Label("SOURCE  •  files and folders"));
		sources.SelectionMode = SelectionMode.Extended;
		stackPanel6.Children.Add(sources);
		WrapPanel wrapPanel3 = new WrapPanel();
		wrapPanel3.Margin = new Thickness(0.0, 5.0, 0.0, 0.0);
		WrapPanel wrapPanel4 = wrapPanel3;
		stackPanel6.Children.Add(wrapPanel4);
		AddButton(wrapPanel4, "Paste sources", Paste, lockDuringJob: true);
		AddButton(wrapPanel4, "Add folder…", () =>
		{
			string text = PickFolder(null);
			if (text != null)
			{
				AddSources(new string[1] { text });
			}
		}, lockDuringJob: true);
		AddButton(wrapPanel4, "Add files…", () =>
		{
			OpenFileDialog openFileDialog = new OpenFileDialog
			{
				Multiselect = true,
				Title = "Select files — use Add folder for folders"
			};
			if (openFileDialog.ShowDialog() == true)
			{
				AddSources(openFileDialog.FileNames);
			}
		}, lockDuringJob: true);
		AddButton(wrapPanel4, "Remove", () =>
		{
			object[] array = sources.SelectedItems.Cast<object>().ToArray();
			foreach (object removeItem in array)
			{
				sources.Items.Remove(removeItem);
			}
		}, lockDuringJob: true);
		foreach (Button child in wrapPanel4.Children)
		{
			child.Padding = new Thickness(8.0, 8.0, 8.0, 8.0);
			child.Margin = new Thickness(0.0, 4.0, 4.0, 4.0);
		}
		StackPanel stackPanel7 = new StackPanel();
		Border border2 = Card(stackPanel7);
		border2.Margin = new Thickness(7.0, 0.0, 0.0, 0.0);
		Grid.SetColumn(border2, 1);
		grid.Children.Add(border2);
		stackPanel7.Children.Add(Label("DESTINATION  •  local or network folder"));
		destination.Height = 34.0;
		stackPanel7.Children.Add(destination);
		locked.Add(destination);
		WrapPanel wrapPanel5 = new WrapPanel();
		stackPanel7.Children.Add(wrapPanel5);
		AddButton(wrapPanel5, "Select folder…", () =>
		{
			string text = PickFolder(destination.Text);
			if (text != null)
			{
				destination.Text = text;
			}
		}, lockDuringJob: true);
		directCompare.Content = new TextBlock
		{
			Text = "Use folder contents (Copy + Compare)",
			TextWrapping = TextWrapping.Wrap
		};
		directCompare.ToolTip = "Both Copy and Compare use this mapping. Untick to place the source folder's name inside the destination.";
		stackPanel7.Children.Add(directCompare);
		locked.Add(directCompare);
		stackPanel7.Children.Add(mappingPreview);
		destination.TextChanged += (object param0, TextChangedEventArgs param1) =>
		{
			UpdateMappingPreview();
		};
		directCompare.Checked += (object param0, RoutedEventArgs param1) =>
		{
			UpdateMappingPreview();
		};
		directCompare.Unchecked += (object param0, RoutedEventArgs param1) =>
		{
			UpdateMappingPreview();
		};
		((INotifyCollectionChanged)sources.Items).CollectionChanged += (object param0, NotifyCollectionChangedEventArgs param1) =>
		{
			UpdateMappingPreview();
		};
		UpdateMappingPreview();
		StackPanel stackPanel8 = new StackPanel();
		stackPanel8.Margin = new Thickness(12.0);
		StackPanel stackPanel9 = stackPanel8;
		Expander expander = new Expander();
		expander.Header = "Operating mode and copy options";
		expander.IsExpanded = true;
		expander.Content = stackPanel9;
		expander.Margin = new Thickness(0.0, 12.0, 0.0, 0.0);
		Expander element = expander;
		stackPanel5.Children.Add(element);
		stackPanel9.Children.Add(Label("Existing files — overwrite policy"));
		rules.Width = double.NaN;
		rules.HorizontalAlignment = HorizontalAlignment.Stretch;
		rules.Items.Add("Skip same and skip when destination is newer");
		rules.Items.Add("Skip same; replace other existing files");
		rules.Items.Add("Replace existing files");
		rules.SelectedIndex = 0;
		stackPanel9.Children.Add(rules);
		locked.Add(rules);
		stackPanel9.Children.Add(askNewer);
		locked.Add(askNewer);
		stackPanel9.Children.Add(new TextBlock
		{
			Text = "Size/time equality is a skip policy, not content verification. Fast Copy does not prove equality. Compare never writes. Newer destinations are protected in every write mode unless you enable per-file confirmation above.",
			TextWrapping = TextWrapping.Wrap,
			Foreground = Muted,
			FontSize = 12.0
		});
		skipTolerance.Content = new TextBlock
		{
			Text = (string)skipTolerance.Content,
			TextWrapping = TextWrapping.Wrap
		};
		stackPanel9.Children.Add(skipTolerance);
		locked.Add(skipTolerance);
		stackPanel9.Children.Add(details);
		stackPanel9.Children.Add(permissions);
		locked.Add(details);
		locked.Add(permissions);
		stackPanel9.Children.Add(reviewBeforeCopy);
		locked.Add(reviewBeforeCopy);
		stackPanel9.Children.Add(new TextBlock
		{
			Text = "Start copy scans and then copies automatically. File errors are retried, recorded, and the job continues. A completion report is saved with the logs.",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 12.0,
			Foreground = Muted
		});
		stackPanel9.Children.Add(new TextBlock
		{
			Text = "Default: primary stream + last-write time. Optional DACL changes require permission. Owner, audit rules and alternate streams are not copied or certified.",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 12.0,
			Foreground = Muted
		});
		WrapPanel wrapPanel6 = new WrapPanel();
		wrapPanel6.Margin = new Thickness(0.0, 10.0, 0.0, 0.0);
		WrapPanel wrapPanel7 = wrapPanel6;
		AddButton(wrapPanel7, "Enable Explorer Ctrl+V chooser", EnablePasteChooser, lockDuringJob: true);
		AddButton(wrapPanel7, "Disable Ctrl+V chooser", DisablePasteChooser, lockDuringJob: true);
		AddButton(wrapPanel7, "Paste chooser status", () =>
		{
			MessageBox.Show(this, PasteHelper.StatusReport(), "ClearCopy paste helper");
		}, lockDuringJob: false);
		wrapPanel7.ToolTip = "The optional Ctrl+V chooser runs a ClearCopy helper at sign-in. It handles copied files in Explorer folder views; text, cut/move, other apps and the native Paste menu remain unchanged. Disable TeraCopy's default copy handling if it also intercepts paste.";
		AddButton(wrapPanel7, "Repair Explorer integration", () =>
		{
			ConfigureIntegration(repair: true);
		}, lockDuringJob: true);
		AddButton(wrapPanel7, "Remove integration", () =>
		{
			PasteHelper.Disable();
			Shell(install: false, notify: false);
			PasteIntegration.Remove(PasteConfig);
			using (RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\ClearCopy"))
			{
				registryKey.SetValue("AutoSetupDisabled", 1);
			}
			integrationStatus.Text = "Integration removed. Click Repair to enable it again.";
		}, lockDuringJob: true);
		AddButton(wrapPanel7, "Add to paste chooser", () =>
		{
			PasteIntegration.Install(PasteConfig, SelfInstall.IntegrationPath);
			integrationStatus.Text = "Added to TeraCopy's chooser. ClearCopy can stay closed; close an old popup and paste again.";
		}, lockDuringJob: true);
		StackPanel integrationPanel = new StackPanel();
		integrationPanel.Children.Add(integrationStatus);
		integrationPanel.Children.Add(wrapPanel7);
		stackPanel5.Children.Add(new Expander
		{
			Header = "Explorer integration",
			Content = integrationPanel,
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
		});
		WrapPanel wrapPanel8 = new WrapPanel();
		wrapPanel8.Margin = new Thickness(0.0, 8.0, 0.0, 0.0);
		WrapPanel wrapPanel9 = wrapPanel8;
		stackPanel5.Children.Add(wrapPanel9);
		AddButton(wrapPanel9, "Save job…", SaveDefinition, lockDuringJob: true);
		AddButton(wrapPanel9, "Load job…", LoadDefinition, lockDuringJob: true);
		AddButton(wrapPanel9, "Resume loaded job", () =>
		{
			if (loadedJob == null)
			{
				MessageBox.Show(this, "Load a saved job first.");
			}
			else
			{
				Start(loadedJob.CompareOnly);
			}
		}, lockDuringJob: true);
		AddButton(wrapPanel9, "Test a folder / share…", RunDiagnostics, lockDuringJob: true);
		AddButton(wrapPanel9, "Housekeeping…", OpenHousekeeping, lockDuringJob: true);
		refresh = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(200.0)
		};
		refresh.Tick += (object param0, EventArgs param1) =>
		{
			RenderProgress();
		};
		refresh.Start();
		Closed += (object param0, EventArgs param1) =>
		{
			refresh.Stop();
		};
	}

	private static string Size(long bytes)
	{
		double num = bytes;
		string[] array = new string[5] { "B", "KiB", "MiB", "GiB", "TiB" };
		int num2 = 0;
		while (num >= 1024.0 && num2 < array.Length - 1)
		{
			num /= 1024.0;
			num2++;
		}
		return num.ToString((num2 == 0) ? "0" : "0.0") + " " + array[num2];
	}

	private void RenderProgress()
	{
		FlushLog();
		DrainResults();
		if (!busy)
		{
			return;
		}
		Update update;
		lock (this)
		{
			update = pendingUpdate;
		}
		double num = activeSeconds + (paused ? 0.0 : (DateTime.UtcNow - started).TotalSeconds);
		string text = TimeSpan.FromSeconds(num).ToString("hh\\:mm\\:ss");
		bool waitingForNetwork = update != null && update.Phase == "Waiting for network" && !paused;
		if (waitingForNetwork && !networkWaitSince.HasValue)
		{
			networkWaitSince = DateTime.UtcNow;
		}
		else if (!waitingForNetwork && networkWaitSince.HasValue)
		{
			networkWaitSeconds += (DateTime.UtcNow - networkWaitSince.Value).TotalSeconds;
			networkWaitSince = null;
		}
		double workingSeconds = Math.Max(0.0, num - networkWaitSeconds - (networkWaitSince.HasValue ? (DateTime.UtcNow - networkWaitSince.Value).TotalSeconds : 0.0));
		if (update == null)
		{
			metrics.Text = "Elapsed " + text + " • Preparing job…";
			return;
		}
		bool side = update.Phase == "Fixing access" || update.Phase == "Checking file access" || update.Phase == "Waiting for your answer";
		if (side)
		{
			phaseText.Text = paused ? "Paused" : update.Phase;
			bar.IsIndeterminate = update.Total == 0 && !paused;
			bar.Value = update.Total > 0 ? Math.Min(100.0, 100.0 * update.Done / update.Total) : 0.0;
			percentText.Text = update.Total > 0 ? (100.0 * update.Done / update.Total).ToString("F0") + "%" : "";
			currentPath.Text = string.IsNullOrEmpty(update.Path) ? "" : (Path.GetFileName(update.Path.TrimEnd('\\')) + "  •  " + update.Path);
			currentPath.ToolTip = update.Path;
			fileBar.IsIndeterminate = false;
			fileBar.Value = 0.0;
			fileLabel.Text = update.Total > 0 ? (update.Phase == "Fixing access" ? $"Item {update.Done:N0} of {update.Total:N0}" : $"{update.Done:N0} of {update.Total:N0} files checked") : "";
			metrics.Text = "Elapsed " + text + "   •   " + update.Phase + " — no files are copied during this step";
			status.Text = update.Note ?? "";
			return;
		}
		bool flag = update.Phase == "Scanning";
		bool flag2 = update.Phase == "Rechecking source";
		bool flag3 = update.Phase == "Reviewing copy plan";
		TextBlock textBlock = phaseText;
		string text2;
		if (paused)
		{
			text2 = "Paused";
		}
		else
		{
			text2 = (flag ? "Scanning source folders" : update.Phase);
		}
		textBlock.Text = text2;
		bar.IsIndeterminate = (flag || flag2 || flag3) && !paused;
		double val;
		if (update.WorkTotal > 0)
		{
			val = 100.0 * (double)update.WorkBytes / (double)update.WorkTotal;
		}
		else
		{
			val = ((update.Total > 0) ? (100.0 * (double)update.Done / (double)update.Total) : 0.0);
		}
		bar.Value = Math.Max(0.0, Math.Min(100.0, val));
		TextBlock textBlock2 = percentText;
		string text3;
		if (flag)
		{
			text3 = "Scanning";
		}
		else if (flag2)
		{
			text3 = "Final check";
		}
		else
		{
			text3 = (flag3 ? "Review" : (val.ToString("F1") + "%"));
		}
		textBlock2.Text = text3;
		currentPath.Text = (string.IsNullOrEmpty(update.Path) ? "" : (Path.GetFileName(update.Path.TrimEnd('\\')) + "  •  " + update.Path));
		currentPath.ToolTip = update.Path;
		fileBar.IsIndeterminate = flag && !paused;
		fileBar.Value = ((update.FileTotal == 0) ? 0.0 : Math.Min(100.0, 100.0 * (double)update.FileBytes / (double)update.FileTotal));
		fileLabel.Text = (flag ? "Counting folders and files — total and ETA become available after scanning" : $"Current file: {Size(update.FileBytes)} / {Size(update.FileTotal)}  ({fileBar.Value:F1}%)");
		if (update.Phase == "Final checks complete")
		{
			currentPath.Text = "File processing and final checks finished";
			fileBar.Value = 100.0;
			fileLabel.Text = "Review the result counts and selected verification scope below.";
		}
		double num2 = (double)(update.Transferred + update.ReadBytes) / Math.Max(0.1, num);
		string text4 = "calculating";
		if (waitingForNetwork)
		{
			text4 = "waiting for network";
		}
		else if (!flag && update.WorkBytes > 0 && workingSeconds > 0.0)
		{
			text4 = TimeSpan.FromSeconds(Math.Min(315360000.0, (double)Math.Max(0L, update.WorkTotal - update.WorkBytes) / ((double)update.WorkBytes / workingSeconds))).ToString("dd\\.hh\\:mm\\:ss");
		}
		metrics.Text = (flag ? $"Elapsed {text}   •   {update.Total:N0} files found   •   {update.Folders:N0} folders   •   {Size(update.TotalBytes)} found" : ($"Elapsed {text}   •   {update.Done:N0} / {update.Total:N0} files   •   {Math.Max(0, update.Total - update.Done):N0} remaining   •   I/O {Size((long)num2)}/s   •   ETA ~{text4}" + "\n" + RemainingData(update)));
		ShowCounts(update);
		double totalSeconds = (DateTime.UtcNow - lastProgress).TotalSeconds;
		TextBlock textBlock3 = status;
		string text5;
		if (paused)
		{
			text5 = "Paused. Resume to continue, or Cancel to stop safely.";
		}
		else if (update.Phase == "Waiting for network" && !string.IsNullOrEmpty(update.Note))
		{
			text5 = update.Note;
		}
		else if (totalSeconds > 3.0)
		{
			text5 = "Waiting for file or network access — last update " + (int)totalSeconds + " seconds ago. Cancel takes effect when the current I/O returns.";
		}
		else
		{
			text5 = (flag ? "Finding the source files. No destination files are changed during scanning." : "");
		}
		textBlock3.Text = text5;
	}

	private static string RemainingData(Update u)
	{
		string text = (u.Copying ? ("Up to " + Size(u.CopyLeft) + " left to copy") : "Copy: none (compare only)");
		string text2 = (u.Verifying ? (Size(u.VerifyLeft) + " left to verify (source + destination reads)") : "Verify: off (SHA-256 verification not selected)");
		return text + "   •   " + text2;
	}

	private void ShowCounts(Update u)
	{
		ShowCounts(new Result
		{
			Copied = u.Copied,
			Skipped = u.Skipped,
			Matched = u.Matched,
			Missing = u.Missing,
			Mismatched = u.Mismatched,
			Errors = u.Errors,
			Changed = u.Changed,
			Unchecked = u.Unchecked,
			MetadataDifferences = u.MetadataDifferences,
			ProtectedNewer = u.ProtectedNewer,
			NotReached = u.NotReached
		});
	}

	private void ShowCounts(Result r)
	{
		countSnapshot = r;
		List<string> list = new List<string>();
		list.Add("Copied " + r.Copied);
		list.Add("Skipped " + r.Skipped);
		list.Add("Matched " + r.Matched);
		if (r.ProtectedNewer > 0) list.Add("Protected newer " + r.ProtectedNewer);
		if (r.NotReached > 0) list.Add("Not reached " + r.NotReached);
		List<string> list2 = list;
		if (r.Missing > 0)
		{
			list2.Add("Missing " + r.Missing);
		}
		if (r.Mismatched > 0)
		{
			list2.Add("Mismatch " + r.Mismatched);
		}
		if (r.Errors > 0)
		{
			list2.Add("Errors " + r.Errors);
		}
		if (r.Changed > 0)
		{
			list2.Add("Changed " + r.Changed);
		}
		if (r.Unchecked > 0)
		{
			list2.Add("Unchecked " + r.Unchecked);
		}
		if (r.MetadataDifferences > 0)
		{
			list2.Add("Metadata " + r.MetadataDifferences);
		}
		counts.Text = string.Join("   ", list2);
		counts.TextWrapping = TextWrapping.Wrap;
		counts.ToolTip = Summary(r);
		counts.Foreground = ((r.Missing + r.Mismatched + r.Errors + r.Changed + r.Unchecked + r.MetadataDifferences + r.ProtectedNewer + r.NotReached > 0) ? Failure : Ink);
		CountLink("Copied", "Copied", r.Copied, true, false);
		CountLink("Skipped", "Skipped", r.Skipped, true, false);
		CountLink("Matched", "Matched", r.Matched, true, false);
		CountLink("Protected newer", "Protected newer", r.ProtectedNewer, r.ProtectedNewer > 0, true);
		CountLink("Not reached", "Not reached", r.NotReached, r.NotReached > 0, true);
		CountLink("Missing", "Missing", r.Missing, r.Missing > 0, true);
		CountLink("Mismatch", "Mismatch", r.Mismatched, r.Mismatched > 0, true);
		CountLink("Errors", "Errors", r.Errors, r.Errors > 0, true);
		CountLink("Changed", "Changed", r.Changed, r.Changed > 0, true);
		CountLink("Unchecked", "Unchecked", r.Unchecked, r.Unchecked > 0, true);
		CountLink("Metadata", "Metadata", r.MetadataDifferences, r.MetadataDifferences > 0, true);
	}

	// Which result statuses each count represents. Skipped includes protected
	// newer destinations because those are also counted as skipped.
	private static bool StatusesAreIssues(string[] statuses)
	{
		return statuses != null && !statuses.Any((string x) => x == "Copied" || x == "Skipped" || x == "Matched");
	}

	private static string[] StatusesFor(string key)
	{
		switch (key)
		{
		case "Copied": return new[] { "Copied" };
		case "Skipped": return new[] { "Skipped", "Protected newer" };
		case "Matched": return new[] { "Matched" };
		case "Protected newer": return new[] { "Protected newer" };
		case "Not reached": return new[] { "Not reached" };
		case "Missing": return new[] { "Missing" };
		case "Mismatch": return new[] { "Mismatch" };
		case "Errors": return new[] { "Error", "Metadata error", "Unchecked" };
		case "Changed": return new[] { "Changed" };
		case "Unchecked": return new[] { "Unchecked", "Error", "Metadata error", "Changed" };
		case "Metadata": return new[] { "Metadata difference" };
		default: return new string[0];
		}
	}

	private void CountLink(string key, string label, int value, bool visible, bool issue)
	{
		if (!countButtons.TryGetValue(key, out var button))
		{
			button = new Button
			{
				Background = Brushes.Transparent,
				BorderThickness = new Thickness(0.0),
				Padding = new Thickness(4.0, 1.0, 4.0, 1.0),
				Margin = new Thickness(0.0, 0.0, 10.0, 0.0),
				FontWeight = FontWeights.SemiBold,
				Cursor = Cursors.Hand
			};
			button.Click += (object s, RoutedEventArgs e) =>
			{
				ShowResultsFor(key);
			};
			countButtons[key] = button;
			countsPanel.Children.Add(button);
		}
		string text = label + " " + value;
		if (!(button.Content is string current) || current != text)
		{
			button.Content = text;
		}
		button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
		button.Foreground = (issue && value > 0) ? Failure : Ink;
		button.ToolTip = "Show the " + label.ToLowerInvariant() + " items in Results";
		AutomationProperties.SetName(button, text + ". Show these items in Results");
	}

	private void ShowResultsFor(string key)
	{
		DrainResults();
		statusFilter = StatusesFor(key);
		resultFilter.Text = "";
		issuesOnly.IsChecked = false;
		CollectionViewSource.GetDefaultView(results).Refresh();
		int shown = CollectionViewSource.GetDefaultView(results).Cast<object>().Count();
		statusFilterText.Text = "Showing " + key.ToLowerInvariant() + ": " + shown + (shown == 1 ? " item" : " items");
		if (ResultsCapped && !StatusesAreIssues(statusFilter))
		{
			statusFilterText.Text = "Showing " + key.ToLowerInvariant() + ": first " + shown.ToString("N0") + " (window keeps the first " + resultRowLimit.ToString("N0") + " ordinary rows)";
		}
		UpdateFullListButton();
		statusFilterText.Visibility = Visibility.Visible;
		if (clearStatusFilter != null)
		{
			clearStatusFilter.Visibility = Visibility.Visible;
		}
		if (compactView)
		{
			SetCompact(compact: false, save: true);
		}
		pages.SelectedIndex = pages.Items.Count - 1;
	}

	private void ClearStatusFilter()
	{
		statusFilter = null;
		statusFilterText.Visibility = Visibility.Collapsed;
		if (clearStatusFilter != null)
		{
			clearStatusFilter.Visibility = Visibility.Collapsed;
		}
		CollectionViewSource.GetDefaultView(results).Refresh();
	}

	private void UpdateDetailsButton()
	{
		if (detailsButton == null)
		{
			return;
		}
		string tip = compactView ? "Show details (folders, options, log and results)" : "Hide details for a compact view";
		detailsButton.Content = new TextBlock
		{
			Text = compactView ? "\ue70d" : "\ue70e",
			FontFamily = new FontFamily("Segoe MDL2 Assets"),
			FontSize = 14.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		detailsButton.ToolTip = tip;
		AutomationProperties.SetName(detailsButton, tip);
	}

	private void SetCompact(bool compact, bool save)
	{
		if (compact != compactView)
		{
			compactView = compact;
			bool normal = WindowState == WindowState.Normal;
			if (compact)
			{
				if (normal && ActualHeight > 0.0)
				{
					expandedHeight = ActualHeight;
				}
				pages.Visibility = Visibility.Collapsed;
				if (pagesRow != null)
				{
					pagesRow.Height = GridLength.Auto;
				}
				if (rootGrid != null)
				{
					rootGrid.Margin = new Thickness(8.0);
				}
				if (jobCardBorder != null)
				{
					jobCardBorder.Margin = new Thickness(0.0);
				}
				if (jobCardBorder != null)
				{
					jobCardBorder.MaxHeight = double.PositiveInfinity;
				}
				MinHeight = 0.0;
				if (normal)
				{
					SizeToContent = SizeToContent.Height;
				}
			}
			else
			{
				SizeToContent = SizeToContent.Manual;
				if (rootGrid != null)
				{
					rootGrid.Margin = new Thickness(12.0);
				}
				if (jobCardBorder != null)
				{
					jobCardBorder.Margin = new Thickness(0.0, 0.0, 0.0, 12.0);
				}
				pages.Visibility = Visibility.Visible;
				if (pagesRow != null)
				{
					pagesRow.Height = new GridLength(1.0, GridUnitType.Star);
				}
				MinHeight = 650.0;
				if (normal)
				{
					Height = Math.Max(650.0, expandedHeight);
				}
			}
			UpdateDetailsButton();
		}
		if (!save)
		{
			return;
		}
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\ClearCopy");
			registryKey.SetValue("CompactView", compact ? 1 : 0);
		}
		catch
		{
		}
	}

	private string Summary(Result r)
	{
		return $"Copied {r.Copied}, skipped {r.Skipped}, SHA-256 matched {r.Matched}, missing {r.Missing}, mismatched {r.Mismatched}, protected newer {r.ProtectedNewer}, not reached {r.NotReached}, errors {r.Errors}, changed {r.Changed}, unchecked {r.Unchecked}, metadata differences {r.MetadataDifferences}. Copy/skip counts are actions, not verification.";
	}

	private void UpdateMappingPreview()
	{
		try
		{
			if (SameSavedSelection(sources.Items.Cast<string>().ToArray(), destination.Text))
			{
				mappingPreview.Text = "Saved subset: " + loadedJob.Selection.Count + " exact source → destination mappings. Other source paths are not included.";
				return;
			}
			string text = ((sources.Items.Count > 0) ? ((string)sources.Items[0]) : "SourceFolder");
			string text2 = (string.IsNullOrWhiteSpace(destination.Text) ? "Destination" : destination.Text);
			TextBlock textBlock = mappingPreview;
			bool? isChecked = directCompare.IsChecked;
			textBlock.Text = ((isChecked == true && isChecked.HasValue && sources.Items.Count <= 1) ? ("Folder example: " + text + "\\example.txt → " + text2.TrimEnd('\\') + "\\example.txt") : ("Folder example: " + text + "\\example.txt → " + Path.Combine(text2, Path.GetFileName(text.TrimEnd('\\')), "example.txt")));
		}
		catch
		{
			mappingPreview.Text = "Select source and destination to preview paths.";
		}
	}

	// Terminal-style scrolling: keep following new lines only while the view is
	// at the bottom. If the user has scrolled up, leave the view where it is (and
	// postpone trimming, which would move it) until the text gets very large.
	private static bool AtBottom(TextBox box)
	{
		return box.ExtentHeight <= box.ViewportHeight + 1.0 || box.VerticalOffset + box.ViewportHeight >= box.ExtentHeight - 2.0;
	}

	private static void AppendFollowing(TextBox box, string text)
	{
		bool follow = AtBottom(box);
		double horizontal = box.HorizontalOffset;
		box.AppendText(text);
		int length = box.Text.Length;
		if (length > (follow ? 120000 : 600000))
		{
			box.Text = box.Text.Substring(length - 90000);
			follow = true;
		}
		if (follow)
		{
			box.ScrollToEnd();
			box.ScrollToHorizontalOffset(horizontal);
		}
	}

	private void FlushLog()
	{
		StringBuilder stringBuilder = new StringBuilder();
		StringBuilder stringBuilder2 = new StringBuilder();
		List<string> lines;
		lock (uiLog)
		{
			lines = new List<string>(uiLog);
			uiLog.Clear();
		}
		// The on-screen log only ever keeps the last ~90,000 characters, so a large
		// burst is trimmed before it reaches the text box. The log file has every line.
		const int maxLines = 2000;
		int skipped = Math.Max(0, lines.Count - maxLines);
		if (skipped > 0)
		{
			stringBuilder.AppendLine("… " + skipped.ToString("N0") + " earlier lines not shown here; see the log file (Open logs).");
		}
		int issueLines = 0;
		for (int i = 0; i < lines.Count; i++)
		{
			string text = lines[i];
			if (i >= skipped)
			{
				stringBuilder.AppendLine(text);
			}
			if (issueLines < maxLines && (text.Contains("MISSING ") || text.Contains("MISMATCH ") || text.Contains("ERROR ")))
			{
				stringBuilder2.AppendLine(text);
				issueLines++;
			}
		}
		if (stringBuilder.Length == 0)
		{
			return;
		}
		AppendFollowing(log, stringBuilder.ToString());
		if (stringBuilder2.Length > 0)
		{
			AppendFollowing(differences, stringBuilder2.ToString());
		}
	}

	private void SavePreview(string path)
	{
		UpdateLayout();
		RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96.0, 96.0, PixelFormats.Pbgra32);
		renderTargetBitmap.Render(this);
		PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder();
		pngBitmapEncoder.Frames.Add(BitmapFrame.Create(renderTargetBitmap));
		using FileStream stream = File.Create(path);
		pngBitmapEncoder.Save(stream);
	}

	private void UpdateSyncButton()
	{
		if (syncButton != null)
		{
			syncButton.IsEnabled = !busy && currentJob != null && currentJob.CompareOnly && Jobs.RepairableRows(currentJob.Completed).Length > 0;
		}
	}

	private void SyncDifferences()
	{
		if (!busy && currentJob != null && currentJob.CompareOnly)
		{
			JobRecord jobRecord = currentJob;
			ItemResult[] array = Jobs.RepairableRows(jobRecord.Completed);
			if (array.Length != 0)
			{
				loadedJob = jobRecord;
				sources.Items.Clear();
				AddSources(jobRecord.Sources);
				destination.Text = jobRecord.Destination;
				directCompare.IsChecked = jobRecord.Contents;
				rules.SelectedIndex = (int)jobRecord.Rule;
				mode.SelectedIndex = (int)OperationMode.Compare;
				details.IsChecked = jobRecord.Details;
				permissions.IsChecked = jobRecord.Permissions;
				skipTolerance.IsChecked = jobRecord.SkipToleranceSeconds > 0;
				pendingSync = array;
				Start(compareOnly: true);
			}
		}
	}

	private void SetupResults()
	{
		string[][] array = new string[7][]
		{
			new string[2] { "Status", "DisplayStatus" },
			new string[2] { "Name", "Name" },
			new string[2] { "Source", "Source" },
			new string[2] { "Destination", "Target" },
			new string[2] { "Bytes", "Size" },
			new string[2] { "Reason", "Reason" },
			new string[2] { "Previous version", "Backup" }
		};
		foreach (string[] array2 in array)
		{
			resultGrid.Columns.Add(new DataGridTextColumn
			{
				Header = array2[0],
				Binding = new Binding(array2[1]),
				Width = ((array2[1] == "Status") ? 110 : ((array2[1] == "Size") ? 90 : ((array2[1] == "Name") ? 160 : 250))),
				CanUserSort = true
			});
		}
		resultGrid.ItemsSource = results;
		ICollectionView view = CollectionViewSource.GetDefaultView(results);
		view.Filter = (object value) =>
		{
			ItemResult itemResult = (ItemResult)value;
			bool flag = itemResult.Status != "Matched" && itemResult.Status != "Copied" && itemResult.Status != "Skipped";
			string value2 = resultFilter.Text ?? "";
			if (statusFilter != null && Array.IndexOf(statusFilter, itemResult.Status) < 0)
			{
				return false;
			}
			return (statusFilter != null || issuesOnly.IsChecked != true || flag) && string.Join(" ", itemResult.Source, itemResult.Target, itemResult.Status, itemResult.Reason).IndexOf(value2, StringComparison.OrdinalIgnoreCase) >= 0;
		};
		resultFilter.TextChanged += (object param0, TextChangedEventArgs param1) =>
		{
			view.Refresh();
		};
		issuesOnly.Checked += (object param0, RoutedEventArgs param1) =>
		{
			view.Refresh();
		};
		issuesOnly.Unchecked += (object param0, RoutedEventArgs param1) =>
		{
			view.Refresh();
		};
		DockPanel dockPanel = new DockPanel();
		StackPanel stackPanel = new StackPanel();
		stackPanel.Orientation = Orientation.Horizontal;
		StackPanel stackPanel2 = stackPanel;
		ScrollViewer scrollViewer = new ScrollViewer();
		scrollViewer.Content = stackPanel2;
		scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
		scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
		ScrollViewer element = scrollViewer;
		DockPanel.SetDock(element, Dock.Top);
		dockPanel.Children.Add(element);
		syncButton = AddButton(stackPanel2, "Repair all content differences", SyncDifferences, lockDuringJob: false);
		syncButton.ToolTip = "Recheck and repair confirmed missing/mismatched files, verify repairs, then compare the original scope. Newer destinations stay protected unless individually confirmed. Metadata-only and inaccessible files are not recopied; extra destination files stay in place.";
		UpdateSyncButton();
		stackPanel2.Children.Add(new TextBlock
		{
			Text = "Filter",
			Margin = new Thickness(8.0, 10.0, 0.0, 0.0)
		});
		statusFilterText.Visibility = Visibility.Collapsed;
		statusFilterText.FontWeight = FontWeights.SemiBold;
		statusFilterText.VerticalAlignment = VerticalAlignment.Center;
		statusFilterText.Margin = new Thickness(8.0, 10.0, 4.0, 0.0);
		stackPanel2.Children.Add(statusFilterText);
		clearStatusFilter = AddButton(stackPanel2, "Show all", ClearStatusFilter, lockDuringJob: false);
		clearStatusFilter.Visibility = Visibility.Collapsed;
		openFullList = AddButton(stackPanel2, "Open full list", OpenFullList, lockDuringJob: false);
		openFullList.Visibility = Visibility.Collapsed;
		stackPanel2.Children.Add(resultFilter);
		stackPanel2.Children.Add(issuesOnly);
		AddButton(stackPanel2, "Export CSV…", ExportResults, lockDuringJob: false);
		AddButton(stackPanel2, "Copy path", () =>
		{
			if (resultGrid.SelectedItem is ItemResult itemResult)
			{
				Clipboard.SetText(itemResult.Target);
			}
		}, lockDuringJob: false);
		AddButton(stackPanel2, "Open folder", () =>
		{
			if (resultGrid.SelectedItem is ItemResult itemResult)
			{
				string text = (itemResult.Directory ? itemResult.Target : Path.GetDirectoryName(itemResult.Target));
				if (Directory.Exists(text))
				{
					Process.Start("explorer.exe", "\"" + text + "\"");
				}
			}
		}, lockDuringJob: false);
		AddButton(stackPanel2, "Retry selected…", RetrySelected, lockDuringJob: true);
		AddButton(stackPanel2, "Fix access…", FixAccessSelected, lockDuringJob: true).ToolTip = "Take ownership of source items Windows refuses and add read permission, then retry them. Works on the selected rows, or on every access problem when nothing is selected.";
		dockPanel.Children.Add(resultGrid);
		pages.Items.Add(new TabItem
		{
			Header = "Results",
			Content = dockPanel
		});
		AddButton(stackPanel2, "Repair selected content differences…", RepairSelected, lockDuringJob: true);
	}

	private void ExportResults()
	{
		SaveFileDialog saveFileDialog = new SaveFileDialog();
		saveFileDialog.Filter = "CSV report|*.csv";
		saveFileDialog.FileName = "ClearCopy-results.csv";
		SaveFileDialog saveFileDialog2 = saveFileDialog;
		if (saveFileDialog2.ShowDialog() == true)
		{
			Jobs.Export(saveFileDialog2.FileName, CollectionViewSource.GetDefaultView(results).Cast<ItemResult>());
		}
	}

	private void QueueResult(ItemResult row)
	{
		resultsStream?.Write(row);
		if (!Jobs.IsIssue(row) && keptNormalRows++ >= resultRowLimit)
		{
			// Still counted and in the full list on disk; just not kept in memory.
			droppedRows++;
			return;
		}
		lock (rowQueue)
		{
			rowQueue.Enqueue(row);
		}
		if (currentJob != null)
		{
			// 0.4: no periodic job checkpoint. The job file is written at start,
			// finish and cancel; a rerun skips what was already copied.
			lock (checkpointSync)
			{
				currentJob.Completed.Add(row);
			}
		}
	}

	private bool ResultsCapped => droppedRows > 0;

	private void UpdateFullListButton()
	{
		if (openFullList == null)
		{
			return;
		}
		bool show = ResultsCapped && fullResultsPath != null;
		openFullList.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
		openFullList.ToolTip = show ? "Only issues and the first " + resultRowLimit.ToString("N0") + " other rows are kept in this window. Open the full list (" + fullResultsPath + ")." : null;
	}

	private void OpenFullList()
	{
		if (fullResultsPath == null)
		{
			return;
		}
		resultsStream?.Flush();
		if (!File.Exists(fullResultsPath))
		{
			MessageBox.Show(this, "The full results list is not available:\n" + fullResultsPath, "ClearCopy", MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}
		try
		{
			Process.Start(new ProcessStartInfo(fullResultsPath) { UseShellExecute = true });
		}
		catch
		{
			Process.Start("explorer.exe", "/select,\"" + fullResultsPath + "\"");
		}
	}

	// Keep the newest 30 log sets; runs in the background so it never delays the UI.
	private const int KeepLogs = 30;

	private string PasteRequestDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearCopy", "PasteRequests");

	private Task ApplyLogRetentionAsync()
	{
		string logs = LogDirectory;
		string paste = (testLogDirectory == null) ? PasteRequestDirectory : null;
		return Task.Run(() =>
		{
			try
			{
				string summary = Housekeeping.ApplyRetention(logs, paste, KeepLogs, 7, DateTime.UtcNow);
				if (summary != null)
				{
					Dispatcher.BeginInvoke((Action)(() =>
					{
						lock (uiLog)
						{
							uiLog.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + summary);
						}
					}));
				}
			}
			catch
			{
			}
		});
	}

	// Called by CrashHandler, possibly from a background thread: record the
	// crash in the job log and save the running job as Stopped so it can resume.
	internal void EmergencySave(string reason, string report)
	{
		try
		{
			writer?.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  CRASH " + reason + ((report != null) ? (" | report: " + report) : ""));
			writer?.Flush();
		}
		catch
		{
		}
		if (!busy || currentJob == null || string.IsNullOrEmpty(currentJobFile))
		{
			return;
		}
		JobRecord snapshot;
		lock (checkpointSync)
		{
			currentJob.State = "Stopped";
			currentJob.Warning = reason;
			snapshot = currentJob.Snapshot();
		}
		lock (checkpointWrite)
		{
			Jobs.Save(currentJobFile, snapshot);
		}
	}

	private void DrainResults()
	{
		lock (rowQueue)
		{
			if (rowQueue.Count == 0)
			{
				return;
			}
			// A large batch is added in one step with a single "reset" notification,
			// so the grid redraws once instead of once per row.
			if (rowQueue.Count > 200)
			{
				ItemResult[] batch = rowQueue.ToArray();
				rowQueue.Clear();
				results.AddRange(batch);
				return;
			}
			while (rowQueue.Count > 0)
			{
				results.Add(rowQueue.Dequeue());
			}
		}
	}

	private void SaveCheckpoint(bool force)
	{
		if (currentJob == null || string.IsNullOrEmpty(currentJobFile) || (!force && (DateTime.UtcNow - lastCheckpoint).TotalSeconds < 30.0))
		{
			return;
		}
		lastCheckpoint = DateTime.UtcNow;
		JobRecord snapshot;
		lock (checkpointSync)
		{
			snapshot = currentJob.Snapshot();
		}
		string file = currentJobFile;
		if (force)
		{
			// Start, finish and cancellation: drop any older queued snapshot, let
			// an in-progress write finish, then write this one before returning.
			Task running;
			lock (checkpointQueue)
			{
				pendingCheckpoint = null;
				running = checkpointTask;
			}
			try
			{
				running.Wait();
			}
			catch
			{
			}
			WriteCheckpoint(file, snapshot);
			return;
		}
		lock (checkpointQueue)
		{
			pendingCheckpoint = snapshot;
			pendingCheckpointFile = file;
			if (checkpointWriterRunning)
			{
				return;
			}
			checkpointWriterRunning = true;
			checkpointTask = Task.Run((Action)CheckpointWriter);
		}
	}

	private void CheckpointWriter()
	{
		while (true)
		{
			JobRecord snapshot;
			string file;
			lock (checkpointQueue)
			{
				if (pendingCheckpoint == null)
				{
					checkpointWriterRunning = false;
					return;
				}
				snapshot = pendingCheckpoint;
				file = pendingCheckpointFile;
				pendingCheckpoint = null;
			}
			WriteCheckpoint(file, snapshot);
		}
	}

	private void WriteCheckpoint(string file, JobRecord snapshot)
	{
		lock (checkpointWrite)
		{
			try
			{
				Jobs.Save(file, snapshot);
				Interlocked.Increment(ref CheckpointWrites);
			}
			catch (Exception ex)
			{
				checkpointWarning = "Job checkpoint incomplete: " + ex.Message;
			}
		}
	}

	private JobRecord CaptureJob(bool compare)
	{
		JobRecord jobRecord = new JobRecord();
		jobRecord.Sources = sources.Items.Cast<string>().Select(Path.GetFullPath).ToArray();
		jobRecord.Destination = Path.GetFullPath(destination.Text);
		jobRecord.Contents = directCompare.IsChecked == true;
		jobRecord.Rule = (ExistingRule)rules.SelectedIndex;
		jobRecord.Mode = compare ? OperationMode.Compare : (OperationMode)mode.SelectedIndex;
		jobRecord.Verify = jobRecord.Mode != OperationMode.FastCopy;
		jobRecord.CompareOnly = jobRecord.Mode == OperationMode.Compare;
		jobRecord.Details = details.IsChecked == true;
		jobRecord.Permissions = permissions.IsChecked == true;
		jobRecord.SkipToleranceSeconds = ((skipTolerance.IsChecked == true) ? 2 : 0);
		return jobRecord;
	}

	private bool SameSavedSelection(string[] paths, string dest)
	{
		try
		{
			return loadedJob != null && loadedJob.Selection.Count > 0 && string.Equals(Path.GetFullPath(dest).TrimEnd('\\'), Path.GetFullPath(loadedJob.Destination).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && paths.Select((string p) => Path.GetFullPath(p).TrimEnd('\\')).SequenceEqual(loadedJob.Sources.Select((string p) => Path.GetFullPath(p).TrimEnd('\\')), StringComparer.OrdinalIgnoreCase) && directCompare.IsChecked == loadedJob.Contents;
		}
		catch
		{
			return false;
		}
	}

	private List<Entry> SavedEntries()
	{
		return loadedJob.Selection.Select((ItemResult r) => new Entry
		{
			Source = r.Source,
			Target = r.Target,
			Directory = r.Directory,
			Length = r.Size
		}).ToList();
	}

	private void SaveDefinition()
	{
		SaveFileDialog saveFileDialog = new SaveFileDialog();
		saveFileDialog.Filter = "ClearCopy job|*.clearcopy.xml";
		saveFileDialog.FileName = "Job.clearcopy.xml";
		SaveFileDialog saveFileDialog2 = saveFileDialog;
		if (saveFileDialog2.ShowDialog() == true)
		{
			JobRecord jobRecord = CaptureJob(mode.SelectedIndex == (int)OperationMode.Compare);
			if (SameSavedSelection(jobRecord.Sources, jobRecord.Destination))
			{
				jobRecord.Selection = loadedJob.Selection;
			}
			Jobs.Save(saveFileDialog2.FileName, jobRecord);
		}
	}

	private void LoadDefinition()
	{
		OpenFileDialog openFileDialog = new OpenFileDialog();
		openFileDialog.Filter = "ClearCopy job|*.xml";
		OpenFileDialog openFileDialog2 = openFileDialog;
		if (Directory.Exists(JobsDirectory))
		{
			openFileDialog2.InitialDirectory = JobsDirectory;
		}
		if (openFileDialog2.ShowDialog() == true)
		{
			LoadDefinition(openFileDialog2.FileName);
		}
	}

	private void LoadDefinition(string file)
	{
		JobRecord jobRecord = (loadedJob = Jobs.Load(file));
		sources.Items.Clear();
		AddSources(jobRecord.Sources);
		destination.Text = jobRecord.Destination;
		directCompare.IsChecked = jobRecord.Contents;
		rules.SelectedIndex = (int)jobRecord.Rule;
		mode.SelectedIndex = (int)Jobs.GetMode(jobRecord);
		details.IsChecked = jobRecord.Details;
		permissions.IsChecked = jobRecord.Permissions;
		skipTolerance.IsChecked = jobRecord.SkipToleranceSeconds > 0;
		pages.SelectedIndex = 0;
		askNewer.IsChecked = false;
		status.Text = "Loaded " + jobRecord.State + " " + Jobs.GetMode(jobRecord) + " job. It will rescan. Verified Copy revalidates saved hashes; Fast Copy uses the configured policy. Newer-file permissions must be confirmed again.";
	}

	private void RetrySelected()
	{
		PrepareSelected(repair: false);
	}

	private void RepairSelected()
	{
		PrepareSelected(repair: true);
	}

	private void PrepareSelected(bool repair)
	{
		ItemResult[] array = (from g in (from ItemResult r in resultGrid.SelectedItems
				where r.Status != "Copied" && r.Status != "Skipped" && r.Status != "Matched"
				select r).GroupBy((ItemResult r) => r.Target, StringComparer.OrdinalIgnoreCase)
			select g.Last()).ToArray();
		if (repair)
		{
			ItemResult[] confirmed = Jobs.RepairableRows(currentJob?.Completed ?? new List<ItemResult>());
			array = array.Where(Jobs.IsContentIssue).Where(r => confirmed.Any(c => string.Equals(c.Source, r.Source, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Target, r.Target, StringComparison.OrdinalIgnoreCase))).ToArray();
		}
		if (array.Length == 0)
		{
			MessageBox.Show(this, repair ? "Select confirmed missing or content-mismatched files. Metadata-only, changed and inaccessible files require separate review; no content will be copied for them." : "Select missing, mismatched, changed or failed result rows.");
			return;
		}
		selectedRetry = array.Select((ItemResult r) => new Entry
		{
			Source = r.Source,
			Target = r.Target,
			Directory = r.Directory,
			Length = r.Size
		}).ToList();
		if (repair)
		{
			selectedRule = ExistingRule.Replace;
			selectedRepair = true;
		}
		Start(!repair && currentJob != null && currentJob.CompareOnly);
	}

	private void CompareWholeScope()
	{
		if (busy) return;
		if (currentJob?.CompareSources != null && currentJob.CompareSources.Length > 0)
		{
			sources.Items.Clear();
			AddSources(currentJob.CompareSources);
			destination.Text = currentJob.Destination;
			directCompare.IsChecked = currentJob.CompareContents;
		}
		loadedJob = null;
		selectedRetry = null;
		mode.SelectedIndex = (int)OperationMode.Compare;
		Start(true);
	}

	private bool ApprovePreflight(List<Entry> plan, Preflight preflight, JobRecord job)
	{
		if (automaticTest && !testCancelPreflight)
		{
			return true;
		}
		string text = string.Join(Environment.NewLine, from e in plan.Where((Entry e) => !e.Directory && e.ScanError == null).Take(3)
			select e.Source + Environment.NewLine + "  → " + e.Target);
		string text2 = string.Format("{0:N0} files, {1:N0} folders\nExpected copy: {2:N0}; policy skip: {3:N0}; unknown/scan failures: {4:N0}\nAdditional space estimate: {5}; available: {6}\nPolicy: {7}{11}\nDetails: {8}; access permissions (DACL): {9}\n\n{10}\n\nReplaced destination files will be retained as .clearcopy-replaced-*.bak. Space/access checks are advisory. Continue?", preflight.Files, preflight.Directories, preflight.WillCopy, preflight.WillSkip, preflight.Unknown, Size(preflight.EstimatedBytes), preflight.FreeBytes.HasValue ? Size((long)Math.Min(9223372036854775807uL, preflight.FreeBytes.Value)) : "not available", job.Rule, job.Details, job.Permissions, text, (job.SkipToleranceSeconds > 0) ? (" (modified times within " + job.SkipToleranceSeconds + " s count as same for skips)") : "");
		Window dialog = new Window
		{
			Owner = this,
			Title = "Review copy plan",
			Width = 790.0,
			Height = Math.Min(620.0, SystemParameters.WorkArea.Height - 80.0),
			WindowStartupLocation = WindowStartupLocation.CenterOwner
		};
		DockPanel dockPanel = new DockPanel();
		dockPanel.Margin = new Thickness(16.0);
		DockPanel dockPanel2 = dockPanel;
		dialog.Content = dockPanel2;
		StackPanel stackPanel = new StackPanel();
		stackPanel.Orientation = Orientation.Horizontal;
		stackPanel.HorizontalAlignment = HorizontalAlignment.Right;
		stackPanel.Margin = new Thickness(0.0, 12.0, 0.0, 0.0);
		StackPanel stackPanel2 = stackPanel;
		DockPanel.SetDock(stackPanel2, Dock.Bottom);
		dockPanel2.Children.Add(stackPanel2);
		Button button = new Button();
		button.Content = "Start this copy";
		button.Padding = new Thickness(14.0, 7.0, 14.0, 7.0);
		Button button2 = button;
		button2.Click += (object param0, RoutedEventArgs param1) =>
		{
			dialog.DialogResult = true;
		};
		stackPanel2.Children.Add(button2);
		Button button3 = new Button();
		button3.Content = "Cancel";
		button3.IsCancel = true;
		button3.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
		button3.Padding = new Thickness(14.0, 7.0, 14.0, 7.0);
		Button element = button3;
		stackPanel2.Children.Add(element);
		dockPanel2.Children.Add(new TextBox
		{
			IsReadOnly = true,
			Text = text2,
			TextWrapping = TextWrapping.Wrap,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			BorderThickness = new Thickness(0.0)
		});
		if (testCancelPreflight)
		{
			dialog.Loaded += (object param0, RoutedEventArgs param1) =>
			{
				DispatcherTimer timer = new DispatcherTimer
				{
					Interval = TimeSpan.FromMilliseconds(300.0)
				};
				timer.Tick += (object obj, EventArgs e) =>
				{
					timer.Stop();
					dialog.DialogResult = false;
				};
				timer.Start();
			};
		}
		return dialog.ShowDialog() == true;
	}

	private async void RunDiagnostics()
	{
		string folder = PickFolder(destination.Text);
		if (folder == null || MessageBox.Show(this, "Create and remove a uniquely named disposable test folder inside:\n" + folder + "\n\nThis tests read/write access, comparison, locked files and long paths. It will not change existing files or disconnect your network. Continue?", "Test selected filesystem", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
		{
			return;
		}
		Window window = new Window
		{
			Title = "ClearCopy diagnostics",
			Owner = this,
			Width = 760.0,
			Height = 520.0,
			WindowStartupLocation = WindowStartupLocation.CenterOwner
		};
		TextBox text = (TextBox)(window.Content = new TextBox
		{
			IsReadOnly = true,
			Text = "Running disposable checks…",
			AcceptsReturn = true,
			TextWrapping = TextWrapping.Wrap,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Margin = new Thickness(16.0)
		});
		foreach (Control item in locked)
		{
			item.IsEnabled = false;
		}
		window.Show();
		try
		{
			int num = default;
			_ = num;
			_ = 0;
			try
			{
				string report = (text.Text = await Task.Run(() => Diagnostics.Run(folder, CancellationToken.None)));
				try
				{
					string text3 = Path.Combine(LogDirectory, "diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
					Directory.CreateDirectory(LogDirectory);
					File.WriteAllText(text3, report);
					text.AppendText("\nReport: " + text3);
				}
				catch (Exception ex)
				{
					text.AppendText("\nReport could not be saved: " + ex.Message);
				}
			}
			catch (Exception ex2)
			{
				text.Text = "Diagnostics failed: " + ex2.Message;
			}
		}
		finally
		{
			foreach (Control item2 in locked)
			{
				item2.IsEnabled = true;
			}
		}
	}

	private static bool Recyclable(string path)
	{
		try
		{
			if (path.StartsWith("\\\\", StringComparison.Ordinal))
			{
				return false;
			}
			return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).DriveType == DriveType.Fixed;
		}
		catch
		{
			return false;
		}
	}

	private static void RecycleOrDelete(string path)
	{
		if (Recyclable(path))
		{
			FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
		}
		else
		{
			File.Delete(path);
		}
	}

	private static Task<T> RunSta<T>(Func<T> work)
	{
		TaskCompletionSource<T> done = new TaskCompletionSource<T>();
		Thread thread = new Thread(() =>
		{
			try
			{
				done.SetResult(work());
			}
			catch (Exception exception)
			{
				done.SetException(exception);
			}
		});
		thread.SetApartmentState(ApartmentState.STA);
		thread.IsBackground = true;
		thread.Start();
		return done.Task;
	}

	private void OpenHousekeeping()
	{
		OpenHousekeeping(null, null);
	}

	private void OpenHousekeeping(string testFolder, string testShot)
	{
		ObservableCollection<CleanupCandidate> items = new ObservableCollection<CleanupCandidate>();
		string scope = null;
		string scopeFolder = null;
		Window window = new Window
		{
			Owner = this,
			Title = "Housekeeping — ClearCopy",
			Width = 1040.0,
			Height = 660.0,
			MinWidth = 760.0,
			MinHeight = 480.0,
			WindowStartupLocation = WindowStartupLocation.CenterOwner
		};
		DockPanel dockPanel = new DockPanel();
		dockPanel.Margin = new Thickness(14.0);
		DockPanel dockPanel2 = dockPanel;
		window.Content = dockPanel2;
		TextBlock textBlock = new TextBlock();
		textBlock.TextWrapping = TextWrapping.Wrap;
		textBlock.Margin = new Thickness(0.0, 0.0, 0.0, 8.0);
		textBlock.Text = "Preview ClearCopy's leftovers before removing anything. Nothing is ticked for you. Previous-version backups and temporary .partial files can only be ticked row by row: highlight rows, then use Tick highlighted. Items that may belong to a running job, are open in another program or changed recently are listed but cannot be ticked. Each item is re-checked just before removal, and an audit log is saved with the ClearCopy logs.";
		TextBlock element = textBlock;
		DockPanel.SetDock(element, Dock.Top);
		dockPanel2.Children.Add(element);
		WrapPanel wrapPanel = new WrapPanel();
		wrapPanel.Margin = new Thickness(0.0, 0.0, 0.0, 6.0);
		WrapPanel wrapPanel2 = wrapPanel;
		DockPanel.SetDock(wrapPanel2, Dock.Top);
		dockPanel2.Children.Add(wrapPanel2);
		TextBlock summary = new TextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 8.0, 0.0, 4.0)
		};
		WrapPanel wrapPanel3 = new WrapPanel();
		DockPanel.SetDock(wrapPanel3, Dock.Bottom);
		dockPanel2.Children.Add(wrapPanel3);
		DockPanel.SetDock(summary, Dock.Bottom);
		dockPanel2.Children.Add(summary);
		DataGrid grid = new DataGrid
		{
			IsReadOnly = true,
			AutoGenerateColumns = false,
			SelectionMode = DataGridSelectionMode.Extended,
			SelectionUnit = DataGridSelectionUnit.FullRow,
			ItemsSource = items,
			EnableRowVirtualization = true
		};
		FrameworkElementFactory frameworkElementFactory = new FrameworkElementFactory(typeof(CheckBox));
		frameworkElementFactory.SetBinding(ToggleButton.IsCheckedProperty, new Binding("Selected")
		{
			Mode = BindingMode.TwoWay,
			UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
		});
		frameworkElementFactory.SetBinding(UIElement.IsEnabledProperty, new Binding("Selectable"));
		frameworkElementFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		grid.Columns.Add(new DataGridTemplateColumn
		{
			Header = "Remove",
			CellTemplate = new DataTemplate
			{
				VisualTree = frameworkElementFactory
			},
			Width = 64.0,
			SortMemberPath = "Selected"
		});
		string[][] array = new string[5][]
		{
			new string[4] { "Type", "KindText", "170", "" },
			new string[4] { "Path", "Path", "330", "" },
			new string[4] { "Size", "SizeText", "90", "Bytes" },
			new string[4] { "Modified", "ModifiedText", "125", "NewestUtc" },
			new string[4] { "Notes", "StatusText", "330", "" }
		};
		foreach (string[] array2 in array)
		{
			grid.Columns.Add(new DataGridTextColumn
			{
				Header = array2[0],
				Binding = new Binding(array2[1]),
				Width = double.Parse(array2[2]),
				SortMemberPath = ((array2[3] == "") ? array2[1] : array2[3]),
				CanUserSort = true
			});
		}
		dockPanel2.Children.Add(grid);
		List<Button> buttons = new List<Button>();
		Func<Panel, string, Action, Button> func = (Panel panel, string text, Action action) =>
		{
			Button button = new Button
			{
				Content = text,
				Padding = new Thickness(10.0, 7.0, 10.0, 7.0),
				Margin = new Thickness(0.0, 4.0, 6.0, 4.0)
			};
			button.Click += (object param0, RoutedEventArgs param1) =>
			{
				try
				{
					action();
				}
				catch (Exception ex)
				{
					MessageBox.Show(window, ex.Message, "ClearCopy housekeeping");
				}
			};
			panel.Children.Add(button);
			buttons.Add(button);
			return button;
		};
		Action refreshSummary = () =>
		{
			List<CleanupCandidate> list = items.Where((CleanupCandidate c) => c.Selected).ToList();
			summary.Text = ((scope == null) ? "Choose what to scan." : (scope + ": " + items.Count + " item(s) listed, " + items.Count((CleanupCandidate c) => !c.Selectable) + " cannot be ticked.")) + "   Ticked: " + list.Count + " item(s), " + Housekeeping.Size(list.Sum((CleanupCandidate c) => c.Bytes)) + "." + (list.Any((CleanupCandidate c) => !Recyclable(c.Path)) ? "  Some ticked items are on a network or removable drive and would be deleted permanently." : "");
		};
		Action<List<CleanupCandidate>, string, string> show = (List<CleanupCandidate> found, string label, string folder) =>
		{
			items.Clear();
			foreach (CleanupCandidate item in found.OrderBy((CleanupCandidate c) => c.Kind).ThenBy((CleanupCandidate c) => c.Path, StringComparer.OrdinalIgnoreCase))
			{
				item.PropertyChanged += (object param0, PropertyChangedEventArgs param1) =>
				{
					refreshSummary();
				};
				items.Add(item);
			}
			scope = label;
			scopeFolder = folder;
			refreshSummary();
		};
		Func<Func<List<CleanupCandidate>>, string, string, Task> scan = async (Func<List<CleanupCandidate>> work, string label, string folder) =>
		{
			foreach (Button item2 in buttons)
			{
				item2.IsEnabled = false;
			}
			summary.Text = "Scanning…";
			try
			{
				int num = default;
				_ = num;
				_ = 0;
				try
				{
					List<CleanupCandidate> found = await Task.Run(work);
					show(found, label, folder);
				}
				catch (Exception ex)
				{
					summary.Text = "Scan failed: " + ex.Message;
				}
			}
			finally
			{
				foreach (Button item3 in buttons)
				{
					item3.IsEnabled = true;
				}
			}
		};
		Action scanAppData = () =>
		{
			string appRoot = AppDataRoot;
			scan(() => Housekeeping.ScanAppData(appRoot, DateTime.UtcNow), "ClearCopy data (" + appRoot + ")", null);
		};
		Action<string> scanFolder = (string folder) =>
		{
			string jobs = JobsDirectory;
			string logs = LogDirectory;
			scan(() => Housekeeping.ScanDestination(folder, Housekeeping.ActiveJobs(jobs), Housekeeping.BackupOrigins(logs), DateTime.UtcNow, CancellationToken.None), "Backups and temporary copies under " + folder, folder);
		};
		func(wrapPanel2, "Scan ClearCopy data (logs, saved jobs, paste requests)", scanAppData);
		func(wrapPanel2, "Scan a destination folder for backups and .partial files…", () =>
		{
			string text = PickFolder(string.IsNullOrWhiteSpace(destination.Text) ? null : destination.Text);
			if (text != null)
			{
				scanFolder(text);
			}
		});
		func(wrapPanel3, "Tick highlighted", () =>
		{
			foreach (CleanupCandidate item4 in grid.SelectedItems.Cast<CleanupCandidate>())
			{
				item4.Selected = true;
			}
		});
		func(wrapPanel3, "Untick highlighted", () =>
		{
			foreach (CleanupCandidate item5 in grid.SelectedItems.Cast<CleanupCandidate>())
			{
				item5.Selected = false;
			}
		});
		func(wrapPanel3, "Tick paste requests", () =>
		{
			foreach (CleanupCandidate item6 in items.Where((CleanupCandidate c) => c.Kind == CleanupKind.PasteRequest))
			{
				item6.Selected = true;
			}
		});
		func(wrapPanel3, "Tick logs older than 30 days", () =>
		{
			DateTime cutoff = DateTime.UtcNow.AddDays(-30.0);
			foreach (CleanupCandidate item7 in items.Where((CleanupCandidate c) => c.Kind == CleanupKind.LogSet && c.NewestUtc < cutoff))
			{
				item7.Selected = true;
			}
		});
		func(wrapPanel3, "Untick all", () =>
		{
			foreach (CleanupCandidate item8 in items)
			{
				item8.Selected = false;
			}
		});
		func(wrapPanel3, "Remove ticked…", async () =>
		{
			List<CleanupCandidate> ticked = items.Where((CleanupCandidate c) => c.Selected).ToList();
			if (ticked.Count == 0)
			{
				MessageBox.Show(window, "Tick the items you want to remove first.", "ClearCopy housekeeping");
			}
			else
			{
				List<CleanupCandidate> permanent = ticked.Where((CleanupCandidate c) => !Recyclable(c.Path)).ToList();
				int backups = ticked.Count((CleanupCandidate c) => c.Kind == CleanupKind.ReplacedBackup);
				int partials = ticked.Count((CleanupCandidate c) => c.Kind == CleanupKind.PartialTemp);
				int jobs = ticked.Count((CleanupCandidate c) => c.Kind == CleanupKind.JobRecord);
				if (MessageBox.Show(messageBoxText: "Remove " + ticked.Count + " ticked item(s), " + Housekeeping.Size(ticked.Sum((CleanupCandidate c) => c.Bytes)) + "?\n\n" + ((backups > 0) ? (backups + " previous-version backup(s): the only copy of what those files contained before ClearCopy replaced them.\n") : "") + ((partials > 0) ? (partials + " temporary .partial file(s) from interrupted jobs.\n") : "") + ((jobs > 0) ? (jobs + " saved job record(s): those jobs can no longer be resumed.\n") : "") + "\nItems on fixed local drives go to the Recycle Bin.", owner: window, caption: "Confirm housekeeping", button: MessageBoxButton.YesNo, icon: MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
				{
					List<CleanupCandidate> chosen = ticked;
					if (permanent.Count > 0)
					{
						switch (MessageBox.Show(window, permanent.Count + " ticked item(s), " + Housekeeping.Size(permanent.Sum((CleanupCandidate c) => c.Bytes)) + ", are on a network or removable drive. Those locations have no Recycle Bin, so they would be deleted permanently.\n\nYes: also delete those permanently.\nNo: remove only the Recycle Bin items.\nCancel: remove nothing.", "Permanent deletion", MessageBoxButton.YesNoCancel, MessageBoxImage.Exclamation))
						{
						case MessageBoxResult.None:
						case MessageBoxResult.Cancel:
							return;
						case MessageBoxResult.No:
							chosen = ticked.Where((CleanupCandidate c) => Recyclable(c.Path)).ToList();
							break;
						}
					}
					if (chosen.Count != 0)
					{
						foreach (Button item9 in buttons)
						{
							item9.IsEnabled = false;
						}
						summary.Text = "Re-checking and removing…";
						string jobsDir = JobsDirectory;
						string logs = LogDirectory;
						string label = scope;
						try
						{
							int num = default;
							_ = num;
							_ = 0;
							try
							{
								CleanupOutcome outcome = await RunSta(() => Housekeeping.Remove(chosen, jobsDir, RecycleOrDelete, DateTime.UtcNow));
								string audit;
								try
								{
									audit = Housekeeping.WriteAudit(logs, outcome, "ClearCopy housekeeping — " + label);
								}
								catch (Exception ex)
								{
									audit = "(audit log could not be saved: " + ex.Message + ")";
								}
								MessageBox.Show(window, "Removed " + outcome.Removed + " item(s), " + Housekeeping.Size(outcome.BytesRemoved) + ". Skipped " + outcome.Skipped + " item(s) that changed, failed or became active since the preview.\n\nAudit log: " + audit, "ClearCopy housekeeping");
							}
							catch (Exception ex2)
							{
								MessageBox.Show(window, "Housekeeping stopped: " + ex2.Message, "ClearCopy housekeeping");
							}
						}
						finally
						{
							foreach (Button item10 in buttons)
							{
								item10.IsEnabled = true;
							}
						}
						if (scopeFolder != null)
						{
							scanFolder(scopeFolder);
						}
						else
						{
							scanAppData();
						}
					}
				}
			}
		});
		func(wrapPanel3, "Close", () =>
		{
			window.Close();
		});
		refreshSummary();
		if (testFolder != null)
		{
			window.Loaded += (object param0, RoutedEventArgs param1) =>
			{
				scanFolder(testFolder);
				DispatcherTimer timer = new DispatcherTimer
				{
					Interval = TimeSpan.FromSeconds(2.0)
				};
				timer.Tick += (object obj, EventArgs e) =>
				{
					timer.Stop();
					try
					{
						CleanupCandidate cleanupCandidate = items.FirstOrDefault((CleanupCandidate c) => c.Selectable);
						if (cleanupCandidate != null)
						{
							grid.SelectedItems.Clear();
							grid.SelectedItems.Add(cleanupCandidate);
							foreach (CleanupCandidate item11 in grid.SelectedItems.Cast<CleanupCandidate>())
							{
								item11.Selected = true;
							}
						}
						foreach (CleanupCandidate item12 in items.Where((CleanupCandidate c) => !c.Selectable))
						{
							item12.Selected = true;
						}
						window.UpdateLayout();
						RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96.0, 96.0, PixelFormats.Pbgra32);
						renderTargetBitmap.Render(window);
						PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder
						{
							Frames = { BitmapFrame.Create(renderTargetBitmap) }
						};
						using (FileStream stream = File.Create(testShot))
						{
							pngBitmapEncoder.Save(stream);
						}
						File.WriteAllText(testShot + ".txt", summary.Text + Environment.NewLine + string.Join(Environment.NewLine, items.Select((CleanupCandidate c) => (c.Selected ? "[x] " : "[ ] ") + c.KindText + " | " + c.Path + " | " + c.SizeText + " | " + c.StatusText)));
					}
					catch (Exception ex)
					{
						File.WriteAllText(testShot + ".txt", ex.ToString());
					}
					window.Close();
				};
				timer.Start();
			};
		}
		window.ShowDialog();
	}
}
