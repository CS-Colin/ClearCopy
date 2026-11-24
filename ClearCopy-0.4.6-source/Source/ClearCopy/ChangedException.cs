using System.IO;

namespace ClearCopy;

public sealed class ChangedException : IOException
{
	public ChangedException(string message)
		: base(message)
	{
	}
}
