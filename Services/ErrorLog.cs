using System;
using System.Diagnostics;
using System.IO;

namespace GoBase.Services
{
	// Failures, with their stack, in %LOCALAPPDATA%\GoBase\gobase.log, and in the debug output.
	// The file is there for when no debugger or DebugView is listening.
	internal static class ErrorLog
	{
		public static string FilePath { get; } = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoBase", "gobase.log");

		private static readonly object Gate = new();

		public static void Write(string message)
		{
			Debug.WriteLine(message);
			try
			{
				lock (Gate)
				{
					Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
					File.AppendAllText(FilePath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
				}
			}
			catch (Exception)
			{
				// Logging must never be the reason something else fails.
			}
		}
	}
}
