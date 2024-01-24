﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.
// MUX Reference dxaml\xcp\dxaml\lib\KeyboardAccelerator_Partial.cpp, tag winui3/release/1.4.3, commit 685d2bf

using System.Collections.Generic;
using System.Globalization;
using DirectUI;
using Windows.System;

namespace Microsoft.UI.Xaml.Input;

partial class KeyboardAccelerator
{
	/* static */
	internal static bool RaiseKeyboardAcceleratorInvoked(
		KeyboardAccelerator pNativeAccelerator,
		DependencyObject pElement)
	{
		var spPeerDO = pNativeAccelerator;

		if (spPeerDO is null)
		{
			// There is no need to fire the event if there is no peer, since no one is listening
			return false;
		}

		var spElementPeerDO = pElement;

		KeyboardAccelerator spAccelerator = spPeerDO;

		KeyboardAcceleratorInvokedEventArgs spArgs = new(spElementPeerDO, pNativeAccelerator);
		pNativeAccelerator.Invoked?.Invoke(pNativeAccelerator, spArgs);
		var pIsHandled = spArgs.Handled;

		// If not handled, raise an event on parent element to give it a chance to handle the event.
		// This will enable controls which don't have automated invoked action, to handle the event. e.g Pivot
		if (!pIsHandled)
		{
			UIElement.RaiseKeyboardAcceleratorInvokedStatic(pElement, spArgs, pIsHandled));
		}

		return pIsHandled;
	}

	internal static string GetStringRepresentationForUIElement(UIElement uiElement)
	{
		// We don't want to bother doing anything if we've never actually set a keyboard accelerator,
		// so we'll just return null unless we have.
		// UNO TODO if (!uiElement.GetHandle().CheckOnDemandProperty(KnownPropertyIndex.UIElement_KeyboardAccelerators).IsNull())
		if (uiElement.KeyboardAccelerators.Count != 0)
		{
			IList<KeyboardAccelerator> keyboardAccelerators;
			int keyboardAcceleratorCount;

			keyboardAccelerators = uiElement.KeyboardAccelerators;
			keyboardAcceleratorCount = keyboardAccelerators.Count;

			if (keyboardAcceleratorCount > 0)
			{
				var keyboardAcceleratorStringRepresentation = keyboardAccelerators[0];
				return keyboardAcceleratorStringRepresentation.GetStringRepresentation();
			}
		}

		return null;
	}

	string GetStringRepresentation()
	{
		var key = Key;
		var modifiers = Modifiers;
		var stringRepresentationLocal = "";

		if ((modifiers & VirtualKeyModifiers.Control) != 0)
		{
			ConcatVirtualKey(VirtualKey.Control, ref stringRepresentationLocal);
		}

		if ((modifiers & VirtualKeyModifiers.Menu) != 0)
		{
			ConcatVirtualKey(VirtualKey.Menu, ref stringRepresentationLocal);
		}

		if ((modifiers & VirtualKeyModifiers.Windows) != 0)
		{
			ConcatVirtualKey(VirtualKey.LeftWindows, ref stringRepresentationLocal);
		}

		if ((modifiers & VirtualKeyModifiers.Shift) != 0)
		{
			ConcatVirtualKey(VirtualKey.Shift, ref stringRepresentationLocal);
		}

		ConcatVirtualKey(key, ref stringRepresentationLocal);

		return stringRepresentationLocal;
	}

	void ConcatVirtualKey(VirtualKey key, ref string keyboardAcceleratorString)
	{
		string keyName;

		// UNO TODO
		//(DXamlCore.GetCurrent().GetLocalizedResourceString(GetResourceStringIdFromVirtualKey(key), keyName.ReleaseAndGetAddressOf()));
		keyName = key.ToString();


		if (string.IsNullOrEmpty(keyboardAcceleratorString))
		{
			// If this is the first key string we've accounted for, then
			// we can just set the keyboard accelerator string equal to it.
			keyboardAcceleratorString = keyName;
		}
		else
		{
			// Otherwise, if we already had an existing keyboard accelerator string,
			// then we'll use the formatting string to join strings together
			// to combine it with the new key string.
			string joiningFormatString;
			// UNO TODO
			// (DXamlCore.GetCurrent().GetLocalizedResourceString(KEYBOARD_ACCELERATOR_TEXT_JOIN, joiningFormatString.ReleaseAndGetAddressOf()));
			joiningFormatString = "{0} + {1}";

			keyboardAcceleratorString = string.Format(CultureInfo.InvariantCulture, joiningFormatString, keyboardAcceleratorString, keyName);
		}
	}
}
