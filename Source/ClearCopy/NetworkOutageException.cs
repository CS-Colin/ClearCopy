using System;
using System.IO;

namespace ClearCopy;

// Raised when a network share stays unreachable for longer than the engine's
// OutageWaitLimit. It derives from OperationCanceledException on purpose: the
// engine then runs exactly the same, already tested, clean-stop path as a user
// cancel (current file marked unchecked, nothing partial trusted, unvisited
// files reported as not reached), and the job can be resumed later.
public sealed class NetworkOutageException : OperationCanceledException
{
	public string Path { get; }

	public NetworkOutageException(string path, TimeSpan waited, Exception last)
		: base("Network location unavailable for " + Describe(waited) + " (" + path + "): " + Clean(last.Message) + ". Completed files are kept; load this job to resume when the network is back.", last)
	{
		Path = path;
	}

	// Windows error text ends with a full stop and a line break.
	public static string Clean(string message)
	{
		return (message ?? "").Replace("\r", " ").Replace("\n", " ").Trim().TrimEnd('.', ' ');
	}

	public static string Describe(TimeSpan waited)
	{
		return (waited.TotalMinutes >= 1.0) ? (Math.Round(waited.TotalMinutes, 1) + " min") : (Math.Max(1, (int)Math.Round(waited.TotalSeconds)) + " s");
	}

	// Windows error codes that mean "the network or share went away", as
	// opposed to a problem with the file itself.
	public static bool IsNetworkError(Exception ex)
	{
		if (!(ex is IOException) || ex is FileNotFoundException || ex is DirectoryNotFoundException || ex is PathTooLongException)
		{
			return false;
		}
		switch (ex.HResult & 0xFFFF)
		{
		case 51:   // ERROR_REM_NOT_LIST
		case 53:   // ERROR_BAD_NETPATH
		case 54:   // ERROR_NETWORK_BUSY
		case 55:   // ERROR_DEV_NOT_EXIST
		case 59:   // ERROR_UNEXP_NET_ERR
		case 64:   // ERROR_NETNAME_DELETED
		case 67:   // ERROR_BAD_NET_NAME
		case 121:  // ERROR_SEM_TIMEOUT
		case 1222: // ERROR_NO_NETWORK
		case 1231: // ERROR_NETWORK_UNREACHABLE
		case 1232: // ERROR_HOST_UNREACHABLE
		case 1236: // ERROR_CONNECTION_ABORTED
		case 2250: // ERROR_NOT_CONNECTED
			return true;
		default:
			return false;
		}
	}
}
