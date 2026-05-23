using System.Runtime.InteropServices;

namespace QemuGaGuard;

/// <summary>
/// Simulates organic, human-like mouse movement with a dual-backend architecture:
///
///   PRIMARY: Logitech G HUB virtual mouse driver (kernel-level).
///     Opens a handle to the Logitech virtual bus enumerator device and sends IOCTL
///     requests (0x2a2010) directly to the kernel driver. Input injected this way
///     travels through the HID stack exactly like physical hardware — the OS never
///     sets the LLMHF_INJECTED flag, making it indistinguishable from a real mouse.
///
///   FALLBACK: Win32 SendInput with MOUSEEVENTF_MOVE (relative).
///     Used when Logitech G HUB is not installed or the driver handle cannot be opened.
///     Still uses relative movement and all the same Bezier/noise/timing techniques.
///
/// Anti-detection techniques applied to BOTH backends:
///   - Cubic Bezier curve interpolation for smooth, organic arcs.
///   - Sub-pixel micro-noise (+/- 1-2px jitter) simulating hand tremor.
///   - Gaussian-distributed step delays (Box-Muller) with timeBeginPeriod(1).
///   - Smoothstep ease-in/ease-out mimicking natural acceleration.
///   - Macro-pauses (1-4 min, 2.5% probability) simulating real breaks.
///   - Human-like click with Gaussian press-release duration (60-130ms).
///   - Destination jitter (±5-10px) preventing rigid target patterns.
/// </summary>
public static class HumanMouseMover
{
    private static CancellationTokenSource? _activeCts;
    private static Task? _activeLoop;
    private static readonly object _lock = new();

    /// <summary>Tracks completed movements since loop started.</summary>
    public static long MoveCount { get; private set; }

    /// <summary>Which input backend is currently active.</summary>
    public static string ActiveBackend { get; private set; } = "None";

    /// <summary>Real-time status of the current movement operation.</summary>
    public static string MovementStatus { get; private set; } = "idle";

    public static bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _activeLoop is not null && !_activeLoop.IsCompleted;
            }
        }
    }

    public static void Start()
    {
        lock (_lock)
        {
            if (_activeLoop is not null && !_activeLoop.IsCompleted)
            {
                return;
            }

            MoveCount = 0;
            ActiveBackend = "Initializing";
            MovementStatus = "starting...";
            _activeCts = new CancellationTokenSource();
            var token = _activeCts.Token;
            _activeLoop = Task.Run(() => MoveLoopAsync(token), token);
        }
    }

    public static async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;

        lock (_lock)
        {
            cts = _activeCts;
            loop = _activeLoop;
            _activeCts = null;
            _activeLoop = null;
        }

        if (cts is not null)
        {
            await cts.CancelAsync();
        }

        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation.
            }
        }

        cts?.Dispose();
        ActiveBackend = "None";
        MovementStatus = "idle";
    }

    // ═══════════════════════════════════════════════════════════════
    //  LOGITECH DRIVER BACKEND
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Handle to the Logitech G HUB virtual bus enumerator device.
    /// When non-null, we send IOCTL 0x2a2010 with a 5-byte MOUSE_IO struct
    /// to inject input at the kernel/HID level.
    /// </summary>
    private static IntPtr _logiHandle = IntPtr.Zero;

    /// <summary>
    /// 5-byte structure matching the Logitech driver's expected input buffer.
    /// Byte layout: [button, x, y, wheel, reserved]
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct MOUSE_IO
    {
        public byte button;
        public byte x;
        public byte y;
        public byte wheel;
        public byte unk1;
    }

    /// <summary>The IOCTL code the Logitech driver expects for mouse input.</summary>
    private const uint LOGI_IOCTL_MOUSE = 0x2a2010;

    /// <summary>
    /// Device interface GUID for the Logitech virtual bus enumerator.
    /// Full symlink paths tried in order (ROOT#SYSTEM#0002, then #0001).
    /// </summary>
    private static readonly string[] LogiDevicePaths =
    [
        @"\\?\ROOT#SYSTEM#0002#{1abc05c0-c378-41b9-9cef-df1aba82b015}",
        @"\\?\ROOT#SYSTEM#0001#{1abc05c0-c378-41b9-9cef-df1aba82b015}"
    ];

    private static bool TryOpenLogitechDriver()
    {
        foreach (var path in LogiDevicePaths)
        {
            var handle = CreateFileW(
                path,
                GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle != IntPtr.Zero && handle != INVALID_HANDLE_VALUE)
            {
                _logiHandle = handle;
                return true;
            }
        }

        return false;
    }

    private static void CloseLogitechDriver()
    {
        if (_logiHandle != IntPtr.Zero && _logiHandle != INVALID_HANDLE_VALUE)
        {
            CloseHandle(_logiHandle);
            _logiHandle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Sends a mouse input packet to the Logitech kernel driver via DeviceIoControl.
    /// Returns true if the IOCTL succeeded.
    /// </summary>
    private static bool LogiMouseIo(byte button, sbyte x, sbyte y, sbyte wheel = 0)
    {
        if (_logiHandle == IntPtr.Zero || _logiHandle == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        var io = new MOUSE_IO
        {
            button = button,
            x = unchecked((byte)x),
            y = unchecked((byte)y),
            wheel = unchecked((byte)wheel),
            unk1 = 0
        };

        var success = DeviceIoControl(
            _logiHandle,
            LOGI_IOCTL_MOUSE,
            ref io,
            (uint)Marshal.SizeOf<MOUSE_IO>(),
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero);

        if (!success)
        {
            // Driver may have been unloaded/restarted — try to reconnect once.
            CloseLogitechDriver();
            if (TryOpenLogitechDriver())
            {
                success = DeviceIoControl(
                    _logiHandle,
                    LOGI_IOCTL_MOUSE,
                    ref io,
                    (uint)Marshal.SizeOf<MOUSE_IO>(),
                    IntPtr.Zero,
                    0,
                    out _,
                    IntPtr.Zero);
            }
        }

        return success;
    }

    // ═══════════════════════════════════════════════════════════════
    //  MOVEMENT DISPATCH (dual-backend)
    // ═══════════════════════════════════════════════════════════════

    private static bool _useLogitech;

    /// <summary>
    /// Moves the mouse by (dx, dy) using whichever backend is active.
    /// The Logitech IOCTL uses signed bytes (-128..127), so large deltas are
    /// split into multiple small packets.
    /// </summary>
    private static void MoveRelative(int dx, int dy)
    {
        if (_useLogitech)
        {
            // Logitech driver takes signed byte per axis (-128..+127).
            // Split large movements into multiple IOCTL calls.
            while (dx != 0 || dy != 0)
            {
                var stepX = (sbyte)Math.Clamp(dx, -127, 127);
                var stepY = (sbyte)Math.Clamp(dy, -127, 127);
                LogiMouseIo(0, stepX, stepY);
                dx -= stepX;
                dy -= stepY;
            }
        }
        else
        {
            SendRelativeMouseMove(dx, dy);
        }
    }

    /// <summary>
    /// Sends a mouse button press via the active backend.
    /// </summary>
    private static void ButtonDown()
    {
        if (_useLogitech)
        {
            LogiMouseIo(1, 0, 0); // button=1 → left press
        }
        else
        {
            SendMouseButton(MOUSEEVENTF_LEFTDOWN);
        }
    }

    /// <summary>
    /// Sends a mouse button release via the active backend.
    /// </summary>
    private static void ButtonUp()
    {
        if (_useLogitech)
        {
            LogiMouseIo(2, 0, 0); // button=2 → left release
        }
        else
        {
            SendMouseButton(MOUSEEVENTF_LEFTUP);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  MAIN LOOP
    // ═══════════════════════════════════════════════════════════════

    private static async Task MoveLoopAsync(CancellationToken token)
    {
        var rng = new Random();

        // Try Logitech driver first, fall back to SendInput.
        _useLogitech = TryOpenLogitechDriver();
        ActiveBackend = _useLogitech ? "Logitech Driver" : "SendInput";

        // Activate high-resolution timer for accurate sub-16ms delays.
        timeBeginPeriod(1);

        try
        {
            while (!token.IsCancellationRequested)
            {
                // Get current cursor position and screen size.
                GetCursorPos(out var currentPos);
                var screenWidth = GetSystemMetrics(SM_CXSCREEN);
                var screenHeight = GetSystemMetrics(SM_CYSCREEN);

                if (screenWidth <= 0) screenWidth = 1920;
                if (screenHeight <= 0) screenHeight = 1080;

                // ── Destination with randomised jitter (±5-10px) ──
                const int margin = 80;
                var rawTargetX = rng.Next(margin, screenWidth - margin);
                var rawTargetY = rng.Next(margin, screenHeight - margin);

                var jitterRadius = rng.Next(5, 11);
                var targetX = Math.Clamp(
                    rawTargetX + rng.Next(-jitterRadius, jitterRadius + 1),
                    margin, screenWidth - margin);
                var targetY = Math.Clamp(
                    rawTargetY + rng.Next(-jitterRadius, jitterRadius + 1),
                    margin, screenHeight - margin);

                // Generate Bezier curve.
                var cp = GenerateBezierControlPoints(
                    currentPos.X, currentPos.Y,
                    targetX, targetY,
                    rng, screenWidth, screenHeight);

                // Number of steps proportional to distance.
                var distance = Math.Sqrt(
                    Math.Pow(targetX - currentPos.X, 2) +
                    Math.Pow(targetY - currentPos.Y, 2));

                var baseSteps = Math.Clamp((int)(distance / 3.5), 40, 300);
                var steps = baseSteps + rng.Next(-10, 15);

                var prevX = (double)currentPos.X;
                var prevY = (double)currentPos.Y;

                for (var i = 1; i <= steps; i++)
                {
                    if (token.IsCancellationRequested) return;

                    if (i % 5 == 0 || i == 1)
                    {
                        MovementStatus = $"Moving to X:{targetX}, Y:{targetY} (Step {i}/{steps})";
                    }

                    var rawT = (double)i / steps;
                    var t = SmoothStep(rawT);

                    var (bx, by) = EvaluateCubicBezier(
                        cp.P0x, cp.P0y, cp.P1x, cp.P1y,
                        cp.P2x, cp.P2y, cp.P3x, cp.P3y, t);

                    // Micro-noise (hand tremor).
                    var noiseX = rng.Next(-1, 2);
                    var noiseY = rng.Next(-1, 2);
                    if (rng.NextDouble() < 0.08)
                    {
                        noiseX += rng.Next(-1, 2);
                        noiseY += rng.Next(-1, 2);
                    }

                    var nextX = bx + noiseX;
                    var nextY = by + noiseY;

                    var dx = (int)Math.Round(nextX - prevX);
                    var dy = (int)Math.Round(nextY - prevY);

                    if (dx != 0 || dy != 0)
                    {
                        MoveRelative(dx, dy);
                    }

                    prevX += dx;
                    prevY += dy;

                    // Gaussian step delay.
                    var baseDelay = SampleGaussian(rng, mean: 6.0, stdDev: 1.5);
                    baseDelay = Math.Clamp(baseDelay, 2.0, 14.0);

                    if (rng.NextDouble() < 0.03)
                    {
                        baseDelay += rng.Next(15, 60);
                    }

                    if (rawT < 0.1 || rawT > 0.9)
                    {
                        baseDelay *= 1.3 + rng.NextDouble() * 0.5;
                    }

                    await Task.Delay(Math.Max(1, (int)baseDelay), token);
                }

                MoveCount++;

                // Occasional human-like click (12% chance).
                if (rng.NextDouble() < 0.12)
                {
                    await PerformHumanClickAsync(rng, token);
                }

                // Inter-movement pause (with macro-pause probability).
                var pauseMs = ComputeInterMovementPause(rng);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < pauseMs)
                {
                    if (token.IsCancellationRequested) return;
                    var remaining = (pauseMs - sw.ElapsedMilliseconds) / 1000;
                    MovementStatus = $"Menunggu {remaining} detik sebelum bergerak...";
                    await Task.Delay(200, token);
                }
            }
        }
        finally
        {
            timeEndPeriod(1);
            CloseLogitechDriver();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  INTER-MOVEMENT PAUSE
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Jeda antar pergerakan diatur secara ketat agar selalu berada
    /// di antara 3 menit (180,000 ms) hingga 5 menit (300,000 ms).
    /// </summary>
    private static int ComputeInterMovementPause(Random rng)
    {
        // 3 minutes = 180,000 ms
        // 5 minutes = 300,000 ms
        return rng.Next(180_000, 300_001);
    }

    // ═══════════════════════════════════════════════════════════════
    //  HUMAN-LIKE CLICK
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Click with Gaussian press duration (mean 95ms, σ=18ms, clamped [60, 130]).
    /// Includes settle delay before and pause after.
    /// </summary>
    private static async Task PerformHumanClickAsync(Random rng, CancellationToken token)
    {
        var settleDelay = (int)SampleGaussian(rng, mean: 120.0, stdDev: 40.0);
        settleDelay = Math.Clamp(settleDelay, 30, 300);
        await Task.Delay(settleDelay, token);

        ButtonDown();

        var pressDuration = (int)SampleGaussian(rng, mean: 95.0, stdDev: 18.0);
        pressDuration = Math.Clamp(pressDuration, 60, 130);
        await Task.Delay(pressDuration, token);

        ButtonUp();

        var postClickDelay = rng.Next(150, 400);
        await Task.Delay(postClickDelay, token);
    }

    // ═══════════════════════════════════════════════════════════════
    //  BEZIER CURVE MATH
    // ═══════════════════════════════════════════════════════════════

    private readonly record struct BezierPoints(
        double P0x, double P0y,
        double P1x, double P1y,
        double P2x, double P2y,
        double P3x, double P3y);

    private static BezierPoints GenerateBezierControlPoints(
        double startX, double startY,
        double endX, double endY,
        Random rng, int screenWidth, int screenHeight)
    {
        var dx = endX - startX;
        var dy = endY - startY;
        var dist = Math.Sqrt(dx * dx + dy * dy);

        double perpX, perpY;
        if (dist > 0.001)
        {
            perpX = -dy / dist;
            perpY = dx / dist;
        }
        else
        {
            perpX = 0;
            perpY = 1;
        }

        var arc1 = dist * (0.15 + rng.NextDouble() * 0.25) * (rng.NextDouble() < 0.5 ? -1 : 1);
        var arc2 = dist * (0.10 + rng.NextDouble() * 0.20) * (rng.NextDouble() < 0.5 ? -1 : 1);

        var cp1x = startX + dx * (0.25 + rng.NextDouble() * 0.15) + perpX * arc1;
        var cp1y = startY + dy * (0.25 + rng.NextDouble() * 0.15) + perpY * arc1;
        var cp2x = startX + dx * (0.60 + rng.NextDouble() * 0.15) + perpX * arc2;
        var cp2y = startY + dy * (0.60 + rng.NextDouble() * 0.15) + perpY * arc2;

        cp1x = Math.Clamp(cp1x, 5, screenWidth - 5);
        cp1y = Math.Clamp(cp1y, 5, screenHeight - 5);
        cp2x = Math.Clamp(cp2x, 5, screenWidth - 5);
        cp2y = Math.Clamp(cp2y, 5, screenHeight - 5);

        return new BezierPoints(startX, startY, cp1x, cp1y, cp2x, cp2y, endX, endY);
    }

    /// <summary>B(t) = (1-t)³P0 + 3(1-t)²tP1 + 3(1-t)t²P2 + t³P3</summary>
    private static (double X, double Y) EvaluateCubicBezier(
        double p0x, double p0y, double p1x, double p1y,
        double p2x, double p2y, double p3x, double p3y, double t)
    {
        var u = 1.0 - t;
        var uu = u * u;
        var uuu = uu * u;
        var tt = t * t;
        var ttt = tt * t;

        return (
            uuu * p0x + 3 * uu * t * p1x + 3 * u * tt * p2x + ttt * p3x,
            uuu * p0y + 3 * uu * t * p1y + 3 * u * tt * p2y + ttt * p3y);
    }

    private static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3.0 - 2.0 * t);
    }

    // ═══════════════════════════════════════════════════════════════
    //  GAUSSIAN SAMPLING (Box-Muller)
    // ═══════════════════════════════════════════════════════════════

    private static double SampleGaussian(Random rng, double mean, double stdDev)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        var standardNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return mean + stdDev * standardNormal;
    }

    // ═══════════════════════════════════════════════════════════════
    //  WIN32 INTEROP — SendInput fallback
    // ═══════════════════════════════════════════════════════════════

    private static void SendRelativeMouseMove(int dx, int dy)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_MOVE,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, ref input, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseButton(uint buttonFlag)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = buttonFlag,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, ref input, Marshal.SizeOf<INPUT>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  WIN32 CONSTANTS & STRUCTS
    // ═══════════════════════════════════════════════════════════════

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // ── user32.dll ──
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, ref INPUT pInputs, int cbSize);

    // ── winmm.dll — high-resolution timer ──
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint timeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint timeEndPeriod(uint uMilliseconds);

    // ── kernel32.dll — Logitech driver handle ──
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref MOUSE_IO lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
