// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using System.Runtime.InteropServices.Marshalling;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace Files.Storage
{
	public static class WindowTaskbar
	{
		public static unsafe void SetPhotoViewerIdentity(nint window, string? appId)
		{
			PInvoke.SHGetPropertyStoreForWindow(new HWND(window), out IPropertyStore store).ThrowOnFailure();
			try
			{
				PROPVARIANT empty = default;
				// Set pinning policy before the ID; the viewer is launched through Files.
				PROPVARIANT preventPinning = appId is null ? empty : new() { vt = VARENUM.VT_BOOL, boolVal = new(-1) };
				store.SetValue(PInvoke.PKEY_AppUserModel_PreventPinning, preventPinning).ThrowOnFailure();
				fixed (char* text = appId)
				{
					PROPVARIANT value = appId is null ? empty : new() { vt = VARENUM.VT_LPWSTR, pwszVal = new PWSTR(text) };
					store.SetValue(PInvoke.PKEY_AppUserModel_ID, value).ThrowOnFailure();
				}
			}
			finally
			{
				if ((object)store is ComObject owner) owner.FinalRelease();
			}
		}
	}
}
