// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using System.IO;
using Windows.ApplicationModel.Activation;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace Files.App.Views
{
	public sealed partial class PhotoViewerWindow
	{
		private bool photoTaskbarIdentity;

		private static bool taskbarRefreshPending;

		internal static bool TryGetTaskActivation(object? args, out int processId, out string command)
		{
			var text = args switch
			{
				ILaunchActivatedEventArgs launch => launch.Arguments,
				ICommandLineActivatedEventArgs line => line.Operation.Arguments,
				_ => null
			};
			if (TryParseTaskArguments(text, out processId, out command)) return true;
			if (text is null) return false;
			var parts = CommandLineParser.SplitArguments(text, true);
			return parts.Length == 2 && string.Equals(Path.GetFileName(parts[0]), "Files.exe", StringComparison.OrdinalIgnoreCase) && TryParseTaskArguments(parts[1], out processId, out command);
		}

		private static void RefreshPhotoTaskbar()
		{
			if (taskbarRefreshPending || !HasOpenWindows) return;
			taskbarRefreshPending = true;
			if (!MainWindow.Instance.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
			{
				try
				{
					var commands = new List<(string Command, string Label)> { ("sets", Strings.PhotoViewerWindowSets.GetLocalizedResource()) };
					commands.AddRange(GetArrangementCommands().Where(item => item.Command != "close"));
					commands.Add(("fill", Strings.PhotoViewerFillAll.GetLocalizedResource()));
					commands.Add(("close", Strings.PhotoViewerCloseAll.GetLocalizedResource()));
					var tasks = commands.Select(item => new WindowTaskbar.TaskItem(item.Label, $"{TaskArgumentPrefix}{Environment.ProcessId}:{item.Command}",
						item.Command == "sets" ? Path.Combine(AppContext.BaseDirectory, "Assets", "PhotoViewer.ico") : Path.Combine(AppContext.BaseDirectory, "Assets", "PhotoLayouts", item.Command + ".ico"))).ToArray();
					var executable = Environment.ProcessPath!;
					await STATask.Run(() => WindowTaskbar.SetTasks("Files.PhotoViewer", executable, tasks), App.Logger);
				}
				finally { taskbarRefreshPending = false; }
			})) taskbarRefreshPending = false;
		}

		private void InitializePhotoTaskbar()
		{
			AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "PhotoViewer.ico"));
			photoTaskbarIdentity = true;
			try
			{
				Files.Storage.WindowTaskbar.SetPhotoViewerIdentity(WindowHandle, "Files.PhotoViewer");
			}
			catch (Exception ex) { Debug.WriteLine(ex); }
		}

		private void ClearPhotoTaskbar()
		{
			if (!photoTaskbarIdentity) return;
			photoTaskbarIdentity = false;
			try { Files.Storage.WindowTaskbar.SetPhotoViewerIdentity(WindowHandle, null); }
			catch (Exception ex) { Debug.WriteLine(ex); }
		}
	}
}
