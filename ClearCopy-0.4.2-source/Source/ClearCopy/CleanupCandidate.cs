using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace ClearCopy;

public sealed class CleanupCandidate : INotifyPropertyChanged
{
	public readonly List<CleanupFile> Files = new List<CleanupFile>();

	public string JobDestination;

	private bool selected;

	public CleanupKind Kind { get; set; }

	public string Path { get; set; }

	public long Bytes { get; set; }

	public DateTime NewestUtc { get; set; }

	public string Detail { get; set; }

	public string Excluded { get; set; }

	public bool Selectable => Excluded == null;

	public string KindText => Kind switch
	{
		CleanupKind.PasteRequest => "Paste request", 
		CleanupKind.LogSet => "Job log + reports", 
		CleanupKind.JobRecord => "Saved job record", 
		CleanupKind.ReplacedBackup => "Previous-version backup", 
		CleanupKind.Unreadable => "Folder not scanned", 
		_ => "Temporary copy (.partial)", 
	};

	public string SizeText => Housekeeping.Size(Bytes);

	public string ModifiedText
	{
		get
		{
			if (!(NewestUtc == DateTime.MinValue))
			{
				return NewestUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
			}
			return "";
		}
	}

	public string StatusText => Excluded ?? Detail;

	public bool Selected
	{
		get
		{
			return selected;
		}
		set
		{
			bool flag = value && Excluded == null;
			if (flag == selected)
			{
				Changed("Selected");
				return;
			}
			selected = flag;
			Changed("Selected");
		}
	}

	public event PropertyChangedEventHandler PropertyChanged;

	private void Changed(string name)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
