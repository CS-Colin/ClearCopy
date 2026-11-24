using System.Collections.Generic;

namespace ClearCopy;

public sealed class CopyPlan : List<Entry>
{
	public string[] Roots;

	public string Destination;

	public bool Contents;
}
