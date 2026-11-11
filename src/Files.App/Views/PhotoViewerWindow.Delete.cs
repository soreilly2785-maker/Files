// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Microsoft.UI.Xaml.Controls;
using System.IO;
using Windows.Storage;

namespace Files.App.Views
{
	public sealed partial class PhotoViewerWindow
	{
		private static readonly HashSet<string> pendingDeletes = new(StringComparer.OrdinalIgnoreCase);
		private bool deleting;

		private async Task DeletePhotoAsync(bool permanently)
		{
			if (closed || deleting || exporting || !HasCurrentPhoto) return;
			var path = paths[index];
			if (!pendingDeletes.Add(path)) return;
			deleting = true;
			try
			{
				var trash = Ioc.Default.GetRequiredService<IStorageTrashBinService>();
				var fromTrash = trash.IsUnderTrashBin(path);
				permanently |= fromTrash || !await trash.CanGoTrashBin(path);
				if (closed) return;
				var dialog = new ContentDialog
				{
					XamlRoot = host.XamlRoot,
					RequestedTheme = host.ActualTheme,
					Title = permanently ? Strings.DeletePermanently.GetLocalizedResource() : Strings.Delete.GetLocalizedResource(),
					Content = string.Format((permanently ? Strings.PhotoViewerConfirmPermanentDelete : Strings.PhotoViewerConfirmRecycle).GetLocalizedResource(), Path.GetFileName(path)),
					PrimaryButtonText = permanently ? Strings.DeletePermanently.GetLocalizedResource() : Strings.Delete.GetLocalizedResource(),
					CloseButtonText = Strings.Cancel.GetLocalizedResource(),
					DefaultButton = ContentDialogButton.Primary
				};
				if (await dialog.ShowAsync() != ContentDialogResult.Primary || closed) return;
				// Resolve as a file again after confirmation; never delete a folder from the viewer.
				_ = await StorageFile.GetFileFromPathAsync(path);
				string[] deletePaths = fromTrash && ShellFilesystemOperations.GetRecycleMetadataPath(path) is { } metadata ? [path, metadata] : [path];
				var (success, result) = await FileOperationsHelpers.DeleteItemAsync(deletePaths, permanently, WindowHandle.ToInt64(), false, null);
				var deleted = result.Final.FirstOrDefault(item => item.Succeeded && StringComparer.OrdinalIgnoreCase.Equals(item.Source, path));
				if (!success || deleted is null)
				{
					if (!closed) status.Text = Strings.PhotoViewerDeleteError.GetLocalizedResource();
					return;
				}
				foreach (var window in openWindows.ToArray()) window.RemoveDeletedPhoto(path);
				if (!permanently && !string.IsNullOrEmpty(deleted.Destination))
					App.HistoryWrapper.AddHistory(new StorageHistory(FileOperationType.Recycle,
						StorageHelpers.FromPathAndType(path, FilesystemItemType.File), StorageHelpers.FromPathAndType(deleted.Destination, FilesystemItemType.File)));
			}
			catch (Exception ex)
			{
				Debug.WriteLine(ex);
				if (!closed) status.Text = Strings.PhotoViewerDeleteError.GetLocalizedResource();
			}
			finally { deleting = false; pendingDeletes.Remove(path); }
		}

		private void RemoveDeletedPhoto(string path)
		{
			if (closed || !paths.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
			var currentPath = paths[index];
			var remaining = paths.Where(item => !StringComparer.OrdinalIgnoreCase.Equals(item, path)).ToArray();
			if (remaining.Length == 0) { Close(); return; }
			var reload = StringComparer.OrdinalIgnoreCase.Equals(currentPath, path) || StringComparer.OrdinalIgnoreCase.Equals(displayedPhoto?.Path, path);
			var nextIndex = Array.FindIndex(remaining, item => StringComparer.OrdinalIgnoreCase.Equals(item, currentPath));
			index = nextIndex >= 0 ? nextIndex : index % remaining.Length;
			paths = remaining;
			if (reload) { loadCancellation?.Cancel(); ClearImage(); }
			if (photoCache.Remove(path, out var cached)) cached.Dispose();
			if (reload) { rotation = 0; flipped = false; ResetImageTransform(false); StartLoad(); }
			FitImage();
			RefreshWindowSetsPanel();
		}
	}
}
