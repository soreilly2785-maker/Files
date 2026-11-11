// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinRT;

namespace Files.App.Views
{
	public sealed partial class PhotoViewerWindow
	{
		internal const string TaskArgumentPrefix = "files-photo-window:";
		private static readonly List<PhotoViewerWindow> openWindows = [];
		private static PhotoViewerWindow? lastActiveWindow;
		private static readonly List<PhotoWindowSet> windowSets = [];

		private sealed class PhotoWindowSet(List<PhotoViewerWindow> windows, int columns, int rows)
		{
			internal List<PhotoViewerWindow> Windows { get; } = windows;
			internal int Columns { get; set; } = columns;
			internal int Rows { get; set; } = rows;
		}

		internal static bool HasOpenWindows => openWindows.Count != 0;

		private OverlappedPresenter? Presenter
		{
			[DynamicWindowsRuntimeCast(typeof(OverlappedPresenter))]
			get => AppWindow.Presenter as OverlappedPresenter;
		}

		internal static IEnumerable<(string Command, string Label)> GetArrangementCommands()
		{
			yield return ("maximize", Strings.PhotoViewerMaximizeAll.GetLocalizedResource());
			yield return ("native", Strings.PhotoViewerRestoreAll.GetLocalizedResource());
			yield return ("tile", Strings.PhotoViewerTileAll.GetLocalizedResource());
			yield return ("auto", Strings.PhotoViewerAutoArrange.GetLocalizedResource());
			yield return ("columns3", Strings.PhotoViewerThreeColumns.GetLocalizedResource());
			yield return ("columns4", Strings.PhotoViewerFourColumns.GetLocalizedResource());
			yield return ("grid4", Strings.PhotoViewerFourGrid.GetLocalizedResource());
			yield return ("grid6", Strings.PhotoViewerSixGrid.GetLocalizedResource());
			yield return ("close", Strings.PhotoViewerCloseAll.GetLocalizedResource());
		}

		internal static bool TryParseTaskArguments(string? arguments, out int processId, out string command)
		{
			processId = 0;
			command = string.Empty;
			if (arguments is null || !arguments.StartsWith(TaskArgumentPrefix, StringComparison.Ordinal))
				return false;
			var parts = arguments[TaskArgumentPrefix.Length..].Split(':');
			if (parts.Length != 2 || !int.TryParse(parts[0], out processId) || processId <= 0 ||
				parts[1] is not ("maximize" or "native" or "tile" or "auto" or "columns3" or "columns4" or "grid4" or "grid6" or "close" or "sets" or "fill" or "fit"))
				return false;
			command = parts[1];
			return true;
		}

		private void Minimize() => Presenter?.Minimize();

		private void ToggleMaximize()
		{
			if (Presenter is not { } presenter) return;
			if (presenter.State == OverlappedPresenterState.Maximized)
				presenter.Restore();
			else
				presenter.Maximize();
		}

		private bool IsLargerThanHalfScreen()
		{
			var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
			return Presenter?.State == OverlappedPresenterState.Maximized || AppWindow.Size.Width > area.Width / 2 + 2 || AppWindow.Size.Height > area.Height / 2 + 2;
		}

		private void ResizeToHalfScreen()
		{
			var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
			Presenter?.Restore();
			ResetView();
			var width = area.Width / 2;
			var height = area.Height / 2;
			AppWindow.MoveAndResize(new(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
		}

		private static void CloseAllPhotos()
		{
			foreach (var window in openWindows.ToArray())
				if (!window.closed) window.Close();
		}

		private void ResizeToNative()
		{
			if (pixelWidth == 0 || pixelHeight == 0) return;
			var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
			var frameWidth = AppWindow.Size.Width - AppWindow.ClientSize.Width;
			var frameHeight = AppWindow.Size.Height - AppWindow.ClientSize.Height;
			var nativeWidth = rotation % 2 == 0 ? pixelWidth : pixelHeight;
			var nativeHeight = rotation % 2 == 0 ? pixelHeight : pixelWidth;
			var fit = Math.Min(1, Math.Min((double)(area.Width - frameWidth) / nativeWidth, (double)(area.Height - frameHeight) / nativeHeight));
			AppWindow.Resize(new(Math.Max(150, (int)(nativeWidth * fit)) + frameWidth, Math.Max(112, (int)(nativeHeight * fit)) + frameHeight));
			AppWindow.Move(new(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + (area.Height - AppWindow.Size.Height) / 2));
		}

		internal static void Arrange(string command)
		{
			if (command == "close") { CloseAllPhotos(); return; }
			var windows = openWindows.Where(window => !window.closed).ToArray();
			if (windows.Length == 0) return;
			var anchor = lastActiveWindow is { closed: false } active ? active : windows[^1];
			if (command == "sets") { anchor.ShowWindowSets(); return; }
			if (command == "fill") { foreach (var window in windows) window.FillWindow(); return; }
			if (command == "fit") { foreach (var window in windows) window.ResetView(); return; }
			var area = DisplayArea.GetFromWindowId(anchor.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
			var automatic = command == "auto" ? ChooseAutomaticGrid(windows, area) : (Columns: 0, Rows: 0);
			var columns = command switch
			{
				"columns3" => 3,
				"columns4" => 4,
				"grid4" => 2,
				"grid6" => 3,
				"auto" => automatic.Columns,
				_ => windows.Length <= 3 ? windows.Length : (int)Math.Ceiling(Math.Sqrt(windows.Length))
			};
			var rows = command switch
			{
				"columns3" or "columns4" => 1,
				"grid4" or "grid6" => 2,
				"auto" => automatic.Rows,
				_ => (windows.Length + columns - 1) / columns
			};
			if (command is not ("maximize" or "native"))
			{
				windowSets.Clear();
				var batchSize = columns * rows;
				for (var start = 0; start < windows.Length; start += batchSize)
					windowSets.Add(new(windows.Skip(start).Take(batchSize).ToList(), columns, rows));
			}
			for (var i = 0; i < windows.Length; i++)
			{
				var window = windows[i];
				var presenter = window.Presenter;
				if (command == "maximize")
				{
					presenter?.Maximize();
					continue;
				}
				presenter?.Restore();
				if (command == "native")
				{
					window.ResizeToNative();
					continue;
				}
				var cell = i % (columns * rows);
				var column = cell % columns;
				var row = cell / columns;
				var left = area.X + area.Width * column / columns;
				var top = area.Y + area.Height * row / rows;
				var right = area.X + area.Width * (column + 1) / columns;
				var bottom = area.Y + area.Height * (row + 1) / rows;
				window.AppWindow.MoveAndResize(new RectInt32(left, top, right - left, bottom - top));
			}
			RefreshWindowSetsPanel();
			var foreground = command is "columns3" or "columns4" or "grid4" or "grid6" ? windows[^1] : anchor;
			if (!foreground.closed)
				foreground.RaiseToFront();
		}

		private static (int Columns, int Rows) ChooseAutomaticGrid(PhotoViewerWindow[] windows, RectInt32 area)
		{
			var best = (Columns: 1, Rows: windows.Length);
			var bestCoverage = -1d;
			for (var columns = 1; columns <= windows.Length; columns++)
			{
				var rows = (windows.Length + columns - 1) / columns;
				var cellAspect = (double)area.Width * rows / (area.Height * columns);
				var coverage = 0d;
				foreach (var window in windows)
				{
					var aspect = window.pixelHeight == 0 ? 4d / 3 : (double)window.pixelWidth / window.pixelHeight;
					if (window.rotation % 2 != 0) aspect = 1 / aspect;
					coverage += Math.Min(aspect / cellAspect, cellAspect / aspect);
				}
				coverage /= columns * rows;
				if (coverage > bestCoverage)
				{
					bestCoverage = coverage;
					best = (columns, rows);
				}
			}
			return best;
		}

		private static void RemoveFromWindowSets(PhotoViewerWindow window)
		{
			foreach (var set in windowSets) set.Windows.Remove(window);
			windowSets.RemoveAll(set => set.Windows.Count == 0);
		}
	}
}
