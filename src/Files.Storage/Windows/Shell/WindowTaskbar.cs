// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using System.Runtime.InteropServices.Marshalling;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace Files.Storage
{
	public static class WindowTaskbar
	{
		public sealed record TaskItem(string Label, string Arguments, string IconPath);

		public static unsafe void SetTasks(string appId, string executable, IReadOnlyList<TaskItem> tasks)
		{
			var list = DestinationList.CreateInstance<ICustomDestinationList>();
			IObjectArray? removed = null;
			IObjectCollection? collection = null;
			var building = false;
			try
			{
				list.SetAppID(appId).ThrowOnFailure();
				list.BeginList(out _, out removed).ThrowOnFailure();
				building = true;
				collection = EnumerableObjectCollection.CreateInstance<IObjectCollection>();
				foreach (var task in tasks)
				{
					var link = Windows.Win32.UI.Shell.ShellLink.CreateInstance<IShellLinkW>();
					try
					{
						link.SetPath(executable).ThrowOnFailure();
						link.SetArguments(task.Arguments).ThrowOnFailure();
						link.SetIconLocation(task.IconPath, 0).ThrowOnFailure();
						var properties = (IPropertyStore)link;
						fixed (char* title = task.Label)
						{
							PROPVARIANT value = new() { vt = VARENUM.VT_LPWSTR, pwszVal = new PWSTR(title) };
							properties.SetValue(PInvoke.PKEY_Title, value).ThrowOnFailure();
						}
						properties.Commit().ThrowOnFailure();
						collection.AddObject(link).ThrowOnFailure();
					}
					finally { Release(link); }
				}
				list.AddUserTasks((IObjectArray)collection).ThrowOnFailure();
				list.CommitList().ThrowOnFailure();
				building = false;
			}
			finally
			{
				if (building) list.AbortList();
				Release(collection);
				Release(removed);
				Release(list);
			}
		}

		private static void Release(object? value)
		{
			if (value is ComObject owner) owner.FinalRelease();
		}

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
