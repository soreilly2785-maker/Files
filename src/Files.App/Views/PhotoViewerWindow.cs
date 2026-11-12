// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Files.Shared.Helpers;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using Microsoft.UI.Input;
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
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT;

namespace Files.App.Views
{
	public sealed partial class PhotoViewerWindow : WindowEx
	{
		private string[] paths;
		private readonly Grid canvas = new();
		private readonly UserControl host = new() { IsTabStop = true, UseSystemFocusVisuals = false };
		private readonly Image image = new() { Stretch = Stretch.Fill, RenderTransformOrigin = new(0.5, 0.5) };
		private readonly TextBlock status = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new(24) };
		private readonly CompositeTransform transform = new();
		private readonly SemaphoreSlim decoderGate = new(1);
		private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer resizeTimer;
		private readonly Button previous;
		private readonly Button next;
		private readonly StackPanel windowControls = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new(8), Opacity = 0 };
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
		private bool dragRegionUpdatePending;

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
			// Finish Files' double-click focus handling before showing the viewer.
			MainWindow.Instance.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
			{
				var window = new PhotoViewerWindow(paths, index);
				window.Activate();
				window.BringOpeningToFront();
				window.openingFocusTimer.Start();
				window.StartLoad();
			});
		}

		[DynamicWindowsRuntimeCast(typeof(OverlappedPresenter))]
		private PhotoViewerWindow(string[] paths, int index) : base(200, 90)
		{
			this.paths = paths;
			this.index = index;
			ExtendsContentIntoTitleBar = true;
			if (AppWindow.Presenter is OverlappedPresenter presenter)
			{
				presenter.SetBorderAndTitleBar(false, false);
				presenter.IsResizable = true;
			}
			InitializePhotoTaskbar();
			RemoveFrame();
			AppWindow.Move(MainWindow.Instance.AppWindow.Position);
			AppWindow.ResizeClient(new(800, 600));
			canvas.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
			status.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
			image.RenderTransform = transform;
			canvas.Children.Add(image);
			canvas.Children.Add(status);
			previous = NavigationButton(Symbol.Back, Strings.PhotoViewerPrevious.GetLocalizedResource(), HorizontalAlignment.Left, -1);
			next = NavigationButton(Symbol.Forward, Strings.PhotoViewerNext.GetLocalizedResource(), HorizontalAlignment.Right, 1);
			canvas.Children.Add(previous);
			canvas.Children.Add(next);
			AddWindowControl(Symbol.Remove, Strings.PhotoViewerMinimize.GetLocalizedResource(), Minimize);
			AddWindowControl(Symbol.FullScreen, Strings.PhotoViewerMaximizeRestore.GetLocalizedResource(), ToggleMaximize);
			AddWindowControl(Symbol.BackToWindow, Strings.PhotoViewerHalfScreen.GetLocalizedResource(), ResizeToHalfScreen);
			AddWindowControl(Symbol.Cancel, Strings.Close.GetLocalizedResource(), Close);
			canvas.Children.Add(windowControls);
			host.Content = canvas;
			Content = host;
			host.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
			InitializeImageInteraction();
			canvas.PointerPressed += OnPointerPressed;
			canvas.PointerMoved += (_, e) =>
			{
				var point = e.GetCurrentPoint(canvas).Position;
				UpdateNavigationHover(point);
				windowControls.Opacity = point.Y <= 56 && point.X >= canvas.ActualWidth - windowControls.ActualWidth - 16 ? 1 : 0;
			};
			canvas.PointerExited += (_, e) =>
			{
				var point = e.GetCurrentPoint(canvas).Position;
				if (point.X < 0 || point.Y < 0 || point.X >= canvas.ActualWidth || point.Y >= canvas.ActualHeight)
				{
					HideNavigation();
					windowControls.Opacity = 0;
				}
				else if (point.Y > 56 || point.X < canvas.ActualWidth - windowControls.ActualWidth - 16)
					windowControls.Opacity = 0;
			};
			canvas.ContextFlyout = CreateMenu();
			resizeTimer = DispatcherQueue.CreateTimer();
			openingFocusTimer = DispatcherQueue.CreateTimer();
			openingFocusTimer.Interval = TimeSpan.FromMilliseconds(200);
			openingFocusTimer.IsRepeating = false;
			openingFocusTimer.Tick += (_, _) => BringOpeningToFront();
			navigationHoverTimer = DispatcherQueue.CreateTimer();
			navigationHoverTimer.Interval = TimeSpan.FromMilliseconds(900);
			navigationHoverTimer.IsRepeating = false;
			navigationHoverTimer.Tick += (_, _) => HideNavigation();
			resizeTimer.Interval = TimeSpan.FromMilliseconds(180);
			resizeTimer.IsRepeating = false;
			resizeTimer.Tick += (_, _) =>
			{
				var desired = DesiredScale();
				if (!closed && HasCurrentPhoto && (desired > decodedScale * 1.1 || desired < decodedScale * 0.7)) StartLoad(false);
			};
			canvas.SizeChanged += (_, _) => { UpdateViewport(); FitImage(); RefreshWindowSetViews(); resizeTimer.Stop(); resizeTimer.Start(); UpdateDragRegions(); };
			windowControls.SizeChanged += (_, _) => UpdateDragRegions();
			host.Loaded += (_, _) => UpdateDragRegions();
			Closed += (_, _) =>
			{
				closed = true;
				loadCancellation?.Cancel();
				resizeTimer.Stop();
				openingFocusTimer.Stop();
				navigationHoverTimer.Stop();
				cacheLifetime.Cancel();
				ClearImage();
				foreach (var photo in photoCache.Values) photo.Dispose();
				photoCache.Clear();
				openWindows.Remove(this);
				RefreshWindowSetsPanel();
				RemoveFromWindowSets(this);
				if (lastActiveWindow == this) lastActiveWindow = openWindows.LastOrDefault();
				DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _ = Ioc.Default.GetRequiredService<IWindowsJumpListService>().RefreshPhotoViewerTasksAsync());
				Dispose();
			};
			openWindows.Add(this);
			RefreshWindowSetsPanel();
			RefreshPhotoTaskbar();
			Activated += (_, e) => { if (e.WindowActivationState != WindowActivationState.Deactivated) { lastActiveWindow = this; RefreshPhotoTaskbar(); } };
			_ = Ioc.Default.GetRequiredService<IWindowsJumpListService>().RefreshPhotoViewerTasksAsync();
		}

		private void AddWindowControl(Symbol symbol, string label, Action action)
		{
			var button = new Button { Content = new SymbolIcon(symbol) };
			ToolTipService.SetToolTip(button, label);
			Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
			button.Click += (_, _) => action();
			windowControls.Children.Add(button);
		}

		private unsafe void RemoveFrame()
		{
			uint noBorder = 0xfffffffe;
			PInvoke.DwmSetWindowAttribute(new(WindowHandle), DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR, &noBorder, sizeof(uint));
			RemoveExtendedFrame();
			PInvoke.SetWindowPos(new(WindowHandle), default, 0, 0, 0, 0,
				SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
		}

		private void RemoveExtendedFrame()
		{
			PInvoke.DwmExtendFrameIntoClientArea(new(WindowHandle), new Windows.Win32.UI.Controls.MARGINS());
			AppWindow.TitleBar.BackgroundColor = Microsoft.UI.Colors.Transparent;
			AppWindow.TitleBar.InactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
		}

		private void UpdateDragRegions()
		{
			if (dragRegionUpdatePending || closed) return;
			dragRegionUpdatePending = true;
			if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
			{
				dragRegionUpdatePending = false;
				if (closed || canvas.XamlRoot is not { } root) return;
				var scale = root.RasterizationScale;
				var width = AppWindow.ClientSize.Width;
				var frameHeight = AppWindow.ClientSize.Height;
				if (width <= 0 || frameHeight <= 0) return;
				var edge = (int)Math.Ceiling(6 * scale);
				var height = (int)Math.Ceiling(40 * scale);
				var controlsWidth = (int)Math.Ceiling((windowControls.ActualWidth + 16) * scale);
				var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
				RemoveExtendedFrame();
				source.SetRegionRects(NonClientRegionKind.LeftBorder, [new(0, 0, edge, frameHeight)]);
				source.SetRegionRects(NonClientRegionKind.RightBorder, [new(Math.Max(0, width - edge), 0, edge, frameHeight)]);
				source.SetRegionRects(NonClientRegionKind.TopBorder, [new(0, 0, width, edge)]);
				source.SetRegionRects(NonClientRegionKind.BottomBorder, [new(0, Math.Max(0, frameHeight - edge), width, edge)]);
				source.SetRegionRects(NonClientRegionKind.Caption, [new(edge, edge, Math.Max(1, width - edge * 2), Math.Max(1, height - edge))]);
				var controlsLeft = Math.Max(edge, width - controlsWidth);
				source.SetRegionRects(NonClientRegionKind.Passthrough, [new(controlsLeft, edge, Math.Max(1, width - edge - controlsLeft), Math.Max(1, height - edge))]);
			})) dragRegionUpdatePending = false;
		}

		private Button NavigationButton(Symbol symbol, string label, HorizontalAlignment alignment, int direction)
		{
			var button = new Button { Content = new SymbolIcon(symbol), HorizontalAlignment = alignment, VerticalAlignment = VerticalAlignment.Center, Margin = new(12), Opacity = 0, IsHitTestVisible = false };
			ToolTipService.SetToolTip(button, label);
			Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
			button.Click += (_, _) => Navigate(direction);
			return button;
		}

		private static IconElement ArrangementIcon(string command) => new BitmapIcon
		{
			UriSource = new($"ms-appx:///Assets/PhotoLayouts/{command}.png"),
			ShowAsMonochrome = true
		};

		private MenuFlyout CreateMenu()
		{
			var menu = new MenuFlyout();
			void Add(string label, Action action, IconElement? icon = null)
			{
				var item = new MenuFlyoutItem { Text = label, Icon = icon };
				item.Click += (_, _) => action();
				menu.Items.Add(item);
			}
			Add(Strings.PhotoViewerWindowSets.GetLocalizedResource(), () => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ShowWindowSets), new SymbolIcon(Symbol.Pictures));
			var arrange = new MenuFlyoutSubItem { Text = Strings.PhotoViewerArrange.GetLocalizedResource(), Icon = new SymbolIcon(Symbol.ViewAll) };
			foreach (var (command, label) in GetArrangementCommands().Where(item => item.Command != "close"))
			{
				var item = new MenuFlyoutItem { Text = label, Icon = ArrangementIcon(command) };
				item.Click += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Arrange(command));
				arrange.Items.Add(item);
			}
			menu.Items.Add(arrange);
			Add(Strings.PhotoViewerHalfScreen.GetLocalizedResource(), ResizeToHalfScreen, new SymbolIcon(Symbol.BackToWindow));
			menu.Items.Add(new MenuFlyoutSeparator());
			Add(Strings.PhotoViewerFillWindow.GetLocalizedResource(), FillWindow, new SymbolIcon(Symbol.FullScreen));
			Add(Strings.PhotoViewerFillAll.GetLocalizedResource(), () => Arrange("fill"), new SymbolIcon(Symbol.FullScreen));
			Add(Strings.PhotoViewerResetView.GetLocalizedResource(), ResetView);
			Add(Strings.PhotoViewerFitAll.GetLocalizedResource(), () => Arrange("fit"));
			menu.Items.Add(new MenuFlyoutSeparator());
			Add(Strings.Copy.GetLocalizedResource(), () => _ = ExportAsync(false), new SymbolIcon(Symbol.Copy));
			Add(Strings.PhotoViewerSaveAs.GetLocalizedResource(), () => _ = ExportAsync(true), new SymbolIcon(Symbol.Save));
			Add(Strings.PhotoViewerEnlargeScreen.GetLocalizedResource(), () => _ = ExportAsync(true, true));
			menu.Items.Add(new MenuFlyoutSeparator());
			Add(Strings.RotateRight.GetLocalizedResource(), () => { presentationRevision++; rotation = (rotation + 1) % 4; FitImage(); resizeTimer.Start(); RefreshWindowSetsPanel(); }, new SymbolIcon(Symbol.Rotate));
			Add(Strings.PhotoViewerFlipHorizontal.GetLocalizedResource(), () => Flip(false));
			Add(Strings.PhotoViewerFlipVertical.GetLocalizedResource(), () => Flip(true));
			menu.Items.Add(new MenuFlyoutSeparator());
			Add(Strings.OpenWith.GetLocalizedResource(), () => _ = OpenWithAsync());
			menu.Items.Add(new MenuFlyoutSeparator());
			Add(Strings.Close.GetLocalizedResource(), Close, new SymbolIcon(Symbol.Cancel));
			Add(Strings.PhotoViewerCloseAll.GetLocalizedResource(), () => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, CloseAllPhotos));
			return menu;
		}

		private void Flip(bool vertical)
		{
			presentationRevision++;
			// Represent every orientation as a horizontal flip followed by a rotation.
			if (vertical != (rotation % 2 == 1))
				rotation = (rotation + 2) % 4;
			flipped = !flipped;
			FitImage();
			RefreshWindowSetsPanel();
		}

		private void Navigate(int direction)
		{
			if (exporting || deleting || paths.Length < 2)
				return;
			index = (index + direction + paths.Length) % paths.Length;
			TrimPhotoCache();
			ResetImageTransform(false);
			resizeTimer.Stop();
			rotation = 0;
			flipped = false;
			StartLoad();
		}

		private void OnKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (deleting || (e.Key == VirtualKey.Escape && e.Handled)) return;
			if (TryCycleSetKey(e)) return;
			switch (e.Key)
			{
				case VirtualKey.Delete:
					if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) ||
						Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
					var permanently = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
					var deletePath = paths[index];
					DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _ = DeletePhotoAsync(deletePath, permanently));
					break;
				case VirtualKey.Left: Navigate(-1); break;
				case VirtualKey.Right: Navigate(1); break;
				case VirtualKey.Escape:
					if (fillWindow || zoom != 1 || panX != 0 || panY != 0) ResetView();
					else if (IsLargerThanHalfScreen()) ResizeToHalfScreen();
					else Close();
					break;
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
		}

		protected override unsafe bool OnWindowMessageReceived(uint message, WPARAM wParam, LPARAM lParam, ref LRESULT result)
		{
			if (message == 0x0002) ClearPhotoTaskbar(); // WM_DESTROY
			if (message == 0x0083 && wParam.Value != 0) // WM_NCCALCSIZE
			{
				if (PInvoke.IsZoomed(new(WindowHandle)))
				{
					MONITORINFO info = new() { cbSize = (uint)sizeof(MONITORINFO) };
					if (PInvoke.GetMonitorInfo(PInvoke.MonitorFromWindow(new(WindowHandle), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST), &info))
					{
						var parameters = (NCCALCSIZE_PARAMS*)lParam.Value;
						// The first rectangle is the proposed client area.
						*(RECT*)&parameters->rgrc = info.rcWork;
					}
				}
				result = default;
				return true;
			}
			if (message == 0x0084 && !PInvoke.IsZoomed(new(WindowHandle)) && PInvoke.GetWindowRect(new(WindowHandle), out var bounds))
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
			var budget = Math.Min(1, Math.Sqrt(MaxDecodedPixels / (pixelWidth * (double)pixelHeight)));
			return Math.Min(budget, EffectiveZoom() * Math.Min(Math.Max(1, canvas.ActualWidth) * scale / width, Math.Max(1, canvas.ActualHeight) * scale / height));
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
			var scaleZoom = EffectiveZoom();
			transform.ScaleX = flipped ? -scaleZoom : scaleZoom;
			transform.ScaleY = scaleZoom;
			transform.TranslateX = panX;
			transform.TranslateY = panY;
			previous.IsEnabled = next.IsEnabled = paths.Length > 1;
		}

		private void StartLoad(bool clear = true)
		{
			loadCancellation?.Cancel();
			var cancellation = new CancellationTokenSource();
			loadCancellation = cancellation;
			if (clear)
			{
				status.Text = string.Empty;
			}
			var path = paths[index];
			if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
			{
				if (closed || cancellation.IsCancellationRequested)
				{
					if (ReferenceEquals(loadCancellation, cancellation)) loadCancellation = null;
					cancellation.Dispose();
					return;
				}
				_ = LoadAsync(path, cancellation);
			}))
			{
				loadCancellation = null;
				cancellation.Dispose();
			}
		}

		private void ClearImage()
		{
			image.Source = null;
			if (displayedPhoto is { CacheQuality: false } photo) photo.Dispose();
			displayedPhoto = null;
		}

		private async Task LoadAsync(string path, CancellationTokenSource cancellation)
		{
			var token = cancellation.Token;
			var entered = false;
			PreparedPhoto? prepared = null;
			try
			{
				if (!HasCurrentPhoto)
				{
					if (!photoCache.ContainsKey(path) && pendingPhotos.TryGetValue(path, out var pending))
						await pending.Task.WaitAsync(token);
					token.ThrowIfCancellationRequested();
					if (photoCache.TryGetValue(path, out var cached))
					{
						ShowPreparedPhoto(cached);
						PrefetchNearbyPhotos();
						if (DesiredScale() <= decodedScale * 1.1) return;
					}
				}
				await decoderGate.WaitAsync(token);
				entered = true;
				prepared = await DecodePhotoAsync(path, !HasCurrentPhoto, token);
				token.ThrowIfCancellationRequested();
				ShowPreparedPhoto(prepared);
				prepared = null;
				PrefetchNearbyPhotos();
				if (DesiredScale() > decodedScale * 1.1)
				{
					prepared = await DecodePhotoAsync(path, false, token);
					token.ThrowIfCancellationRequested();
					ShowPreparedPhoto(prepared);
					prepared = null;
				}
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				if (!token.IsCancellationRequested && !closed) status.Text = Strings.PhotoViewerLoadError.GetLocalizedResource();
				Debug.WriteLine(ex);
			}
			finally
			{
				prepared?.Dispose();
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

		private async Task ExportAsync(bool save, bool enlargeForScreen = false)
		{
			if (exporting || deleting || !HasCurrentPhoto || closed)
				return;
			exporting = true;
			var path = paths[index];
			var exportRotation = rotation;
			var exportFlip = flipped;
			var screen = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
			try
			{
				if (enlargeForScreen && pixelWidth != 0 && pixelHeight != 0 &&
					Math.Min((double)screen.Width / (exportRotation % 2 == 0 ? pixelWidth : pixelHeight),
						(double)screen.Height / (exportRotation % 2 == 0 ? pixelHeight : pixelWidth)) <= 1)
				{
					status.Text = Strings.PhotoViewerAlreadyScreenSize.GetLocalizedResource();
					return;
				}
				SoftwareBitmap bitmap;
				var file = await StorageFile.GetFileFromPathAsync(path);
				using (var input = await file.OpenReadAsync())
				{
					var decoder = await BitmapDecoder.CreateAsync(input);
					bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
				}
				using var ownedBitmap = bitmap;
				if (closed) return;
				var enlarge = enlargeForScreen ? Math.Min((double)screen.Width / (exportRotation % 2 == 0 ? bitmap.PixelWidth : bitmap.PixelHeight),
					(double)screen.Height / (exportRotation % 2 == 0 ? bitmap.PixelHeight : bitmap.PixelWidth)) : 1;
				if (enlargeForScreen && enlarge <= 1)
				{
					status.Text = Strings.PhotoViewerAlreadyScreenSize.GetLocalizedResource();
					return;
				}
				StorageFile? output = null;
				if (save)
				{
					var name = Path.GetFileNameWithoutExtension(path);
					var picker = new FileSavePicker { SuggestedFileName = enlargeForScreen ? string.Format(Strings.PhotoViewerScreenFileName.GetLocalizedResource(), name) : name };
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
				if (enlargeForScreen)
				{
					encoder.BitmapTransform.ScaledWidth = (uint)Math.Floor(bitmap.PixelWidth * enlarge);
					encoder.BitmapTransform.ScaledHeight = (uint)Math.Floor(bitmap.PixelHeight * enlarge);
					encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Cubic;
				}
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
