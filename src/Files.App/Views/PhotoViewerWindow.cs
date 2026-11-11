// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinRT;

namespace Files.App.Views
{
	public sealed partial class PhotoViewerWindow : WindowEx
	{
		private readonly string[] paths;
		private readonly Grid canvas = new();
		private readonly UserControl host = new() { IsTabStop = true };
		private readonly Image image = new() { Stretch = Stretch.Fill, RenderTransformOrigin = new(0.5, 0.5) };
		private readonly TextBlock status = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new(24) };
		private readonly CompositeTransform transform = new();
		private readonly SemaphoreSlim decoderGate = new(1);
		private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer resizeTimer;
		private readonly Button previous;
		private readonly Button next;
		private CancellationTokenSource? loadCancellation;
		private int index;
		private int rotation;
		private bool flipped;
		private bool closed;
		private bool exporting;
		private uint pixelWidth;
		private uint pixelHeight;
		private double decodedScale;
		private bool initialSize = true;

		internal static bool CanOpen(string path) => Path.IsPathFullyQualified(path) && FileExtensionHelpers.IsImageFile(Path.GetExtension(path));

		internal static void Open(string path, IEnumerable<string> folderOrder)
		{
			var paths = folderOrder.Where(CanOpen).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			var index = Array.FindIndex(paths, item => StringComparer.OrdinalIgnoreCase.Equals(item, path));
			if (index < 0)
			{
				paths = [path];
				index = 0;
			}
			var window = new PhotoViewerWindow(paths, index);
			window.Activate();
			window.host.Focus(FocusState.Programmatic);
			window.StartLoad();
		}

		[DynamicWindowsRuntimeCast(typeof(OverlappedPresenter))]
		private PhotoViewerWindow(string[] paths, int index) : base(120, 90)
		{
			this.paths = paths;
			this.index = index;
			if (AppWindow.Presenter is OverlappedPresenter presenter)
			{
				presenter.SetBorderAndTitleBar(false, false);
				presenter.IsResizable = true;
			}
			AppWindow.Resize(new(800, 600));
			canvas.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
			status.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
			image.RenderTransform = transform;
			canvas.Children.Add(image);
			canvas.Children.Add(status);
			previous = NavigationButton(Symbol.Back, Strings.PhotoViewerPrevious.GetLocalizedResource(), HorizontalAlignment.Left, -1);
			next = NavigationButton(Symbol.Forward, Strings.PhotoViewerNext.GetLocalizedResource(), HorizontalAlignment.Right, 1);
			canvas.Children.Add(previous);
			canvas.Children.Add(next);
			host.Content = canvas;
			Content = host;
			host.KeyDown += OnKeyDown;
			canvas.PointerPressed += OnPointerPressed;
			canvas.PointerEntered += (_, _) => { previous.Opacity = next.Opacity = 1; };
			canvas.PointerExited += (_, _) => { previous.Opacity = next.Opacity = 0; };
			canvas.ContextFlyout = CreateMenu();
			resizeTimer = DispatcherQueue.CreateTimer();
			resizeTimer.Interval = TimeSpan.FromMilliseconds(180);
			resizeTimer.IsRepeating = false;
			resizeTimer.Tick += (_, _) => { if (!closed && DesiredScale() > decodedScale * 1.1) StartLoad(false); };
			canvas.SizeChanged += (_, _) => { FitImage(); resizeTimer.Stop(); resizeTimer.Start(); };
			Closed += (_, _) =>
			{
				closed = true;
				loadCancellation?.Cancel();
				resizeTimer.Stop();
				image.Source = null;
				Dispose();
			};
		}

		private Button NavigationButton(Symbol symbol, string label, HorizontalAlignment alignment, int direction)
		{
			var button = new Button { Content = new SymbolIcon(symbol), HorizontalAlignment = alignment, VerticalAlignment = VerticalAlignment.Center, Margin = new(12), Opacity = 0 };
			ToolTipService.SetToolTip(button, label);
			Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
			button.Click += (_, _) => Navigate(direction);
			return button;
		}

		private MenuFlyout CreateMenu()
		{
			var menu = new MenuFlyout();
			void Add(string label, Action action)
			{
				var item = new MenuFlyoutItem { Text = label };
				item.Click += (_, _) => action();
				menu.Items.Add(item);
			}
			Add(Strings.Copy.GetLocalizedResource(), () => _ = ExportAsync(false));
			Add(Strings.PhotoViewerSaveAs.GetLocalizedResource(), () => _ = ExportAsync(true));
			menu.Items.Add(new MenuFlyoutSeparator());
			Add(Strings.RotateRight.GetLocalizedResource(), () => { rotation = (rotation + 1) % 4; FitImage(); });
			Add(Strings.PhotoViewerFlipHorizontal.GetLocalizedResource(), () => Flip(false));
			Add(Strings.PhotoViewerFlipVertical.GetLocalizedResource(), () => Flip(true));
			menu.Items.Add(new MenuFlyoutSeparator());
			Add(Strings.OpenWith.GetLocalizedResource(), () => _ = OpenWithAsync());
			Add(Strings.Close.GetLocalizedResource(), Close);
			return menu;
		}

		private void Flip(bool vertical)
		{
			// Represent every orientation as a horizontal flip followed by a rotation.
			if (vertical != (rotation % 2 == 1))
				rotation = (rotation + 2) % 4;
			flipped = !flipped;
			FitImage();
		}

		private void Navigate(int direction)
		{
			if (exporting || index + direction < 0 || index + direction >= paths.Length)
				return;
			index += direction;
			rotation = 0;
			flipped = false;
			pixelWidth = pixelHeight = 0;
			StartLoad();
		}

		private void OnKeyDown(object sender, KeyRoutedEventArgs e)
		{
			switch (e.Key)
			{
				case VirtualKey.Left: Navigate(-1); break;
				case VirtualKey.Right: Navigate(1); break;
				case VirtualKey.Escape: Close(); break;
				default: return;
			}
			e.Handled = true;
		}

		private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
		{
			var properties = e.GetCurrentPoint(canvas).Properties;
			if (properties.IsXButton1Pressed || properties.IsXButton2Pressed)
			{
				Navigate(properties.IsXButton1Pressed ? -1 : 1);
				e.Handled = true;
			}
			else if (properties.IsLeftButtonPressed && e.OriginalSource is not Button && e.OriginalSource is not SymbolIcon)
			{
				PInvoke.ReleaseCapture();
				PInvoke.SendMessage(new(WindowHandle), 0x00A1, new WPARAM(2), default);
			}
		}

		protected override bool OnWindowMessageReceived(uint message, WPARAM wParam, LPARAM lParam, ref LRESULT result)
		{
			if (message == 0x0084 && PInvoke.GetWindowRect(new(WindowHandle), out var bounds))
			{
				var x = (short)((long)lParam.Value & 0xffff);
				var y = (short)(((long)lParam.Value >> 16) & 0xffff);
				var edge = Math.Max(4, (int)(6 * PInvoke.GetDpiForWindow(new(WindowHandle)) / 96));
				var left = x < bounds.left + edge;
				var right = x >= bounds.right - edge;
				var top = y < bounds.top + edge;
				var bottom = y >= bounds.bottom - edge;
				var hit = top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 0;
				if (hit != 0)
				{
					result = new(hit);
					return true;
				}
			}
			return false;
		}

		private double DesiredScale()
		{
			if (pixelWidth == 0 || pixelHeight == 0)
				return 1;
			var scale = canvas.XamlRoot?.RasterizationScale ?? 1;
			var width = rotation % 2 == 0 ? pixelWidth : pixelHeight;
			var height = rotation % 2 == 0 ? pixelHeight : pixelWidth;
			return Math.Min(1, Math.Min(Math.Max(1, canvas.ActualWidth) * scale / width, Math.Max(1, canvas.ActualHeight) * scale / height));
		}

		private void FitImage()
		{
			if (pixelWidth == 0 || pixelHeight == 0)
				return;
			var width = rotation % 2 == 0 ? pixelWidth : pixelHeight;
			var height = rotation % 2 == 0 ? pixelHeight : pixelWidth;
			var scale = Math.Min(canvas.ActualWidth / width, canvas.ActualHeight / height);
			image.Width = pixelWidth * scale;
			image.Height = pixelHeight * scale;
			image.HorizontalAlignment = HorizontalAlignment.Center;
			image.VerticalAlignment = VerticalAlignment.Center;
			transform.Rotation = rotation * 90;
			transform.ScaleX = flipped ? -1 : 1;
			previous.IsEnabled = index > 0;
			next.IsEnabled = index + 1 < paths.Length;
		}

		private void StartLoad(bool clear = true)
		{
			loadCancellation?.Cancel();
			var cancellation = new CancellationTokenSource();
			loadCancellation = cancellation;
			if (clear)
			{
				image.Source = null;
				decodedScale = 0;
				status.Text = string.Empty;
			}
			_ = LoadAsync(paths[index], cancellation);
		}

		private async Task LoadAsync(string path, CancellationTokenSource cancellation)
		{
			var token = cancellation.Token;
			var entered = false;
			try
			{
				await decoderGate.WaitAsync(token);
				entered = true;
				var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
				using var stream = await file.OpenReadAsync().AsTask(token);
				var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
				token.ThrowIfCancellationRequested();
				pixelWidth = decoder.OrientedPixelWidth;
				pixelHeight = decoder.OrientedPixelHeight;
				if (initialSize)
				{
					initialSize = false;
					var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
					var fit = Math.Min(1, Math.Min(area.Width * 0.9 / pixelWidth, area.Height * 0.9 / pixelHeight));
					var width = Math.Max(150, (int)(pixelWidth * fit));
					var height = Math.Max(112, (int)(pixelHeight * fit));
					AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
				}
				FitImage();
				var scale = DesiredScale();
				var bitmapTransform = new BitmapTransform
				{
					ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale)),
					ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale)),
					InterpolationMode = BitmapInterpolationMode.Fant
				};
				using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, bitmapTransform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(token);
				var source = new SoftwareBitmapSource();
				await source.SetBitmapAsync(bitmap).AsTask(token);
				token.ThrowIfCancellationRequested();
				image.Source = source;
				decodedScale = scale;
				Title = Path.GetFileName(path);
				status.Text = string.Empty;
				var sampleScale = Math.Min(1, 32d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
				bitmapTransform.ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * sampleScale));
				bitmapTransform.ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * sampleScale));
				using var sample = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, bitmapTransform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(token);
				var pixels = new byte[sample.PixelWidth * sample.PixelHeight * 4];
				sample.CopyToBuffer(pixels.AsBuffer());
				long red = 0, green = 0, blue = 0, alpha = 0;
				for (var i = 0; i < pixels.Length; i += 4)
				{
					blue += pixels[i]; green += pixels[i + 1]; red += pixels[i + 2]; alpha += pixels[i + 3];
				}
				token.ThrowIfCancellationRequested();
				canvas.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(red * 255 / Math.Max(1, alpha)), (byte)(green * 255 / Math.Max(1, alpha)), (byte)(blue * 255 / Math.Max(1, alpha))));
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				if (!token.IsCancellationRequested && !closed)
					status.Text = Strings.PhotoViewerLoadError.GetLocalizedResource();
				Debug.WriteLine(ex);
			}
			finally
			{
				if (entered) decoderGate.Release();
				if (ReferenceEquals(loadCancellation, cancellation)) loadCancellation = null;
				cancellation.Dispose();
			}
		}

		private async Task OpenWithAsync()
		{
			try
			{
				var file = await StorageFile.GetFileFromPathAsync(paths[index]);
				var options = new LauncherOptions { DisplayApplicationPicker = true };
				WinRT.Interop.InitializeWithWindow.Initialize(options, WindowHandle);
				await Launcher.LaunchFileAsync(file, options);
			}
			catch (Exception ex) { Debug.WriteLine(ex); if (!closed) status.Text = Strings.PhotoViewerLoadError.GetLocalizedResource(); }
		}

		private async Task ExportAsync(bool save)
		{
			if (exporting || image.Source is null || closed)
				return;
			exporting = true;
			var path = paths[index];
			var exportRotation = rotation;
			var exportFlip = flipped;
			try
			{
				SoftwareBitmap bitmap;
				var file = await StorageFile.GetFileFromPathAsync(path);
				using (var input = await file.OpenReadAsync())
				{
					var decoder = await BitmapDecoder.CreateAsync(input);
					bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
				}
				using var ownedBitmap = bitmap;
				if (closed) return;
				StorageFile? output = null;
				if (save)
				{
					var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(path) };
					picker.FileTypeChoices.Add("PNG", [".png"]);
					picker.FileTypeChoices.Add("JPEG", [".jpg"]);
					WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
					output = await picker.PickSaveFileAsync();
					if (output is null || closed) return;
				}
				using var encoded = new InMemoryRandomAccessStream();
				var encoder = await BitmapEncoder.CreateAsync(output?.FileType.Equals(".jpg", StringComparison.OrdinalIgnoreCase) == true ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId, encoded);
				encoder.SetSoftwareBitmap(bitmap);
				encoder.BitmapTransform.Rotation = (BitmapRotation)exportRotation;
				encoder.BitmapTransform.Flip = exportFlip ? BitmapFlip.Horizontal : BitmapFlip.None;
				await encoder.FlushAsync();
				if (closed) return;
				encoded.Seek(0);
				if (output is not null)
				{
					using var destination = await output.OpenAsync(FileAccessMode.ReadWrite);
					destination.Size = 0;
					await RandomAccessStream.CopyAsync(encoded, destination);
					await destination.FlushAsync();
				}
				else
				{
					var data = new DataPackage();
					data.SetBitmap(RandomAccessStreamReference.CreateFromStream(encoded));
					Clipboard.SetContent(data);
					Clipboard.Flush();
				}
				status.Text = string.Empty;
			}
			catch (Exception ex) { Debug.WriteLine(ex); if (!closed) status.Text = Strings.PhotoViewerExportError.GetLocalizedResource(); }
			finally { exporting = false; }
		}
	}
}
