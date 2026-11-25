namespace MyRemote.Input;

public enum InputEventType
{
    MouseMove = 0,
    MouseDown = 1,
    MouseUp = 2,
    MouseWheel = 3,
    KeyDown = 4,
    KeyUp = 5
}

public enum RemoteMouseButton
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3
}

public sealed record RemoteInputEvent(
    InputEventType Type,
    double NormalizedX = 0,
    double NormalizedY = 0,
    RemoteMouseButton Button = RemoteMouseButton.None,
    int WheelDelta = 0,
    ushort VirtualKey = 0,
    int MonitorLeft = 0,
    int MonitorTop = 0,
    int MonitorWidth = 0,
    int MonitorHeight = 0);

public interface IInputInjector
{
    bool TryInject(RemoteInputEvent inputEvent, string? sessionToken);
}
