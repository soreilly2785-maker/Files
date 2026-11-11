// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using System.IO;

namespace Files.App.Views
{
	public sealed partial class PhotoViewerWindow
	{
		private bool photoTaskbarIdentity;

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
