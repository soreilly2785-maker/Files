// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Files.App.Views
{
	public sealed partial class PhotoViewerWindow
	{
		private const double MaxDecodedPixels = 16 * 1024 * 1024;
		private static int latestOpening;
		private readonly int opening = ++latestOpening;
		private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer openingFocusTimer;
		private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer navigationHoverTimer;
		private bool openingImage = true;
		private double zoom = 1;
		private bool fillWindow;
		private double panX;
		private double panY;
		private uint? panPointer;
		private Point panStart;
		private Point panOrigin;

		private void InitializeImageInteraction()
		{
			canvas.PointerWheelChanged += OnPhotoWheelChanged;
			canvas.DoubleTapped += (_, e) =>
			{
				if (ReferenceEquals(e.OriginalSource, image) || ReferenceEquals(e.OriginalSource, canvas))
				{
					ResetView();
					e.Handled = true;
				}
			};
			image.PointerPressed += (_, e) =>
			{
				if (!HasCurrentPhoto || !e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed || !image.CapturePointer(e.Pointer)) return;
				panPointer = e.Pointer.PointerId;
				panStart = e.GetCurrentPoint(canvas).Position;
				panOrigin = new(panX, panY);
				e.Handled = true;
			};
			image.PointerMoved += (_, e) =>
			{
				if (panPointer != e.Pointer.PointerId) return;
				var point = e.GetCurrentPoint(canvas).Position;
				panX = panOrigin.X + point.X - panStart.X;
				panY = panOrigin.Y + point.Y - panStart.Y;
				FitImage();
				e.Handled = true;
			};
			image.PointerReleased += (_, e) =>
			{
				if (panPointer != e.Pointer.PointerId) return;
				panPointer = null;
				image.ReleasePointerCapture(e.Pointer);
				e.Handled = true;
			};
			image.PointerCaptureLost += (_, _) => panPointer = null;
		}

		private void OnPhotoWheelChanged(object sender, PointerRoutedEventArgs e)
		{
			if (!HasCurrentPhoto) return;
			var point = e.GetCurrentPoint(canvas);
			if (point.Properties.IsHorizontalMouseWheel) return;
			zoom = EffectiveZoom();
			fillWindow = false;
			var nextZoom = Math.Clamp(zoom * Math.Pow(1.1, point.Properties.MouseWheelDelta / 120d), 0.1, 32);
			var ratio = nextZoom / zoom;
			panX = ratio * panX + (1 - ratio) * (point.Position.X - canvas.ActualWidth / 2);
			panY = ratio * panY + (1 - ratio) * (point.Position.Y - canvas.ActualHeight / 2);
			zoom = nextZoom;
			FitImage();
			resizeTimer.Stop();
			resizeTimer.Start();
			e.Handled = true;
		}

		private double EffectiveZoom()
		{
			if (!fillWindow || pixelWidth == 0 || pixelHeight == 0 || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return zoom;
			var width = rotation % 2 == 0 ? pixelWidth : pixelHeight;
			var height = rotation % 2 == 0 ? pixelHeight : pixelWidth;
			var horizontal = canvas.ActualWidth / width;
			var vertical = canvas.ActualHeight / height;
			return zoom * Math.Max(horizontal, vertical) / Math.Min(horizontal, vertical);
		}

		private void FillWindow()
		{
			fillWindow = true;
			ResetImageTransform(false);
		}

		private void ResetView() => ResetImageTransform(true);

		private void ResetImageTransform(bool clearFill)
		{
			if (clearFill) fillWindow = false;
			panPointer = null;
			image.ReleasePointerCaptures();
			zoom = 1;
			panX = panY = 0;
			FitImage();
			resizeTimer.Stop();
			resizeTimer.Start();
		}

		private void UpdateViewport()
		{
			canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight) };
		}

		private void UpdateNavigationHover(Point point)
		{
			var middle = Math.Abs(point.Y - canvas.ActualHeight / 2) <= 64;
			previous.Opacity = middle && point.X <= 72 ? 1 : 0;
			next.Opacity = middle && point.X >= canvas.ActualWidth - 72 ? 1 : 0;
			previous.IsHitTestVisible = previous.Opacity != 0;
			next.IsHitTestVisible = next.Opacity != 0;
			navigationHoverTimer.Stop();
			if (previous.IsHitTestVisible || next.IsHitTestVisible) navigationHoverTimer.Start();
		}

		private void HideNavigation()
		{
			previous.Opacity = next.Opacity = 0;
			previous.IsHitTestVisible = next.IsHitTestVisible = false;
		}

		private void BringOpeningToFront()
		{
			if (closed || opening != latestOpening) return;
			var foreground = PInvoke.GetForegroundWindow();
			if (foreground != new HWND(MainWindow.Instance.WindowHandle) && foreground != new HWND(WindowHandle)) return;
			RaiseToFront();
		}

		private void RaiseToFront()
		{
			// Activation alone can leave an active window behind another window in the Z order.
			PInvoke.SetWindowPos(new(WindowHandle), new HWND(0), 0, 0, 0, 0,
				SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
			Win32Helper.BringToForegroundEx(new(WindowHandle));
			host.Focus(FocusState.Programmatic);
		}
	}
}
