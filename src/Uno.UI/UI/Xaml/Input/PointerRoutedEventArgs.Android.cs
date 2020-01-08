﻿using System;
using System.Collections.Generic;
using System.Text;
using Windows.Devices.Input;
using Android.Views;
using Uno.UI;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Input;
using Windows.UI.Xaml.Extensions;
using Android.OS;
using Uno.Extensions;

namespace Windows.UI.Xaml.Input
{
	partial class PointerRoutedEventArgs
	{
		/// <summary>
		/// The stylus is pressed while holding the barrel button
		/// </summary>
		internal const MotionEventActions StylusWithBarrelDown = (MotionEventActions)211;
		/// <summary>
		/// The stylus is moved after having been pressed while holding the barrel button
		/// </summary>
		internal const MotionEventActions StylusWithBarrelMove = (MotionEventActions)213;
		/// <summary>
		/// The stylus is released after having been pressed while holding the barrel button
		/// </summary>
		internal const MotionEventActions StylusWithBarrelUp = (MotionEventActions)212;

		private const int _pointerIdsCount = (int)MotionEventActions.PointerIndexMask >> (int)MotionEventActions.PointerIndexShift; // 0xff
		private const int _pointerIdsShift = 31 - (int)MotionEventActions.PointerIndexShift; // 23

		private readonly MotionEvent _nativeEvent;
		private readonly int _pointerIndex;
		private readonly UIElement _receiver;
		private readonly PointerPointProperties _properties;

		internal bool HasPressedButton => _properties.HasPressedButton;

		internal PointerRoutedEventArgs(MotionEvent nativeEvent, int pointerIndex, UIElement originalSource, UIElement receiver) : this()
		{
			_nativeEvent = nativeEvent;
			_pointerIndex = pointerIndex;
			_receiver = receiver;

			// Here we assume that usually pointerId is 'PointerIndexShift' bits long (8 bits / 255 ids),
			// and that usually the deviceId is [0, something_not_too_big_hopefully_less_than_0x00ffffff].
			// If deviceId is greater than 0x00ffffff, we might have a conflict but only in case of multi touch
			// and with a high variation of deviceId. We assume that's safe enough.

			// Note: Make sure to use the GetPointerId in order to make sure to keep the same id while: down_1 / down_2 / up_1 / up_2
			// otherwise up_2 will be with the id of 1
			var pointerId = ((uint)nativeEvent.GetPointerId(pointerIndex) & _pointerIdsCount) << _pointerIdsShift | (uint)nativeEvent.DeviceId;
			var nativePointerType = nativeEvent.GetToolType(pointerIndex);
			var pointerType = nativePointerType.ToPointerDeviceType();
			var isInContact = IsInContact(pointerType, nativeEvent, pointerIndex);
			var keys = nativeEvent.MetaState.ToVirtualKeyModifiers();

			FrameId = (uint)_nativeEvent.EventTime;
			Pointer = new Pointer(pointerId, pointerType, isInContact, isInRange: true);
			KeyModifiers = keys;
			OriginalSource = originalSource;
			CanBubbleNatively = true;

			_properties = GetProperties(nativePointerType); // Last: we need the Pointer property to be set!
		}

		public PointerPoint GetCurrentPoint(UIElement relativeTo)
		{
			var timestamp = ToTimeStamp(_nativeEvent.EventTime);
			var device = PointerDevice.For(Pointer.PointerDeviceType);
			var (rawPosition, position) = GetPositions(relativeTo);

			return new PointerPoint(FrameId, timestamp, device, Pointer.PointerId, rawPosition, position, Pointer.IsInContact, _properties);
		}

		private (Point raw, Point relative) GetPositions(UIElement relativeTo)
		{
			var phyX = _nativeEvent.GetX(_pointerIndex);
			var phyY = _nativeEvent.GetY(_pointerIndex);

			Point raw, relative;
			if (relativeTo == null) // Relative to the window
			{
				var windowToReceiver = new int[2];
				_receiver.GetLocationInWindow(windowToReceiver);

				relative = new Point(phyX + windowToReceiver[0], phyY + windowToReceiver[1]).PhysicalToLogicalPixels();
			}
			else if (relativeTo == _receiver) // Fast path
			{
				relative = new Point(phyX, phyY).PhysicalToLogicalPixels();
			}
			else
			{
				var posRelToReceiver = new Point(phyX, phyX).PhysicalToLogicalPixels();
				var posRelToTarget = UIElement.GetTransform(from: _receiver, to: relativeTo).Transform(posRelToReceiver);

				relative = posRelToTarget;
			}

			// Raw coordinates are relative to the screen (easier for the gesture recognizer to track fingers for manipulations)
			// if (ANDROID > 10)
			// {
			//		var raw = new Point(_nativeEvent.getRawX(_pointerIndex), _nativeEvent.getRawY(_pointerIndex)).PhysicalToLogicalPixels();
			// }
			// else
			{
				var screenToReceiver = new int[2];
				_receiver.GetLocationOnScreen(screenToReceiver);

				raw = new Point(phyX + screenToReceiver[0], phyY + screenToReceiver[1]).PhysicalToLogicalPixels();
			}

			return (raw, relative);
		}

		private PointerPointProperties GetProperties(MotionEventToolType type)
		{
			var props = new PointerPointProperties
			{
				IsPrimary = true,
				IsInRange = Pointer.IsInRange
			};

			var action = _nativeEvent.Action;
			var isDown = action.HasFlag(MotionEventActions.Down) || action.HasFlag(MotionEventActions.PointerDown);
			var isUp = action.HasFlag(MotionEventActions.Up) || action.HasFlag(MotionEventActions.PointerUp);
			var updates = _none;
			switch (type)
			{
				case MotionEventToolType.Finger:
					props.IsLeftButtonPressed = Pointer.IsInContact;
					updates = isDown ? _fingerDownUpdates : isUp ? _fingerUpUpdates : _none;
					break;

				case MotionEventToolType.Mouse:
					props.IsLeftButtonPressed = _nativeEvent.IsButtonPressed(MotionEventButtonState.Primary);
					props.IsMiddleButtonPressed = _nativeEvent.IsButtonPressed(MotionEventButtonState.Tertiary);
					props.IsRightButtonPressed = _nativeEvent.IsButtonPressed(MotionEventButtonState.Secondary);
					updates = isDown ? _mouseDownUpdates : isUp ? _mouseUpUpdates : _none;
					break;

				// Note: On UWP, if you touch screen while already holding the barrel button, you will get a right + barrel,
				//		 ** BUT ** if you touch screen and THEN press the barrel button props will be left + barrel until released.
				//		 On Android this distinction seems to be flagged by the "1101 ****" action flag (i.e. "StylusWithBarrel***" actions),
				//		 so here we set the Is<Left|Right>ButtonPressed based on the action and we don't try to link it to the barrel button state.
				case MotionEventToolType.Stylus when action == StylusWithBarrelDown:
				case MotionEventToolType.Stylus when action == StylusWithBarrelMove:
				case MotionEventToolType.Stylus when action == StylusWithBarrelUp:
					// Note: We still validate the "IsButtonPressed(StylusPrimary)" as the user might release the button while pressed.
					//		 In that case we will still receive moves and up with the "StylusWithBarrel***" actions.
					props.IsBarrelButtonPressed = _nativeEvent.IsButtonPressed(MotionEventButtonState.StylusPrimary);
					props.IsRightButtonPressed = Pointer.IsInContact;
					break;
				case MotionEventToolType.Stylus:
					props.IsBarrelButtonPressed = _nativeEvent.IsButtonPressed(MotionEventButtonState.StylusPrimary);
					props.IsLeftButtonPressed = Pointer.IsInContact;
					break;
				case MotionEventToolType.Eraser:
					props.IsEraser = true;
					break;

				case MotionEventToolType.Unknown: // used by Xamarin.UITest
					props.IsLeftButtonPressed = true;
					break;
				default:
					break;
			}

			if (Android.OS.Build.VERSION.SdkInt >= BuildVersionCodes.M // ActionButton was introduced with API 23 (https://developer.android.com/reference/android/view/MotionEvent.html#getActionButton())
				&& updates.TryGetValue(_nativeEvent.ActionButton, out var update))
			{
				props.PointerUpdateKind = update;
			}

			return props;
		}

		#region Misc static helpers
		private static readonly Dictionary<MotionEventButtonState, PointerUpdateKind> _none = new Dictionary<MotionEventButtonState, PointerUpdateKind>(0);
		private static readonly Dictionary<MotionEventButtonState, PointerUpdateKind> _fingerDownUpdates = new Dictionary<MotionEventButtonState, PointerUpdateKind>
		{
			{ MotionEventButtonState.Primary, PointerUpdateKind.LeftButtonPressed }
		};
		private static readonly Dictionary<MotionEventButtonState, PointerUpdateKind> _fingerUpUpdates = new Dictionary<MotionEventButtonState, PointerUpdateKind>
		{
			{ MotionEventButtonState.Primary, PointerUpdateKind.LeftButtonReleased }
		};
		private static readonly Dictionary<MotionEventButtonState, PointerUpdateKind> _mouseDownUpdates = new Dictionary<MotionEventButtonState, PointerUpdateKind>
		{
			{ MotionEventButtonState.Primary, PointerUpdateKind.LeftButtonPressed },
			{ MotionEventButtonState.Tertiary, PointerUpdateKind.MiddleButtonPressed },
			{ MotionEventButtonState.Secondary, PointerUpdateKind.RightButtonPressed }
		};
		private static readonly Dictionary<MotionEventButtonState, PointerUpdateKind> _mouseUpUpdates = new Dictionary<MotionEventButtonState, PointerUpdateKind>
		{
			{ MotionEventButtonState.Primary, PointerUpdateKind.LeftButtonReleased },
			{ MotionEventButtonState.Tertiary, PointerUpdateKind.MiddleButtonReleased },
			{ MotionEventButtonState.Secondary, PointerUpdateKind.RightButtonReleased }
		};

		private static readonly ulong _unixEpochMs = (ulong)(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc) - new DateTime()).TotalMilliseconds;

		private static ulong ToTimeStamp(long uptimeMillis)
		{
			if (FeatureConfiguration.PointerRoutedEventArgs.AllowRelativeTimeStamp)
			{
				return (ulong)(TimeSpan.TicksPerMillisecond * uptimeMillis);
			}
			else
			{
				// We cannot cache the "bootTime" as the "uptimeMillis" is frozen while in deep sleep
				// (cf. https://developer.android.com/reference/android/os/SystemClock)

				var sleepTime = Android.OS.SystemClock.ElapsedRealtime() - Android.OS.SystemClock.UptimeMillis();
				var realUptime = (ulong)(uptimeMillis + sleepTime);
				var timestamp = TimeSpan.TicksPerMillisecond * (_unixEpochMs + realUptime);

				return timestamp;
			}
		}

		private static bool IsInContact(PointerDeviceType type, MotionEvent nativeEvent, int pointerIndex)
		{
			switch (type)
			{
				case PointerDeviceType.Mouse:
					return nativeEvent.ButtonState != 0;

				default:
				case PointerDeviceType.Pen:
				case PointerDeviceType.Touch:
					// WARNING: MotionEventActions.Down == 0, so action.HasFlag(MotionEventActions.Up) is always true!
					var action = nativeEvent.Action;
					return !action.HasFlag(MotionEventActions.Up)
						&& !action.HasFlag(MotionEventActions.PointerUp)
						&& !action.HasFlag(MotionEventActions.Cancel)
						&& !action.HasFlag(MotionEventActions.HoverEnter)
						&& !action.HasFlag(MotionEventActions.HoverMove)
						&& !action.HasFlag(MotionEventActions.HoverExit);
			}
		}
		#endregion
	}
}
