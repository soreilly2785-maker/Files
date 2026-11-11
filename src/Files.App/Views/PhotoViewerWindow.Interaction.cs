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
		private bool openingImage = true;
		private double zoom = 1;
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
				if (pixelWidth == 0 || !e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed || !image.CapturePointer(e.Pointer)) return;
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
			if (pixelWidth == 0) return;
			var point = e.GetCurrentPoint(canvas);
			if (point.Properties.IsHorizontalMouseWheel) return;
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

		private void ResetView()
		{
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
