// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Windows.Graphics;
using Files.App.Utils;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace Files.App.Views
{
	public sealed partial class PhotoViewerWindow
	{
		private int presentationRevision;
		private sealed record ViewCrop(string Path, PhotoViewGeometry.Crop Area, bool Ready = true);

		private ViewCrop CaptureViewCrop()
		{
			if (!HasCurrentPhoto || pixelWidth == 0 || pixelHeight == 0)
				return new(paths[index], new(Math.Max(1, AppWindow.ClientSize.Width), Math.Max(1, AppWindow.ClientSize.Height), 0.5, 0.5), false);
			var width = rotation % 2 == 0 ? pixelWidth : pixelHeight;
			var height = rotation % 2 == 0 ? pixelHeight : pixelWidth;
			return new(paths[index], PhotoViewGeometry.Capture(width, height, Math.Max(1, canvas.ActualWidth), Math.Max(1, canvas.ActualHeight), EffectiveZoom(), panX, panY));
		}

		private void RestoreViewCrop(ViewCrop crop, int revision)
		{
			if (!crop.Ready || closed || revision != presentationRevision || !StringComparer.OrdinalIgnoreCase.Equals(paths[index], crop.Path)) return;
			var width = Math.Max(1, rotation % 2 == 0 ? pixelWidth : pixelHeight);
			var height = Math.Max(1, rotation % 2 == 0 ? pixelHeight : pixelWidth);
			var view = PhotoViewGeometry.Restore(width, height, Math.Max(1, canvas.ActualWidth), Math.Max(1, canvas.ActualHeight), crop.Area);
			zoom = 1;
			var fill = EffectiveZoom();
			zoom = view.Zoom / fill;
			panX = view.PanX;
			panY = view.PanY;
			FitImage();
			resizeTimer.Stop();
			resizeTimer.Start();
		}

		private static void AutoArrangeSet(PhotoWindowSet set, RectInt32 area)
		{
			var crops = set.Windows.Select(window => window.CaptureViewCrop()).ToArray();
			var regions = Files.App.Utils.PhotoLayoutPlanner.Calculate(crops.Select(crop => crop.Area.Width / crop.Area.Height).ToArray(), (double)area.Width / area.Height);
			set.Regions = regions.ToList();
			set.FrameAspect = (double)area.Width / area.Height;
			for (var i = 0; i < set.Windows.Count; i++)
			{
				var window = set.Windows[i];
				var crop = crops[i];
				var revision = ++window.presentationRevision;
				window.Presenter?.Restore();
				var region = regions[i];
				var left = area.X + (int)Math.Round(region.X * area.Width);
				var top = area.Y + (int)Math.Round(region.Y * area.Height);
				var right = area.X + (int)Math.Round((region.X + region.Width) * area.Width);
				var bottom = area.Y + (int)Math.Round((region.Y + region.Height) * area.Height);
				window.AppWindow.MoveAndResize(new(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top)));
				window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => window.RestoreViewCrop(crop, revision));
			}
		}
	}
}
