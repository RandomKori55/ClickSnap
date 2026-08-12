using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ClickSnap;

public class MouseReader : IDisposable
{
    private const ushort EV_KEY = 0x01;
    private const ushort BTN_LEFT = 0x110;
    private const ushort BTN_RIGHT = 0x111;
    private const ushort BTN_MIDDLE = 0x112;
    private const int KEY_MAX = 0x2ff;

    private FileStream? _stream;
    private Thread? _readerThread;
    private CancellationTokenSource? _cts;

    public event Action? ButtonPressed;

    public bool IsRunning => _readerThread is { IsAlive: true };

    [StructLayout(LayoutKind.Sequential)]
    private struct InputEvent
    {
        public long time_sec;
        public long time_usec;
        public ushort type;
        public ushort code;
        public int value;
    }

    [DllImport("libc", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ioctl(int fd, nuint request, byte[] data);

    public void Start()
    {
        if (IsRunning)
            return;

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "MouseReader supports only Linux /dev/input devices.");
        }

        string? devicePath = FindMouseDevice();

        if (devicePath is null)
        {
            throw new InvalidOperationException(
                "No mouse device found. Make sure a mouse is connected and you have read access to /dev/input/event*.\n" +
                "Try adding yourself to the 'input' group: sudo usermod -a -G input $USER");
        }

        FileStream stream;

        try
        {
            stream = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"No read access to {devicePath}. Add your user to the 'input' group: sudo usermod -a -G input $USER",
                ex);
        }

        var cts = new CancellationTokenSource();

        _stream = stream;
        _cts = cts;

        _readerThread = new Thread(() => ReadLoop(stream, cts.Token))
        {
            IsBackground = true,
            Name = "MouseReader"
        };

        _readerThread.Start();
    }

    public void Stop()
    {
        var cts = _cts;
        var stream = _stream;
        var thread = _readerThread;

        _cts = null;
        _stream = null;
        _readerThread = null;

        cts?.Cancel();

        try
        {
            stream?.Dispose();
        }
        catch
        {
            // Ignore dispose errors.
        }

        thread?.Join(1000);
        cts?.Dispose();
    }

    private void ReadLoop(FileStream stream, CancellationToken token)
    {
        byte[] buffer = new byte[Marshal.SizeOf<InputEvent>()];

        while (!token.IsCancellationRequested)
        {
            try
            {
                int bytesRead = stream.Read(buffer, 0, buffer.Length);

                if (bytesRead == 0)
                    break;

                if (bytesRead < buffer.Length)
                {
                    stream.Seek(-bytesRead, SeekOrigin.Current);
                    Thread.Sleep(1);
                    continue;
                }

                InputEvent evt = MemoryMarshal.Read<InputEvent>(buffer);

                if (evt.type == EV_KEY && evt.value == 1)
                {
                    if (evt.code == BTN_LEFT || evt.code == BTN_RIGHT || evt.code == BTN_MIDDLE)
                    {
                        Task.Run(() => ButtonPressed?.Invoke());
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }
        }
    }

    private static string? FindMouseDevice()
    {
        try
        {
            if (!Directory.Exists("/dev/input"))
                return null;

            string[] devices = Directory.GetFiles("/dev/input", "event*");

            foreach (var device in devices)
            {
                if (IsMouseDevice(device))
                    return device;
            }
        }
        catch
        {
            // Ignore enumeration errors.
        }

        return null;
    }

    private static bool IsMouseDevice(string devicePath)
    {
        try
        {
            using var fs = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            int fd = (int)fs.SafeFileHandle.DangerousGetHandle();

            byte[] keyBits = new byte[KEY_MAX / 8 + 1];

            nuint request = EVIOCGBIT(EV_KEY, keyBits.Length);

            if (ioctl(fd, request, keyBits) == -1)
                return false;

            int byteIndex = BTN_LEFT / 8;
            int bitIndex = BTN_LEFT % 8;

            return byteIndex < keyBits.Length && (keyBits[byteIndex] & (1 << bitIndex)) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static nuint EVIOCGBIT(int ev, int len)
    {
        const uint IOC_READ = 2u;
        const uint TYPE = (uint)'E';

        uint nr = 0x20u + (uint)ev;

        uint request = (IOC_READ << 30)
                     | ((uint)len << 16)
                     | (TYPE << 8)
                     | nr;

        return (nuint)request;
    }

    public void Dispose()
    {
        Stop();
        ButtonPressed = null;
    }
}