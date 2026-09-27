using System;
using System.Runtime.InteropServices;
using UnityEngine;

// Free-fly camera matching the interaction model of the old WPF preview:
// left button or WASD enters free look (cursor hidden; rotation reads raw-input
// deltas via Locked mode, while the cursor itself is warped back to the viewport
// center every frame with SetCursorPos because the lock cannot confine it in an
// embedded -parentHWND player), mouse rotates, WASD moves, Shift is faster,
// wheel steps forward/back, right button or Esc releases the cursor, R resets.
internal sealed class FlyCamera : MonoBehaviour
{
    private const float MouseSensitivity = 2.0f;
    private const float MoveSpeed = 3.6f;
    private const float FastMoveMultiplier = 3.0f;
    private const float WheelMoveStep = 0.55f;
    private const float MinPitch = -89f;
    private const float MaxPitch = 89f;

    private Vector3 _homePosition;
    private Quaternion _homeRotation;
    private float _yaw;
    private float _pitch;
    private bool _freeLookActive;
    private IntPtr _windowHandle;
    private bool _windowHandleSearched;

    public void SetHome(Vector3 position, Quaternion rotation)
    {
        _homePosition = position;
        _homeRotation = rotation;
        ResetView();
    }

    private void Update()
    {
        if (Input.GetMouseButtonDown(0) || WasdPressed())
        {
            BeginFreeLook();
        }

        if (Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.Escape))
        {
            EndFreeLook();
        }

        if (Input.GetKeyDown(KeyCode.R))
        {
            ResetView();
        }

        if (!_freeLookActive)
        {
            return;
        }

        // Locked mode feeds mouse deltas from raw input, which works in the embedded
        // window (Input.mousePosition does not update there). The lock itself cannot
        // confine the cursor in an embedded window, so WarpCursorToCenter does that.
        _yaw += Input.GetAxis("Mouse X") * MouseSensitivity;
        _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * MouseSensitivity, MinPitch, MaxPitch);
        transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        WarpCursorToCenter();

        Vector3 movement = Vector3.zero;
        if (Input.GetKey(KeyCode.W))
        {
            movement += transform.forward;
        }

        if (Input.GetKey(KeyCode.S))
        {
            movement -= transform.forward;
        }

        if (Input.GetKey(KeyCode.D))
        {
            movement += transform.right;
        }

        if (Input.GetKey(KeyCode.A))
        {
            movement -= transform.right;
        }

        if (movement.sqrMagnitude > 0f)
        {
            bool fast = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float speed = fast ? MoveSpeed * FastMoveMultiplier : MoveSpeed;
            transform.position += movement.normalized * (speed * Time.deltaTime);
        }

        float scroll = Input.mouseScrollDelta.y;
        if (Mathf.Abs(scroll) > 0.001f)
        {
            transform.position += transform.forward * (Mathf.Sign(scroll) * WheelMoveStep);
        }
    }

    private void ResetView()
    {
        transform.position = _homePosition;
        transform.rotation = _homeRotation;
        Vector3 euler = _homeRotation.eulerAngles;
        _pitch = euler.x > 180f ? euler.x - 360f : euler.x;
        _yaw = euler.y;
    }

    private void BeginFreeLook()
    {
        if (_freeLookActive)
        {
            return;
        }

        _freeLookActive = true;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        WarpCursorToCenter();
    }

    private void EndFreeLook()
    {
        if (!_freeLookActive)
        {
            return;
        }

        _freeLookActive = false;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private void WarpCursorToCenter()
    {
        IntPtr windowHandle = GetGameWindowHandle();
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        var point = new NativePoint { X = Screen.width / 2, Y = Screen.height / 2 };
        if (ClientToScreen(windowHandle, ref point))
        {
            SetCursorPos(point.X, point.Y);
        }
    }

    private IntPtr GetGameWindowHandle()
    {
        if (_windowHandleSearched)
        {
            return _windowHandle;
        }

        _windowHandleSearched = true;
        _windowHandle = FindEmbeddedGameWindow();
        return _windowHandle;
    }

    private static IntPtr FindEmbeddedGameWindow()
    {
        IntPtr parentHandle = GetParentHandleFromArgs();
        if (parentHandle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr found = IntPtr.Zero;
        uint processId = GetCurrentProcessId();
        EnumChildWindows(parentHandle, (childHandle, _) =>
        {
            GetWindowThreadProcessId(childHandle, out uint childProcessId);
            if (childProcessId == processId)
            {
                found = childHandle;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static IntPtr GetParentHandleFromArgs()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "-parentHWND", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(args[i + 1], out long value) && value != 0)
            {
                return new IntPtr(value);
            }
        }

        return IntPtr.Zero;
    }

    private static bool WasdPressed()
    {
        return Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.A) ||
               Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.D);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private delegate bool EnumChildWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parentHandle, EnumChildWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();
}
