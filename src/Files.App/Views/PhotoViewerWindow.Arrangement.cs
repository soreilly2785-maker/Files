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
			yield return ("columns3", Strings.PhotoViewerThreeColumns.GetLocalizedResource());
			yield return ("columns4", Strings.PhotoViewerFourColumns.GetLocalizedResource());
			yield return ("grid4", Strings.PhotoViewerFourGrid.GetLocalizedResource());
		}

		internal static bool TryParseTaskArguments(string? arguments, out int processId, out string command)
		{
			processId = 0;
			command = string.Empty;
			if (arguments is null || !arguments.StartsWith(TaskArgumentPrefix, StringComparison.Ordinal))
				return false;
			var parts = arguments[TaskArgumentPrefix.Length..].Split(':');
			if (parts.Length != 2 || !int.TryParse(parts[0], out processId) || processId <= 0 ||
				parts[1] is not ("maximize" or "native" or "tile" or "columns3" or "columns4" or "grid4"))
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

		private void ResizeToNative()
		{
			if (pixelWidth == 0 || pixelHeight == 0) return;
			var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
			var frameWidth = AppWindow.Size.Width - AppWindow.ClientSize.Width;
			var frameHeight = AppWindow.Size.Height - AppWindow.ClientSize.Height;
			var nativeWidth = rotation % 2 == 0 ? pixelWidth : pixelHeight;
			var nativeHeight = rotation % 2 == 0 ? pixelHeight : pixelWidth;
			var fit = Math.Min(1, Math.Min((double)(area.Width - frameWidth) / nativeWidth, (double)(area.Height - frameHeight) / nativeHeight));
			AppWindow.ResizeClient(new(Math.Max(150, (int)(nativeWidth * fit)), Math.Max(112, (int)(nativeHeight * fit))));
			AppWindow.Move(new(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + (area.Height - AppWindow.Size.Height) / 2));
		}

		internal static void Arrange(string command)
		{
			var windows = openWindows.Where(window => !window.closed).ToArray();
			if (windows.Length == 0) return;
			var anchor = lastActiveWindow is { closed: false } active ? active : windows[^1];
			var area = DisplayArea.GetFromWindowId(anchor.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
			var columns = command switch
			{
				"columns3" => 3,
				"columns4" => 4,
				"grid4" => 2,
				_ => windows.Length <= 3 ? windows.Length : (int)Math.Ceiling(Math.Sqrt(windows.Length))
			};
			var rows = command is "columns3" or "columns4" ? 1 : command == "grid4" ? 2 : (windows.Length + columns - 1) / columns;
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
			var foreground = command is "columns3" or "columns4" or "grid4" ? windows[^1] : anchor;
			if (!foreground.closed)
				Win32Helper.BringToForegroundEx(new(foreground.WindowHandle));
		}
	}
}
