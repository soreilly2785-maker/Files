// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.IO;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Files.App.Views
{
	public sealed partial class PhotoViewerWindow
	{
		private byte[]? previewBytes;
		private string? previewPath;

		private static async Task<byte[]> EncodeSetPreviewAsync(SoftwareBitmap sample, CancellationToken token)
		{
			using var stream = new InMemoryRandomAccessStream();
			var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
			token.ThrowIfCancellationRequested();
			encoder.SetSoftwareBitmap(sample);
			var scale = Math.Min(1, 256d / Math.Max(sample.PixelWidth, sample.PixelHeight));
			encoder.BitmapTransform.ScaledWidth = Math.Max(1, (uint)(sample.PixelWidth * scale));
			encoder.BitmapTransform.ScaledHeight = Math.Max(1, (uint)(sample.PixelHeight * scale));
			encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
			await encoder.FlushAsync();
			token.ThrowIfCancellationRequested();
			stream.Seek(0);
			var bytes = new byte[(int)stream.Size];
			await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
			token.ThrowIfCancellationRequested();
			return bytes;
		}

		private static WindowSetsWindow? setsPanel;

		private void ShowWindowSets()
		{
			setsPanel ??= new WindowSetsWindow(this);
			setsPanel.Activate();
			Win32Helper.BringToForegroundEx(new(setsPanel.WindowHandle));
		}

		private static void RefreshWindowSetsPanel() => setsPanel?.QueueRefresh();

		private static PhotoWindowSet[] GetWindowSets()
		{
			var ungrouped = openWindows.Where(window => !window.closed && !windowSets.Any(set => set.Windows.Contains(window))).ToList();
			if (ungrouped.Count != 0) windowSets.Add(new(ungrouped, Math.Min(3, ungrouped.Count), (ungrouped.Count + 2) / 3));
			return windowSets.Where(set => set.Windows.Count != 0).ToArray();
		}

		private static async Task LoadSetPreviewAsync(Image image, byte[]? bytes)
		{
			if (bytes is not null) image.Source = await bytes.ToThumbnailBitmapAsync(256);
		}
	}
}
