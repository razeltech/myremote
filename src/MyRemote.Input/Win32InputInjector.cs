using System.Runtime.InteropServices;
using MyRemote.Core.Interfaces;
using MyRemote.Core.Models;

namespace MyRemote.Input;

/// <summary>
/// Enforces session authorization and injects validated mouse/keyboard events via Win32 SendInput.
/// </summary>
public sealed class Win32InputInjector(ISessionManager sessionManager, ISecurityService securityService) : IInputInjector
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseeventfMove = 0x0001;
    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    private const uint MouseeventfRightdown = 0x0008;
    private const uint MouseeventfRightup = 0x0010;
    private const uint MouseeventfMiddledown = 0x0020;
    private const uint MouseeventfMiddleup = 0x0040;
    private const uint MouseeventfWheel = 0x0800;
    private const uint MouseeventfVirtualdesk = 0x4000;
    private const uint MouseeventfAbsolute = 0x8000;

    private const uint KeyeventfKeyup = 0x0002;

    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;

    public bool TryInject(RemoteInputEvent inputEvent, string? sessionToken)
    {
        var session = sessionManager.CurrentSession;
        if (session is null || !securityService.ValidateSessionToken(session.SessionToken, sessionToken))
        {
            return false;
        }

        return inputEvent.Type switch
        {
            InputEventType.MouseMove or InputEventType.MouseDown or InputEventType.MouseUp or InputEventType.MouseWheel
                => sessionManager.HasPermission(SessionPermissions.MouseInput) && InjectMouse(inputEvent),
            InputEventType.KeyDown or InputEventType.KeyUp
                => sessionManager.HasPermission(SessionPermissions.KeyboardInput) && InjectKeyboard(inputEvent),
            _ => false
        };
    }

    private static bool InjectMouse(RemoteInputEvent e)
    {
        if (double.IsNaN(e.NormalizedX) || double.IsNaN(e.NormalizedY)) return false;

        double nx = Math.Clamp(e.NormalizedX, 0.0, 1.0);
        double ny = Math.Clamp(e.NormalizedY, 0.0, 1.0);

        int vLeft = GetSystemMetrics(SmXvirtualscreen);
        int vTop = GetSystemMetrics(SmYvirtualscreen);
        int vWidth = Math.Max(1, GetSystemMetrics(SmCxvirtualscreen));
        int vHeight = Math.Max(1, GetSystemMetrics(SmCyvirtualscreen));

        int monLeft = e.MonitorWidth > 0 ? e.MonitorLeft : vLeft;
        int monTop = e.MonitorHeight > 0 ? e.MonitorTop : vTop;
        int monWidth = e.MonitorWidth > 0 ? e.MonitorWidth : vWidth;
        int monHeight = e.MonitorHeight > 0 ? e.MonitorHeight : vHeight;

        double absPixelX = monLeft + nx * monWidth;
        double absPixelY = monTop + ny * monHeight;

        int dx = (int)Math.Round(((absPixelX - vLeft) * 65535.0) / Math.Max(1, vWidth - 1));
        int dy = (int)Math.Round(((absPixelY - vTop) * 65535.0) / Math.Max(1, vHeight - 1));

        uint flags = MouseeventfAbsolute | MouseeventfVirtualdesk | MouseeventfMove;
        uint mouseData = 0;

        switch (e.Type)
        {
            case InputEventType.MouseDown:
                flags |= e.Button switch
                {
                    RemoteMouseButton.Left => MouseeventfLeftdown,
                    RemoteMouseButton.Right => MouseeventfRightdown,
                    RemoteMouseButton.Middle => MouseeventfMiddledown,
                    _ => 0u
                };
                break;
            case InputEventType.MouseUp:
                flags |= e.Button switch
                {
                    RemoteMouseButton.Left => MouseeventfLeftup,
                    RemoteMouseButton.Right => MouseeventfRightup,
                    RemoteMouseButton.Middle => MouseeventfMiddleup,
                    _ => 0u
                };
                break;
            case InputEventType.MouseWheel:
                flags |= MouseeventfWheel;
                mouseData = unchecked((uint)Math.Clamp(e.WheelDelta, -1200, 1200));
                break;
        }

        var input = new InputStruct
        {
            type = InputMouse,
            u = new InputUnion
            {
                mi = new MouseInput
                {
                    dx = Math.Clamp(dx, 0, 65535),
                    dy = Math.Clamp(dy, 0, 65535),
                    mouseData = mouseData,
                    dwFlags = flags
                }
            }
        };

        return SendInput(1, [input], Marshal.SizeOf<InputStruct>()) == 1;
    }

    private static bool InjectKeyboard(RemoteInputEvent e)
    {
        if (e.VirtualKey == 0 || e.VirtualKey > 0xFE) return false;

        var input = new InputStruct
        {
            type = InputKeyboard,
            u = new InputUnion
            {
                ki = new KeybdInput
                {
                    wVk = e.VirtualKey,
                    dwFlags = e.Type == InputEventType.KeyUp ? KeyeventfKeyup : 0u
                }
            }
        };

        return SendInput(1, [input], Marshal.SizeOf<InputStruct>()) == 1;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, InputStruct[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct InputStruct
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput mi;
        [FieldOffset(0)] public KeybdInput ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
