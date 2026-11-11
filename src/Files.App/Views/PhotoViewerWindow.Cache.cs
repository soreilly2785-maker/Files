// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Files.App.Views
{
	public sealed partial class PhotoViewerWindow
	{
		private const int CacheRadius = 5;
		private const double CachePixels = 1024 * 1024;
		private readonly Dictionary<string, PreparedPhoto> photoCache = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, TaskCompletionSource<bool>> pendingPhotos = new(StringComparer.OrdinalIgnoreCase);
		private readonly SemaphoreSlim prefetchGate = new(1);
		private readonly CancellationTokenSource cacheLifetime = new();
		private PreparedPhoto? displayedPhoto;

		private sealed class PreparedPhoto(string path, SoftwareBitmapSource source, SoftwareBitmap bitmap, uint width, uint height, double scale, Windows.UI.Color background, byte[] preview, bool cacheQuality)
		{
			internal string Path { get; } = path;
			internal SoftwareBitmapSource Source { get; } = source;
			private SoftwareBitmap Bitmap { get; } = bitmap;
			internal uint Width { get; } = width;
			internal uint Height { get; } = height;
			internal double Scale { get; } = scale;
			internal Windows.UI.Color Background { get; } = background;
			internal byte[] Preview { get; } = preview;
			internal bool CacheQuality { get; } = cacheQuality;
			private bool disposed;

			internal void Dispose()
			{
				if (disposed) return;
				disposed = true;
				Source.Dispose();
				Bitmap.Dispose();
			}
		}

		private bool HasCurrentPhoto => displayedPhoto is not null && StringComparer.OrdinalIgnoreCase.Equals(displayedPhoto.Path, paths[index]);

		private HashSet<string> WantedPhotos()
		{
			var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { paths[index] };
			for (var distance = 1; distance <= Math.Min(CacheRadius, paths.Length - 1); distance++)
			{
				wanted.Add(paths[(index + distance) % paths.Length]);
				wanted.Add(paths[(index - distance + paths.Length) % paths.Length]);
			}
			return wanted;
		}

		private void TrimPhotoCache()
		{
			var wanted = WantedPhotos();
			foreach (var pair in photoCache.ToArray())
			{
				if (wanted.Contains(pair.Key) || ReferenceEquals(pair.Value, displayedPhoto)) continue;
				photoCache.Remove(pair.Key);
				pair.Value.Dispose();
			}
		}

		private void CachePhoto(PreparedPhoto photo)
		{
			if (photoCache.TryGetValue(photo.Path, out var old))
			{
				if (ReferenceEquals(old, photo)) return;
				if (ReferenceEquals(old, displayedPhoto)) { photo.Dispose(); return; }
				old.Dispose();
			}
			photoCache[photo.Path] = photo;
		}

		private void ShowPreparedPhoto(PreparedPhoto photo)
		{
			var old = displayedPhoto;
			displayedPhoto = photo;
			pixelWidth = photo.Width;
			pixelHeight = photo.Height;
			decodedScale = photo.Scale;
			image.Source = photo.Source;
			canvas.Background = new SolidColorBrush(photo.Background);
			previewBytes = photo.Preview;
			previewPath = photo.Path;
			Title = Path.GetFileName(photo.Path);
			status.Text = string.Empty;
			FitImage();
			if (photo.CacheQuality) CachePhoto(photo);
			if (old is not null && !old.CacheQuality && !ReferenceEquals(old, photo)) old.Dispose();
			TrimPhotoCache();
			if (openingImage)
			{
				openingImage = false;
				BringOpeningToFront();
				openingFocusTimer.Start();
			}
		}

		private void PrefetchNearbyPhotos()
		{
			if (closed) return;
			TrimPhotoCache();
			QueuePrefetch(paths[index]);
			for (var distance = 1; distance <= Math.Min(CacheRadius, paths.Length - 1); distance++)
			{
				QueuePrefetch(paths[(index + distance) % paths.Length]);
				QueuePrefetch(paths[(index - distance + paths.Length) % paths.Length]);
			}
		}

		private void QueuePrefetch(string path)
		{
			if (photoCache.ContainsKey(path) || pendingPhotos.ContainsKey(path)) return;
			var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			pendingPhotos.Add(path, completion);
			_ = PrefetchPhotoAsync(path, completion);
		}

		private async Task PrefetchPhotoAsync(string path, TaskCompletionSource<bool> completion)
		{
			var entered = false;
			PreparedPhoto? photo = null;
			try
			{
				await prefetchGate.WaitAsync(cacheLifetime.Token);
				entered = true;
				if (!WantedPhotos().Contains(path) || photoCache.ContainsKey(path)) return;
				photo = await DecodePhotoAsync(path, true, cacheLifetime.Token);
				if (!closed && WantedPhotos().Contains(path)) { CachePhoto(photo); photo = null; }
			}
			catch (OperationCanceledException) { }
			catch (Exception ex) { Debug.WriteLine(ex); }
			finally
			{
				photo?.Dispose();
				pendingPhotos.Remove(path);
				completion.TrySetResult(photoCache.ContainsKey(path));
				if (entered) prefetchGate.Release();
			}
		}

		private async Task<PreparedPhoto> DecodePhotoAsync(string path, bool cacheQuality, CancellationToken token)
		{
			var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
			using var stream = await file.OpenReadAsync().AsTask(token);
			var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
			token.ThrowIfCancellationRequested();
			var width = decoder.OrientedPixelWidth;
			var height = decoder.OrientedPixelHeight;
			if (initialSize && displayedPhoto is null && StringComparer.OrdinalIgnoreCase.Equals(path, paths[index]))
			{
				pixelWidth = width;
				pixelHeight = height;
				initialSize = false;
				ResizeToNative();
			}
			var frame = AppWindow.ClientSize;
			var photoRotation = cacheQuality ? 0 : rotation;
			var fit = Math.Min((double)Math.Max(1, frame.Width) / (photoRotation % 2 == 0 ? width : height),
				(double)Math.Max(1, frame.Height) / (photoRotation % 2 == 0 ? height : width));
			var cacheScale = Math.Min(1, Math.Min(fit, Math.Sqrt(CachePixels / (width * (double)height))));
			var scale = cacheQuality ? cacheScale : Math.Min(1, Math.Min(fit * zoom, Math.Sqrt(MaxDecodedPixels / (width * (double)height))));
			SoftwareBitmapSource? source = null;
			SoftwareBitmap? bitmap = null;
			PreparedPhoto? result = null;
			try
			{
				var bitmapTransform = new BitmapTransform { ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale)), ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale)), InterpolationMode = BitmapInterpolationMode.Fant };
				bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, bitmapTransform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(token);
				source = new SoftwareBitmapSource();
				await source.SetBitmapAsync(bitmap);
				token.ThrowIfCancellationRequested();
				var preview = StringComparer.OrdinalIgnoreCase.Equals(previewPath, path) && previewBytes is not null ? previewBytes : await EncodeSetPreviewAsync(bitmap, token);
				using var memory = new MemoryStream(preview);
				using var previewStream = memory.AsRandomAccessStream();
				var previewDecoder = await BitmapDecoder.CreateAsync(previewStream);
				using var sample = await previewDecoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
				var pixels = new byte[sample.PixelWidth * sample.PixelHeight * 4];
				sample.CopyToBuffer(pixels.AsBuffer());
				long red = 0, green = 0, blue = 0, alpha = 0;
				for (var i = 0; i < pixels.Length; i += 4) { blue += pixels[i]; green += pixels[i + 1]; red += pixels[i + 2]; alpha += pixels[i + 3]; }
				var background = Windows.UI.Color.FromArgb(255, (byte)(red * 255 / Math.Max(1, alpha)), (byte)(green * 255 / Math.Max(1, alpha)), (byte)(blue * 255 / Math.Max(1, alpha)));
				token.ThrowIfCancellationRequested();
				result = new(path, source, bitmap, width, height, scale, background, preview, cacheQuality || Math.Abs(scale - cacheScale) < 0.0001);
				source = null;
				bitmap = null;
				return result;
			}
			catch { source?.Dispose(); bitmap?.Dispose(); result?.Dispose(); throw; }
		}
	}
}
