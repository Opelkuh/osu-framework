// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using osu.Framework.Caching;
using osu.Framework.Input;
using osu.Framework.Input.StateChanges;
using osu.Framework.Threading;
using osuTK;
using osuTK.Input;
using SDL2;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace osu.Framework.Platform.Sdl
{
    /// <summary>
    /// Implementation of <see cref="IWindowBackend"/> that uses libSDL2.
    /// </summary>
    public class Sdl2WindowBackend : WindowBackend
    {
        private const int default_width = 1366;
        private const int default_height = 768;
        private const float deadzone_threshold = 0.075f;

        private readonly Scheduler commandScheduler = new Scheduler();
        private readonly Scheduler eventScheduler = new Scheduler();

        private bool mouseInWindow;
        private Point previousPolledPoint = Point.Empty;

        private readonly Dictionary<int, JoystickButton[]> axisDirectionButtons = new Dictionary<int, JoystickButton[]>();
        private readonly Dictionary<int, ControllerBindings> bindings = new Dictionary<int, ControllerBindings>();
        private readonly Dictionary<int, IntPtr> joysticks = new Dictionary<int, IntPtr>();

        #region Internal Properties

        internal IntPtr SdlWindowHandle { get; private set; } = IntPtr.Zero;

        #endregion

        #region IWindowBackend.Properties

        public override bool Exists { get; protected set; }

        private string title = "";

        public override string Title
        {
            get => SdlWindowHandle == IntPtr.Zero ? title : SDL.SDL_GetWindowTitle(SdlWindowHandle);
            set
            {
                title = value;
                commandScheduler.Add(() => SDL.SDL_SetWindowTitle(SdlWindowHandle, $"{value} (SDL)"));
            }
        }

        private bool visible;

        public override bool Visible
        {
            get => SdlWindowHandle == IntPtr.Zero ? visible : windowFlags.HasFlag(SDL.SDL_WindowFlags.SDL_WINDOW_SHOWN);
            set
            {
                visible = value;
                commandScheduler.Add(() =>
                {
                    if (value)
                        SDL.SDL_ShowWindow(SdlWindowHandle);
                    else
                        SDL.SDL_HideWindow(SdlWindowHandle);
                });
            }
        }

        private Point position = Point.Empty;

        public override Point Position
        {
            get
            {
                if (SdlWindowHandle == IntPtr.Zero)
                    return position;

                SDL.SDL_GetWindowPosition(SdlWindowHandle, out var x, out var y);
                return new Point(x, y);
            }
            set
            {
                position = value;
                commandScheduler.Add(() => SDL.SDL_SetWindowPosition(SdlWindowHandle, value.X, value.Y));
            }
        }

        private Size size = new Size(default_width, default_height);

        public override Size Size
        {
            get
            {
                if (SdlWindowHandle == IntPtr.Zero)
                    return size;

                SDL.SDL_GetWindowSize(SdlWindowHandle, out var w, out var h);
                return new Size(w, h);
            }
            set
            {
                size = value;
                commandScheduler.Add(() => SDL.SDL_SetWindowSize(SdlWindowHandle, value.Width, value.Height));
            }
        }

        private readonly Cached<float> cachedScale = new Cached<float>();

        private float scale => validateScale();

        private float validateScale(bool force = false)
        {
            if (SdlWindowHandle == IntPtr.Zero)
                return 1f;

            if (!force && cachedScale.IsValid)
                return cachedScale.Value;

            var w = ClientSize.Width;
            float value = 1f;

            switch (windowFlags.ToWindowState())
            {
                case WindowState.Normal:
                    value = w / (float)Size.Width;
                    break;

                case WindowState.Fullscreen:
                    value = w / (float)windowDisplayMode.w;
                    break;

                case WindowState.FullscreenBorderless:
                    // SDL_GetDesktopDisplayMode gets the native display mode, and is used for *borderless* fullscreen
                    SDL.SDL_GetDesktopDisplayMode(windowDisplayIndex, out var mode);
                    value = w / (float)mode.w;
                    break;

                case WindowState.Maximised:
                case WindowState.Minimised:
                    return 1f;
            }

            cachedScale.Value = value;
            return value;
        }

        private bool cursorVisible = true;

        public override bool CursorVisible
        {
            get => SdlWindowHandle == IntPtr.Zero ? cursorVisible : SDL.SDL_ShowCursor(SDL.SDL_QUERY) == SDL.SDL_ENABLE;
            set
            {
                cursorVisible = value;
                commandScheduler.Add(() => SDL.SDL_ShowCursor(value ? SDL.SDL_ENABLE : SDL.SDL_DISABLE));
            }
        }

        private bool cursorConfined;

        public override bool CursorConfined
        {
            get => SdlWindowHandle == IntPtr.Zero ? cursorConfined : SDL.SDL_GetWindowGrab(SdlWindowHandle) == SDL.SDL_bool.SDL_TRUE;
            set
            {
                cursorConfined = value;
                commandScheduler.Add(() => SDL.SDL_SetWindowGrab(SdlWindowHandle, value ? SDL.SDL_bool.SDL_TRUE : SDL.SDL_bool.SDL_FALSE));
            }
        }

        private WindowState initialWindowState = WindowState.Normal;
        private WindowState lastWindowState;

        public override WindowState WindowState
        {
            get => SdlWindowHandle == IntPtr.Zero ? initialWindowState : windowFlags.ToWindowState();
            set
            {
                if (SdlWindowHandle == IntPtr.Zero)
                {
                    initialWindowState = value;
                    return;
                }

                commandScheduler.Add(() =>
                {
                    switch (value)
                    {
                        case WindowState.Normal:
                            SDL.SDL_SetWindowFullscreen(SdlWindowHandle, (uint)SDL.SDL_bool.SDL_FALSE);
                            break;

                        case WindowState.Fullscreen:
                            // set window display mode again, just in case if it changed from the last time we were fullscreen.
                            var fullscreenMode = closestDisplayMode(currentDisplayMode);
                            SDL.SDL_SetWindowDisplayMode(SdlWindowHandle, ref fullscreenMode);

                            SDL.SDL_SetWindowFullscreen(SdlWindowHandle, (uint)SDL.SDL_WindowFlags.SDL_WINDOW_FULLSCREEN);
                            break;

                        case WindowState.FullscreenBorderless:
                            SDL.SDL_SetWindowFullscreen(SdlWindowHandle, (uint)SDL.SDL_WindowFlags.SDL_WINDOW_FULLSCREEN_DESKTOP);
                            break;

                        case WindowState.Maximised:
                            SDL.SDL_MaximizeWindow(SdlWindowHandle);
                            break;

                        case WindowState.Minimised:
                            SDL.SDL_MinimizeWindow(SdlWindowHandle);
                            break;
                    }
                });
            }
        }

        public override Size ClientSize
        {
            get
            {
                if (SdlWindowHandle == IntPtr.Zero)
                    return Size.Empty;

                SDL.SDL_GL_GetDrawableSize(SdlWindowHandle, out var w, out var h);
                return new Size(w, h);
            }
        }

        public override IEnumerable<Display> Displays => Enumerable.Range(0, SDL.SDL_GetNumVideoDisplays()).Select(displayFromSDL);

        private Display currentDisplay;
        private int lastDisplayIndex = -1;

        public override Display CurrentDisplay
        {
            get => currentDisplay ??= Displays.ElementAtOrDefault(SdlWindowHandle == IntPtr.Zero ? 0 : windowDisplayIndex);
            set
            {
                if (value.Index == windowDisplayIndex)
                    return;

                int x = value.Bounds.Left + value.Bounds.Width / 2 - size.Width / 2;
                int y = value.Bounds.Top + value.Bounds.Height / 2 - size.Height / 2;

                WindowState = WindowState.Normal;
                Position = new Point(x, y);
            }
        }

        private DisplayMode currentDisplayMode;

        public override DisplayMode CurrentDisplayMode
        {
            get => SdlWindowHandle == IntPtr.Zero ? currentDisplayMode : displayModeFromSDL(windowDisplayMode, windowDisplayIndex, 0);
            set
            {
                currentDisplayMode = value;

                commandScheduler.Add(() =>
                {
                    var closest = closestDisplayMode(value);
                    var wasFullscreen = windowFlags.ToWindowState() == WindowState.Fullscreen;
                    if (wasFullscreen)
                        SDL.SDL_SetWindowFullscreen(SdlWindowHandle, (uint)SDL.SDL_bool.SDL_FALSE);

                    SDL.SDL_SetWindowDisplayMode(SdlWindowHandle, ref closest);

                    if (wasFullscreen)
                        SDL.SDL_SetWindowFullscreen(SdlWindowHandle, (uint)SDL.SDL_WindowFlags.SDL_WINDOW_FULLSCREEN);

                    cachedScale.Invalidate();
                });
            }
        }

        #endregion

        #region Convenience Functions

        private int windowDisplayIndex => SdlWindowHandle == IntPtr.Zero ? 0 : SDL.SDL_GetWindowDisplayIndex(SdlWindowHandle);

        private Rectangle windowDisplayBounds
        {
            get
            {
                SDL.SDL_GetDisplayBounds(windowDisplayIndex, out var rect);
                return new Rectangle(rect.x, rect.y, rect.w, rect.h);
            }
        }

        private SDL.SDL_WindowFlags windowFlags => SdlWindowHandle == IntPtr.Zero ? 0 : (SDL.SDL_WindowFlags)SDL.SDL_GetWindowFlags(SdlWindowHandle);

        private SDL.SDL_DisplayMode windowDisplayMode
        {
            get
            {
                if (SdlWindowHandle == IntPtr.Zero)
                    return default;

                // SDL_GetWindowDisplayMode gets the resolution currently assigned to the window for *exclusive* fullscreen
                if (SDL.SDL_GetWindowDisplayMode(SdlWindowHandle, out var mode) >= 0)
                    return mode;

                // SDL_GetWindowDisplayMode can fail if the window was shown fullscreen on a different (especially larger) window before.
                // if that happens, fall back to closest mode for the current display.
                return closestDisplayMode(CurrentDisplayMode);
            }
        }

        private SDL.SDL_DisplayMode closestDisplayMode(DisplayMode mode)
        {
            var targetMode = new SDL.SDL_DisplayMode { w = mode.Size.Width, h = mode.Size.Height, refresh_rate = mode.RefreshRate };
            SDL.SDL_GetClosestDisplayMode(windowDisplayIndex, ref targetMode, out var closest);
            return closest;
        }

        private static Display displayFromSDL(int displayIndex)
        {
            var displayModes = Enumerable.Range(0, SDL.SDL_GetNumDisplayModes(displayIndex))
                                         .Select(modeIndex =>
                                         {
                                             SDL.SDL_GetDisplayMode(displayIndex, modeIndex, out var mode);
                                             return displayModeFromSDL(mode, displayIndex, modeIndex);
                                         })
                                         .ToArray();

            SDL.SDL_GetDisplayBounds(displayIndex, out var rect);
            return new Display(displayIndex, SDL.SDL_GetDisplayName(displayIndex), new Rectangle(rect.x, rect.y, rect.w, rect.h), displayModes);
        }

        private static DisplayMode displayModeFromSDL(SDL.SDL_DisplayMode mode, int displayIndex, int modeIndex)
        {
            SDL.SDL_PixelFormatEnumToMasks(mode.format, out var bpp, out _, out _, out _, out _);
            return new DisplayMode(SDL.SDL_GetPixelFormatName(mode.format), new Size(mode.w, mode.h), bpp, mode.refresh_rate, modeIndex, displayIndex);
        }

        private void enqueueJoystickAxisInput(int instanceID, JoystickAxisSource axisSource, short axisValue)
        {
            var clamped = Math.Clamp((float)axisValue / short.MaxValue, -1f, 1f);
            var value = Math.Abs(clamped) < deadzone_threshold ? 0 : Math.Sign(clamped) * (Math.Abs(clamped) - deadzone_threshold) / (1f - deadzone_threshold);

            if (axisDirectionButtons.ContainsKey(instanceID))
            {
                var directionButton = axisDirectionButtons[instanceID][(int)axisSource];
                var negativeButton = JoystickButton.FirstAxisNegative + (int)axisSource;
                var positiveButton = JoystickButton.FirstAxisPositive + (int)axisSource;

                if (value == 0 && directionButton != 0 || value < 0 && directionButton == positiveButton || value > 0 && directionButton == negativeButton)
                {
                    eventScheduler.Add(() => OnJoystickButtonUp(new JoystickButtonInput(directionButton, false)));
                    axisDirectionButtons[instanceID][(int)axisSource] = 0;
                }

                if (value < 0 && directionButton != negativeButton)
                {
                    eventScheduler.Add(() => OnJoystickButtonDown(new JoystickButtonInput(negativeButton, true)));
                    axisDirectionButtons[instanceID][(int)axisSource] = negativeButton;
                }
                else if (value > 0 && directionButton != positiveButton)
                {
                    eventScheduler.Add(() => OnJoystickButtonDown(new JoystickButtonInput(positiveButton, true)));
                    axisDirectionButtons[instanceID][(int)axisSource] = positiveButton;
                }
            }

            eventScheduler.Add(() => OnJoystickAxisChanged(new JoystickAxisInput(new JoystickAxis(axisSource, value))));
        }

        #endregion

        public Sdl2WindowBackend()
        {
            SDL.SDL_Init(SDL.SDL_INIT_VIDEO | SDL.SDL_INIT_GAMECONTROLLER);
        }

        #region IWindowBackend.Methods

        public override void Create()
        {
            SDL.SDL_WindowFlags flags = SDL.SDL_WindowFlags.SDL_WINDOW_OPENGL |
                                        SDL.SDL_WindowFlags.SDL_WINDOW_RESIZABLE |
                                        SDL.SDL_WindowFlags.SDL_WINDOW_ALLOW_HIGHDPI |
                                        WindowState.ToFlags();

            SdlWindowHandle = SDL.SDL_CreateWindow($"{title} (SDL)", Position.X, Position.Y, Size.Width, Size.Height, flags);
            cachedScale.Invalidate();
            Exists = true;
        }

        public override void Run()
        {
            while (Exists)
            {
                commandScheduler.Update();

                processEvents();

                if (!mouseInWindow)
                    pollMouse();

                eventScheduler.Update();

                OnUpdate();
            }

            if (SdlWindowHandle != IntPtr.Zero)
                SDL.SDL_DestroyWindow(SdlWindowHandle);

            OnClosed();

            SDL.SDL_Quit();
        }

        public override void Close() => commandScheduler.Add(() => Exists = false);

        private void pollMouse()
        {
            SDL.SDL_GetGlobalMouseState(out var x, out var y);
            if (previousPolledPoint.X == x && previousPolledPoint.Y == y)
                return;

            previousPolledPoint = new Point(x, y);

            var pos = windowFlags.ToWindowState() == WindowState.Normal ? Position : windowDisplayBounds.Location;
            var rx = x - pos.X;
            var ry = y - pos.Y;

            eventScheduler.Add(() => OnMouseMove(new MousePositionAbsoluteInput { Position = new Vector2(rx * scale, ry * scale) }));
        }

        #endregion

        #region SDL Event Handling

        private void processEvents()
        {
            while (SDL.SDL_PollEvent(out var evt) > 0)
            {
                switch (evt.type)
                {
                    case SDL.SDL_EventType.SDL_QUIT:
                    case SDL.SDL_EventType.SDL_APP_TERMINATING:
                        handleQuitEvent(evt.quit);
                        break;

                    case SDL.SDL_EventType.SDL_WINDOWEVENT:
                        handleWindowEvent(evt.window);
                        break;

                    case SDL.SDL_EventType.SDL_KEYDOWN:
                    case SDL.SDL_EventType.SDL_KEYUP:
                        handleKeyboardEvent(evt.key);
                        break;

                    case SDL.SDL_EventType.SDL_TEXTEDITING:
                        handleTextEditingEvent(evt.edit);
                        break;

                    case SDL.SDL_EventType.SDL_TEXTINPUT:
                        handleTextInputEvent(evt.text);
                        break;

                    case SDL.SDL_EventType.SDL_MOUSEMOTION:
                        handleMouseMotionEvent(evt.motion);
                        break;

                    case SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN:
                    case SDL.SDL_EventType.SDL_MOUSEBUTTONUP:
                        handleMouseButtonEvent(evt.button);
                        break;

                    case SDL.SDL_EventType.SDL_MOUSEWHEEL:
                        handleMouseWheelEvent(evt.wheel);
                        break;

                    case SDL.SDL_EventType.SDL_JOYAXISMOTION:
                        handleJoyAxisEvent(evt.jaxis);
                        break;

                    case SDL.SDL_EventType.SDL_JOYBALLMOTION:
                        handleJoyBallEvent(evt.jball);
                        break;

                    case SDL.SDL_EventType.SDL_JOYHATMOTION:
                        handleJoyHatEvent(evt.jhat);
                        break;

                    case SDL.SDL_EventType.SDL_JOYBUTTONDOWN:
                    case SDL.SDL_EventType.SDL_JOYBUTTONUP:
                        handleJoyButtonEvent(evt.jbutton);
                        break;

                    case SDL.SDL_EventType.SDL_JOYDEVICEADDED:
                    case SDL.SDL_EventType.SDL_JOYDEVICEREMOVED:
                        handleJoyDeviceEvent(evt.jdevice);
                        break;

                    case SDL.SDL_EventType.SDL_CONTROLLERAXISMOTION:
                        handleControllerAxisEvent(evt.caxis);
                        break;

                    case SDL.SDL_EventType.SDL_CONTROLLERBUTTONDOWN:
                    case SDL.SDL_EventType.SDL_CONTROLLERBUTTONUP:
                        handleControllerButtonEvent(evt.cbutton);
                        break;

                    case SDL.SDL_EventType.SDL_CONTROLLERDEVICEADDED:
                    case SDL.SDL_EventType.SDL_CONTROLLERDEVICEREMOVED:
                    case SDL.SDL_EventType.SDL_CONTROLLERDEVICEREMAPPED:
                        handleControllerDeviceEvent(evt.cdevice);
                        break;

                    case SDL.SDL_EventType.SDL_FINGERDOWN:
                    case SDL.SDL_EventType.SDL_FINGERUP:
                    case SDL.SDL_EventType.SDL_FINGERMOTION:
                        handleTouchFingerEvent(evt.tfinger);
                        break;

                    case SDL.SDL_EventType.SDL_DROPFILE:
                    case SDL.SDL_EventType.SDL_DROPTEXT:
                    case SDL.SDL_EventType.SDL_DROPBEGIN:
                    case SDL.SDL_EventType.SDL_DROPCOMPLETE:
                        handleDropEvent(evt.drop);
                        break;
                }
            }
        }

        private void handleQuitEvent(SDL.SDL_QuitEvent evtQuit)
        {
            // TODO: handle OnCloseRequested()
            // we currently have a deadlock issue where GameHost blocks
            Exists = false;
        }

        private void handleDropEvent(SDL.SDL_DropEvent evtDrop)
        {
            switch (evtDrop.type)
            {
                case SDL.SDL_EventType.SDL_DROPFILE:
                    var str = SDL.UTF8_ToManaged(evtDrop.file, true);
                    if (str != null)
                        eventScheduler.Add(() => OnDragDrop(str));

                    break;
            }
        }

        private void handleTouchFingerEvent(SDL.SDL_TouchFingerEvent evtTfinger)
        {
        }

        private void handleControllerDeviceEvent(SDL.SDL_ControllerDeviceEvent evtCdevice)
        {
            switch (evtCdevice.type)
            {
                case SDL.SDL_EventType.SDL_CONTROLLERDEVICEADDED:
                    var controller = SDL.SDL_GameControllerOpen(evtCdevice.which);
                    var instanceID = SDL.SDL_JoystickGetDeviceInstanceID(evtCdevice.which);
                    axisDirectionButtons[instanceID] = new JoystickButton[(int)JoystickAxisSource.AxisCount];
                    joysticks[instanceID] = controller;
                    bindings[instanceID] = new ControllerBindings(controller);
                    break;

                case SDL.SDL_EventType.SDL_CONTROLLERDEVICEREMOVED:
                    SDL.SDL_GameControllerClose(joysticks[evtCdevice.which]);
                    bindings.Remove(evtCdevice.which);
                    axisDirectionButtons.Remove(evtCdevice.which);
                    joysticks.Remove(evtCdevice.which);
                    break;

                case SDL.SDL_EventType.SDL_CONTROLLERDEVICEREMAPPED:
                    bindings[evtCdevice.which] = new ControllerBindings(joysticks[evtCdevice.which]);
                    break;
            }
        }

        private void handleControllerButtonEvent(SDL.SDL_ControllerButtonEvent evtCbutton)
        {
            var button = joystickButtonFromGameController((SDL.SDL_GameControllerButton)evtCbutton.button);

            switch (evtCbutton.type)
            {
                case SDL.SDL_EventType.SDL_CONTROLLERBUTTONDOWN:
                    eventScheduler.Add(() => OnJoystickButtonDown(new JoystickButtonInput(button, true)));
                    break;

                case SDL.SDL_EventType.SDL_CONTROLLERBUTTONUP:
                    eventScheduler.Add(() => OnJoystickButtonUp(new JoystickButtonInput(button, false)));
                    break;
            }
        }

        private void handleControllerAxisEvent(SDL.SDL_ControllerAxisEvent evtCaxis) =>
            enqueueJoystickAxisInput(evtCaxis.which, joystickAxisSourceFromEvent((SDL.SDL_GameControllerAxis)evtCaxis.axis), evtCaxis.axisValue);

        private void handleJoyDeviceEvent(SDL.SDL_JoyDeviceEvent evtJdevice)
        {
            switch (evtJdevice.type)
            {
                case SDL.SDL_EventType.SDL_JOYDEVICEADDED:
                    var instanceID = SDL.SDL_JoystickGetDeviceInstanceID(evtJdevice.which);

                    // if the joystick is already opened, ignore it
                    if (joysticks.ContainsKey(instanceID))
                        break;

                    var joystick = SDL.SDL_JoystickOpen(evtJdevice.which);
                    axisDirectionButtons[instanceID] = new JoystickButton[(int)JoystickAxisSource.AxisCount];
                    joysticks[instanceID] = joystick;
                    break;

                case SDL.SDL_EventType.SDL_JOYDEVICEREMOVED:
                    // if the joystick is already closed, ignore it
                    if (!joysticks.ContainsKey(evtJdevice.which))
                        break;

                    SDL.SDL_JoystickClose(joysticks[evtJdevice.which]);
                    axisDirectionButtons.Remove(evtJdevice.which);
                    joysticks.Remove(evtJdevice.which);
                    break;
            }
        }

        private void handleJoyButtonEvent(SDL.SDL_JoyButtonEvent evtJbutton)
        {
            // if we have bindings, ignore all other button presses
            if (bindings.ContainsKey(evtJbutton.which))
                return;

            var button = JoystickButton.FirstButton + evtJbutton.button;

            switch (evtJbutton.type)
            {
                case SDL.SDL_EventType.SDL_JOYBUTTONDOWN:
                    eventScheduler.Add(() => OnJoystickButtonDown(new JoystickButtonInput(button, true)));
                    break;

                case SDL.SDL_EventType.SDL_JOYBUTTONUP:
                    eventScheduler.Add(() => OnJoystickButtonUp(new JoystickButtonInput(button, false)));
                    break;
            }
        }

        private void handleJoyHatEvent(SDL.SDL_JoyHatEvent evtJhat)
        {
        }

        private void handleJoyBallEvent(SDL.SDL_JoyBallEvent evtJball)
        {
        }

        private void handleJoyAxisEvent(SDL.SDL_JoyAxisEvent evtJaxis)
        {
            // if this axis exists in the controller bindings, skip it
            if (bindings.ContainsKey(evtJaxis.which) && bindings[evtJaxis.which].GetAxisForIndex(evtJaxis.axis) != SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_INVALID)
                return;

            enqueueJoystickAxisInput(evtJaxis.which, JoystickAxisSource.Axis1 + evtJaxis.axis, evtJaxis.axisValue);
        }

        private void handleMouseWheelEvent(SDL.SDL_MouseWheelEvent evtWheel) =>
            eventScheduler.Add(() => OnMouseWheel(new MouseScrollRelativeInput { Delta = new Vector2(evtWheel.x, evtWheel.y) }));

        private void handleMouseButtonEvent(SDL.SDL_MouseButtonEvent evtButton)
        {
            MouseButton button = mouseButtonFromEvent(evtButton.button);

            switch (evtButton.type)
            {
                case SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN:
                    eventScheduler.Add(() => OnMouseDown(new MouseButtonInput(button, true)));
                    break;

                case SDL.SDL_EventType.SDL_MOUSEBUTTONUP:
                    eventScheduler.Add(() => OnMouseUp(new MouseButtonInput(button, false)));
                    break;
            }
        }

        private void handleMouseMotionEvent(SDL.SDL_MouseMotionEvent evtMotion) =>
            eventScheduler.Add(() => OnMouseMove(new MousePositionAbsoluteInput { Position = new Vector2(evtMotion.x * scale, evtMotion.y * scale) }));

        private unsafe void handleTextInputEvent(SDL.SDL_TextInputEvent evtText)
        {
            var ptr = new IntPtr(evtText.text);
            if (ptr == IntPtr.Zero)
                return;

            string text = Marshal.PtrToStringAnsi(ptr) ?? "";

            foreach (char c in text)
                eventScheduler.Add(() => OnKeyTyped(c));
        }

        private void handleTextEditingEvent(SDL.SDL_TextEditingEvent evtEdit)
        {
        }

        private void handleKeyboardEvent(SDL.SDL_KeyboardEvent evtKey)
        {
            Key key = evtKey.keysym.ToKey();

            if (key == Key.Unknown || key == Key.CapsLock)
                return;

            switch (evtKey.type)
            {
                case SDL.SDL_EventType.SDL_KEYDOWN:
                    eventScheduler.Add(() => OnKeyDown(new KeyboardKeyInput(key, true)));
                    break;

                case SDL.SDL_EventType.SDL_KEYUP:
                    eventScheduler.Add(() => OnKeyUp(new KeyboardKeyInput(key, false)));
                    break;
            }
        }

        private void handleWindowEvent(SDL.SDL_WindowEvent evtWindow)
        {
            var currentState = windowFlags.ToWindowState();
            var displayIndex = windowDisplayIndex;

            if (lastWindowState != currentState)
            {
                lastWindowState = currentState;
                cachedScale.Invalidate();
                eventScheduler.Add(() => OnWindowStateChanged(currentState));
            }

            if (lastDisplayIndex != displayIndex)
            {
                lastDisplayIndex = displayIndex;
                currentDisplay = null;
                cachedScale.Invalidate();
                eventScheduler.Add(() => OnDisplayChanged(Displays.ElementAtOrDefault(displayIndex) ?? PrimaryDisplay));
            }

            switch (evtWindow.windowEvent)
            {
                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_SHOWN:
                    eventScheduler.Add(OnShown);
                    break;

                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_HIDDEN:
                    eventScheduler.Add(OnHidden);
                    break;

                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_MOVED:
                    var eventPos = new Point(evtWindow.data1, evtWindow.data2);

                    if (currentState == WindowState.Normal && !eventPos.Equals(position))
                    {
                        position = eventPos;
                        cachedScale.Invalidate();
                        eventScheduler.Add(() => OnMoved(eventPos));
                    }

                    break;

                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_SIZE_CHANGED:
                    var eventSize = new Size(evtWindow.data1, evtWindow.data2);

                    if (currentState == WindowState.Normal && !eventSize.Equals(size))
                    {
                        size = eventSize;
                        cachedScale.Invalidate();
                        eventScheduler.Add(() => OnResized(eventSize));
                    }

                    break;

                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_MINIMIZED:
                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_MAXIMIZED:
                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_RESTORED:
                    break;

                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_ENTER:
                    mouseInWindow = true;
                    eventScheduler.Add(OnMouseEntered);
                    break;

                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_LEAVE:
                    mouseInWindow = false;
                    eventScheduler.Add(OnMouseLeft);
                    break;

                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_FOCUS_GAINED:
                    eventScheduler.Add(OnFocusGained);
                    break;

                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_FOCUS_LOST:
                    eventScheduler.Add(OnFocusLost);
                    break;

                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_CLOSE:
                    eventScheduler.Add(OnClosed);
                    break;
            }
        }

        private MouseButton mouseButtonFromEvent(byte button)
        {
            switch ((uint)button)
            {
                default:
                case SDL.SDL_BUTTON_LEFT:
                    return MouseButton.Left;

                case SDL.SDL_BUTTON_RIGHT:
                    return MouseButton.Right;

                case SDL.SDL_BUTTON_MIDDLE:
                    return MouseButton.Middle;

                case SDL.SDL_BUTTON_X1:
                    return MouseButton.Button1;

                case SDL.SDL_BUTTON_X2:
                    return MouseButton.Button2;
            }
        }

        private JoystickButton joystickButtonFromGameController(SDL.SDL_GameControllerButton button)
        {
            switch (button)
            {
                default:
                case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_INVALID:
                    return 0;

                case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_A:
                    return JoystickButton.GamePadA;

                case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_B:
                    return JoystickButton.GamePadB;

                case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_X:
                    return JoystickButton.GamePadX;

                case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_Y:
                    return JoystickButton.GamePadY;

                case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_BACK:
                    return JoystickButton.GamePadBack;

                case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_GUIDE:
                    return JoystickButton.GamePadGuide;

                case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_START:
                    return JoystickButton.GamePadStart;

                case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_LEFTSTICK:
                    return JoystickButton.GamePadLeftStick;

                case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_RIGHTSTICK:
                    return JoystickButton.GamePadRightStick;

                case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_LEFTSHOULDER:
                    return JoystickButton.GamePadLeftShoulder;

                case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_RIGHTSHOULDER:
                    return JoystickButton.GamePadRightShoulder;

                case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_UP:
                    return JoystickButton.GamePadDPadUp;

                case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_DOWN:
                    return JoystickButton.GamePadDPadDown;

                case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_LEFT:
                    return JoystickButton.GamePadDPadLeft;

                case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_RIGHT:
                    return JoystickButton.GamePadDPadRight;
            }
        }

        private JoystickAxisSource joystickAxisSourceFromEvent(SDL.SDL_GameControllerAxis axis)
        {
            switch (axis)
            {
                default:
                case SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_INVALID:
                    return 0;

                case SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTX:
                    return JoystickAxisSource.Axis1;

                case SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTY:
                    return JoystickAxisSource.Axis2;

                case SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_TRIGGERLEFT:
                    return JoystickAxisSource.Axis3;

                case SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_RIGHTX:
                    return JoystickAxisSource.Axis4;

                case SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_RIGHTY:
                    return JoystickAxisSource.Axis5;

                case SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_TRIGGERRIGHT:
                    return JoystickAxisSource.Axis6;
            }
        }

        /// <summary>
        /// Keeps a lookup of button/axis bindings as defined by the SDL GameController API.
        /// Primarily used for reverse lookups to determine whether a physical button has no logical mapping.
        /// </summary>
        protected readonly struct ControllerBindings
        {
            public readonly SDL.SDL_GameControllerButtonBind[] ButtonBindings;
            public readonly SDL.SDL_GameControllerButtonBind[] AxisBindings;

            public SDL.SDL_GameControllerButton GetButtonForIndex(byte index)
            {
                for (var i = 0; i < ButtonBindings.Length; i++)
                {
                    if (ButtonBindings[i].bindType != SDL.SDL_GameControllerBindType.SDL_CONTROLLER_BINDTYPE_NONE && ButtonBindings[i].value.button == index)
                        return (SDL.SDL_GameControllerButton)i;
                }

                return SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_INVALID;
            }

            public SDL.SDL_GameControllerAxis GetAxisForIndex(byte index)
            {
                for (var i = 0; i < AxisBindings.Length; i++)
                {
                    if (AxisBindings[i].bindType != SDL.SDL_GameControllerBindType.SDL_CONTROLLER_BINDTYPE_NONE && AxisBindings[i].value.button == index)
                        return (SDL.SDL_GameControllerAxis)i;
                }

                return SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_INVALID;
            }

            public ControllerBindings(IntPtr controller)
            {
                ButtonBindings = Enumerable.Range(0, (int)SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_MAX)
                                           .Select(i => SDL.SDL_GameControllerGetBindForButton(controller, (SDL.SDL_GameControllerButton)i)).ToArray();

                AxisBindings = Enumerable.Range(0, (int)SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_MAX)
                                         .Select(i => SDL.SDL_GameControllerGetBindForAxis(controller, (SDL.SDL_GameControllerAxis)i)).ToArray();
            }
        }

        #endregion
    }
}
