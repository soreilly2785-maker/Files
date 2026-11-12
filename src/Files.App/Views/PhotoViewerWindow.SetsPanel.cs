// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.IO;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using WinRT;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace Files.App.Views
{
	public sealed partial class PhotoViewerWindow
	{
		private sealed partial class WindowSetsWindow : WindowEx
		{
			private const string DragFormat = "Files.PhotoWindow";
			private readonly Grid cards = new() { ColumnSpacing = 10, RowSpacing = 10 };
			private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
			private readonly ToggleButton pin = new() { Content = Strings.PhotoViewerPinSets.GetLocalizedResource() };
			private readonly List<Button> buttons = [];
			private readonly List<Action> updatePreviews = [];
			private bool viewRefreshPending;
			private PhotoViewerWindow? draggingPhoto;
			private bool active;
			private bool closed;
			private bool refreshPending;

			[DynamicWindowsRuntimeCast(typeof(OverlappedPresenter))]
			internal WindowSetsWindow(PhotoViewerWindow anchor) : base(320, 260)
			{
				Title = Strings.PhotoViewerWindowSets.GetLocalizedResource();
				AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "PhotoViewer.ico"));
				SystemBackdrop = new AppSystemBackdrop(true);
				var root = new Grid { Padding = new(12), RowSpacing = 10 };
				root.RowDefinitions.Add(new() { Height = GridLength.Auto });
				root.RowDefinitions.Add(new() { Height = GridLength.Auto });
				root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
				root.RowDefinitions.Add(new() { Height = GridLength.Auto });
				root.Children.Add(pin);
				var hint = new TextBlock { Text = Strings.PhotoViewerSetsDragHint.GetLocalizedResource(), TextWrapping = TextWrapping.Wrap };
				Grid.SetRow(hint, 1);
				root.Children.Add(hint);
				scroll.Content = cards;
				Grid.SetRow(scroll, 2);
				root.Children.Add(scroll);
				var newSet = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Padding = new(12), CornerRadius = new(6), BorderThickness = new(1), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray), Child = new TextBlock { Text = Strings.PhotoViewerNewSetDrop.GetLocalizedResource(), HorizontalAlignment = HorizontalAlignment.Center } };
				ConfigureDrop(newSet, null, null);
				Grid.SetRow(newSet, 3);
				root.Children.Add(newSet);
				Content = root;
				root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => { TryCycleSetKey(e); }), true);
				pin.Checked += (_, _) => { if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = true; };
				pin.Unchecked += (_, _) => { if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = false; };
				scroll.SizeChanged += (_, _) => LayoutCards();
				Activated += (_, e) =>
				{
					active = e.WindowActivationState != WindowActivationState.Deactivated;
					if (!active) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { if (!closed && !active && pin.IsChecked != true && draggingPhoto is null) Close(); });
				};
				Closed += (_, _) => { closed = true; setsPanel = null; Dispose(); };
				Rebuild();
				var sets = GetWindowSets();
				var scale = anchor.canvas.XamlRoot?.RasterizationScale ?? 1;
				var area = DisplayArea.GetFromWindowId(anchor.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
				var columns = sets.Length <= 1 ? 1 : 2;
				var width = Math.Min(area.Width * 0.8, (sets.Length <= 1 ? 580 : 960) * scale);
				var rows = (sets.Length + columns - 1) / columns;
				var height = Math.Min(area.Height * 0.8, (160 + rows * 240) * scale);
				AppWindow.MoveAndResize(new(area.X + (area.Width - (int)width) / 2, area.Y + (area.Height - (int)height) / 2, (int)width, (int)height));
			}

			internal void CloseUnlessPinned()
			{
				if (!closed && pin.IsChecked != true) Close();
			}

			internal void QueueViewRefresh()
			{
				if (closed || viewRefreshPending || refreshPending) return;
				viewRefreshPending = true;
				DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
				{
					viewRefreshPending = false;
					if (!closed && !refreshPending) foreach (var update in updatePreviews) update();
				});
			}

			internal void QueueRefresh()
			{
				if (closed || refreshPending) return;
				refreshPending = true;
				DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
				{
					refreshPending = false;
					if (closed) return;
					if (openWindows.Count == 0 && pin.IsChecked != true) Close();
					else if (draggingPhoto is null) Rebuild();
				});
			}

			private void Rebuild()
			{
				cards.Children.Clear();
				buttons.Clear();
				updatePreviews.Clear();
				var sets = GetWindowSets();
				if (sets.Length == 0) cards.Children.Add(new TextBlock { Text = Strings.PhotoViewerNoSets.GetLocalizedResource(), TextWrapping = TextWrapping.Wrap });
				for (var index = 0; index < sets.Length; index++)
				{
					var set = sets[index];
					var windows = set.Windows.Where(window => !window.closed).ToArray();
					var content = new Grid { RowSpacing = 6 };
					content.RowDefinitions.Add(new() { Height = GridLength.Auto });
					content.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
					content.Children.Add(new TextBlock { Text = string.Format((windows.Length == 1 ? Strings.PhotoViewerSetLabelSingle : Strings.PhotoViewerSetLabel).GetLocalizedResource(), index + 1, windows.Length) });
					var previewHost = new Grid();
					var previewGrid = new Canvas { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
					previewHost.Children.Add(previewGrid);
					previewHost.SizeChanged += (_, _) =>
					{
						previewGrid.Width = Math.Min(previewHost.ActualWidth, previewHost.ActualHeight * set.FrameAspect);
						previewGrid.Height = previewGrid.Width / set.FrameAspect;
					};
					var cells = new List<Grid>();
					for (var i = 0; i < windows.Length; i++)
					{
						var window = windows[i];
						var previewTransform = new CompositeTransform();
						var preview = new Image { Stretch = Stretch.Fill, RenderTransformOrigin = new(0.5, 0.5), RenderTransform = previewTransform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
						var clip = new RectangleGeometry();
						var viewport = new Grid { Clip = clip, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
						viewport.Children.Add(preview);
						var cell = new Grid { Background = window.canvas.Background, CanDrag = true };
						cell.Children.Add(viewport);
						void UpdatePreview()
						{
							if (window.closed || !double.IsFinite(window.image.Width) || !double.IsFinite(window.image.Height)) return;
							var scale = Math.Min(cell.ActualWidth / Math.Max(1, window.canvas.ActualWidth), cell.ActualHeight / Math.Max(1, window.canvas.ActualHeight));
							viewport.Width = window.canvas.ActualWidth * scale;
							viewport.Height = window.canvas.ActualHeight * scale;
							clip.Rect = new Rect(0, 0, viewport.Width, viewport.Height);
							preview.Width = window.image.Width * scale;
							preview.Height = window.image.Height * scale;
							previewTransform.Rotation = window.rotation * 90;
							previewTransform.ScaleX = window.EffectiveZoom() * (window.flipped ? -1 : 1);
							previewTransform.ScaleY = window.EffectiveZoom();
							previewTransform.TranslateX = window.panX * scale;
							previewTransform.TranslateY = window.panY * scale;
						}
						updatePreviews.Add(UpdatePreview);
						cell.SizeChanged += (_, _) => UpdatePreview();
						cell.DragStarting += (_, e) => { draggingPhoto = window; e.Data.SetData(DragFormat, string.Empty); e.AllowedOperations = DataPackageOperation.Move | DataPackageOperation.Copy; };
						cell.DropCompleted += (_, _) => { draggingPhoto = null; QueueRefresh(); };
						ConfigureDrop(cell, set, window);
						ToolTipService.SetToolTip(cell, Path.GetFileName(window.paths[window.index]));
						cells.Add(cell);
						previewGrid.Children.Add(cell);
						_ = LoadSetPreviewAsync(preview, window.previewBytes);
					}
					previewGrid.SizeChanged += (_, _) =>
					{
						for (var i = 0; i < cells.Count; i++)
						{
							var region = set.Regions is { } regions && i < regions.Count ? regions[i] : new Files.App.Utils.PhotoLayoutPlanner.Region((i % set.Columns) / (double)set.Columns, (i / set.Columns) / (double)set.Rows, 1d / set.Columns, 1d / set.Rows);
							Canvas.SetLeft(cells[i], region.X * previewGrid.ActualWidth);
							Canvas.SetTop(cells[i], region.Y * previewGrid.ActualHeight);
							cells[i].Width = Math.Max(0, region.Width * previewGrid.ActualWidth - 4);
							cells[i].Height = Math.Max(0, region.Height * previewGrid.ActualHeight - 4);
						}
					};
					Grid.SetRow(previewHost, 1);
					content.Children.Add(previewHost);
					var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
					ConfigureDrop(button, set, null);
					button.Click += (_, _) =>
					{
						if (pin.IsChecked != true) Close();
						MainWindow.Instance.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
						{
							ActivateWindowSet(windows);
						});
					};
					buttons.Add(button);
					cards.Children.Add(button);
				}
				LayoutCards();
			}

			private void LayoutCards()
			{
				if (buttons.Count == 0) return;
				var columns = Math.Min(buttons.Count, Math.Max(1, (int)(scroll.ActualWidth / 300)));
				var rows = (buttons.Count + columns - 1) / columns;
				cards.ColumnDefinitions.Clear();
				cards.RowDefinitions.Clear();
				for (var i = 0; i < columns; i++) cards.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
				for (var i = 0; i < rows; i++) cards.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
				cards.Height = Math.Max(scroll.ActualHeight, rows * 150);
				for (var i = 0; i < buttons.Count; i++) { Grid.SetColumn(buttons[i], i % columns); Grid.SetRow(buttons[i], i / columns); }
			}

			private void ConfigureDrop(UIElement element, PhotoWindowSet? set, PhotoViewerWindow? target)
			{
				element.AllowDrop = true;
				element.DragOver += (_, e) =>
				{
					if (draggingPhoto is null || !e.DataView.Contains(DragFormat)) return;
					var swap = target is not null && e.Modifiers.HasFlag(Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control);
					e.AcceptedOperation = swap ? DataPackageOperation.Copy : DataPackageOperation.Move;
					e.DragUIOverride.Caption = (swap ? Strings.PhotoViewerSwapPhotos : Strings.PhotoViewerMovePhoto).GetLocalizedResource();
					e.Handled = true;
				};
				element.Drop += (_, e) =>
				{
					if (draggingPhoto is not { closed: false } photo || !e.DataView.Contains(DragFormat)) return;
					var swap = target is not null && e.Modifiers.HasFlag(Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control);
					e.AcceptedOperation = swap ? DataPackageOperation.Copy : DataPackageOperation.Move;
					e.Handled = true;
					DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { if (!closed) { MoveBetweenSets(photo, set, target, swap); Activate(); } });
				};
			}
		}

		private static void MoveBetweenSets(PhotoViewerWindow photo, PhotoWindowSet? destination, PhotoViewerWindow? target, bool swap)
		{
			if (photo.closed || target?.closed == true || ReferenceEquals(photo, target)) return;
			var source = windowSets.FirstOrDefault(set => set.Windows.Contains(photo));
			if (source is null || destination is not null && !windowSets.Contains(destination)) return;
			var sourceArea = DisplayArea.GetFromWindowId(photo.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
			var anchor = target ?? destination?.Windows.FirstOrDefault() ?? photo;
			var destinationArea = DisplayArea.GetFromWindowId(anchor.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
			if (destination is null) { destination = new([], 1, 1); windowSets.Add(destination); }
			if (swap && target is { closed: false } && destination.Windows.Contains(target))
			{
				var sourceIndex = source.Windows.IndexOf(photo);
				var targetIndex = destination.Windows.IndexOf(target);
				source.Windows[sourceIndex] = target;
				destination.Windows[targetIndex] = photo;
			}
			else
			{
				source.Windows.Remove(photo);
				var targetIndex = target is null ? -1 : destination.Windows.IndexOf(target);
				if (targetIndex < 0) destination.Windows.Add(photo);
				else destination.Windows.Insert(targetIndex, photo);
			}
			windowSets.RemoveAll(set => set.Windows.Count == 0);
			if (source.Windows.Count != 0) AutoArrangeSet(source, sourceArea);
			if (!ReferenceEquals(source, destination)) AutoArrangeSet(destination, destinationArea);
			RefreshWindowSetsPanel();
		}

	}
}
