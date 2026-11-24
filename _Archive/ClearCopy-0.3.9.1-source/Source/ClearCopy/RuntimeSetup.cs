using System;

namespace ClearCopy;

public static class RuntimeSetup
{
	public static void EnableLongPaths()
	{
		AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", isEnabled: false);
		AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", isEnabled: false);
	}
}
