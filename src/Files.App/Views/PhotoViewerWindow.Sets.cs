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
			var scale = Math.Min(1, 128d / Math.Max(sample.PixelWidth, sample.PixelHeight));
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

		private void ShowWindowSets()
		{
			var sets = windowSets.Where(set => set.Windows.Any(window => !window.closed)).ToList();
			var ungrouped = openWindows.Where(window => !window.closed && !sets.Any(set => set.Windows.Contains(window))).ToList();
			if (ungrouped.Count != 0) sets.Add(new(ungrouped, Math.Min(3, ungrouped.Count), (ungrouped.Count + 2) / 3));
			var cards = new StackPanel { Spacing = 8 };
			var flyout = new Flyout { Content = new ScrollViewer { Content = cards, MaxHeight = 560, MaxWidth = 620, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
			for (var index = 0; index < sets.Count; index++)
			{
				var set = sets[index];
				var windows = set.Windows.Where(window => !window.closed).ToArray();
				var content = new StackPanel { Spacing = 6 };
				content.Children.Add(new TextBlock { Text = string.Format(Strings.PhotoViewerSetLabel.GetLocalizedResource(), index + 1, windows.Length) });
				var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
				for (var column = 0; column < set.Columns; column++) grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
				for (var row = 0; row < set.Rows; row++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
				var width = Math.Min(112, 520d / set.Columns);
				for (var i = 0; i < windows.Length; i++)
				{
					var window = windows[i];
					var preview = new Image
					{
						Width = window.rotation % 2 == 0 ? width : 84,
						Height = window.rotation % 2 == 0 ? 84 : width,
						Stretch = Stretch.Uniform,
						RenderTransformOrigin = new(0.5, 0.5),
						RenderTransform = new CompositeTransform { Rotation = window.rotation * 90, ScaleX = window.flipped ? -1 : 1 },
						HorizontalAlignment = HorizontalAlignment.Center,
						VerticalAlignment = VerticalAlignment.Center
					};
					var cell = new Grid { Width = width, Height = 84, Background = window.canvas.Background };
					cell.Children.Add(preview);
					ToolTipService.SetToolTip(cell, Path.GetFileName(window.paths[window.index]));
					Grid.SetColumn(cell, i % set.Columns);
					Grid.SetRow(cell, i / set.Columns);
					grid.Children.Add(cell);
					_ = LoadSetPreviewAsync(preview, window.previewBytes);
				}
				content.Children.Add(grid);
				var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, HorizontalAlignment = HorizontalAlignment.Stretch };
				button.Click += (_, _) =>
				{
					flyout.Closed += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
					{
						foreach (var window in windows)
						{
							if (window.closed) continue;
							if (window.Presenter is { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter) presenter.Restore();
							window.RaiseToFront();
						}
					});
					flyout.Hide();
				};
				cards.Children.Add(button);
			}
			flyout.ShowAt(canvas);
		}

		private static async Task LoadSetPreviewAsync(Image image, byte[]? bytes)
		{
			if (bytes is not null) image.Source = await bytes.ToThumbnailBitmapAsync(128);
		}
	}
}
