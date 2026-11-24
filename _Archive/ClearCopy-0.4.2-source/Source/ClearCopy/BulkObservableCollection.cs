using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ClearCopy;

// ObservableCollection that can add many items with a single "reset"
// notification, so a bound grid redraws once instead of once per item.
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
	public void AddRange(IEnumerable<T> items)
	{
		CheckReentrancy();
		int added = 0;
		foreach (T item in items)
		{
			Items.Add(item);
			added++;
		}
		if (added == 0)
		{
			return;
		}
		OnPropertyChanged(new PropertyChangedEventArgs("Count"));
		OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
		OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
	}
}
