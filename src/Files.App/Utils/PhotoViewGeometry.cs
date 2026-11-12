// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

namespace Files.App.Utils
{
	internal static class PhotoViewGeometry
	{
		internal readonly record struct Crop(double Width, double Height, double CenterX, double CenterY);
		internal readonly record struct View(double Zoom, double PanX, double PanY);

		internal static Crop Capture(double width, double height, double frameWidth, double frameHeight, double zoom, double panX, double panY)
		{
			var scale = Math.Min(frameWidth / width, frameHeight / height) * zoom;
			var imageWidth = width * scale;
			var imageHeight = height * scale;
			var left = (frameWidth - imageWidth) / 2 + panX;
			var top = (frameHeight - imageHeight) / 2 + panY;
			var visibleLeft = Math.Max(0, left);
			var visibleTop = Math.Max(0, top);
			var visibleRight = Math.Min(frameWidth, left + imageWidth);
			var visibleBottom = Math.Min(frameHeight, top + imageHeight);
			if (visibleRight <= visibleLeft || visibleBottom <= visibleTop) return new(width, height, 0.5, 0.5);
			return new((visibleRight - visibleLeft) / scale, (visibleBottom - visibleTop) / scale,
				((visibleLeft + visibleRight) / 2 - left) / imageWidth, ((visibleTop + visibleBottom) / 2 - top) / imageHeight);
		}

		internal static View Restore(double width, double height, double frameWidth, double frameHeight, Crop crop)
		{
			var fit = Math.Min(frameWidth / width, frameHeight / height);
			var scale = Math.Min(frameWidth / crop.Width, frameHeight / crop.Height);
			return new(scale / fit, (0.5 - crop.CenterX) * width * scale, (0.5 - crop.CenterY) * height * scale);
		}
	}
}
