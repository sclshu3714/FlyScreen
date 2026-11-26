using System.Runtime.InteropServices;
using System.Text;
using FlyScreen.Linux.Environment;

namespace FlyScreen.Linux.LockInput;

/// <summary>
/// 通过 /dev/uinput 创建虚拟键鼠。登录管理器（GDM/SDDM 等）通常能收到。
/// 需要写权限（root、input 组或 udev 规则）。
/// </summary>
internal sealed class UinputInjector : IDisposable
{
    private const ushort EvSyn = 0x00;
    private const ushort EvKey = 0x01;
    private const ushort EvRel = 0x02;
    private const ushort EvAbs = 0x03;
    private const ushort SynReport = 0;
    private const ushort RelX = 0x00;
    private const ushort RelY = 0x01;
    private const ushort RelWheel = 0x08;
    private const ushort RelHWheel = 0x06;
    private const ushort AbsX = 0x00;
    private const ushort AbsY = 0x01;
    private const ushort BtnLeft = 0x110;
    private const ushort BtnRight = 0x111;
    private const ushort BtnMiddle = 0x112;
    private const uint UiDevCreate = 0x5501;
    private const uint UiDevDestroy = 0x5502;
    private const uint UiSetEvBit = 0x40045564;
    private const uint UiSetKeyBit = 0x40045565;
    private const uint UiSetRelBit = 0x40045566;
    private const uint UiSetAbsBit = 0x40045567;
    private const uint UiDevSetup = 0x405c5503;
    private const int AbsMax = 65535;
    private const int AbsCnt = 64;
    private const int NameSize = 80;

    private int _fd = -1;

    public bool IsAvailable => _fd >= 0;

    public bool TryOpen()
    {
        if (!LinuxRuntimeEnvironment.IsLinux) return false;
        if (_fd >= 0) return true;

        try
        {
            _fd = open("/dev/uinput", OpenRdwr | OpenNonblock, 0);
            if (_fd < 0)
                _fd = open("/dev/input/uinput", OpenRdwr | OpenNonblock, 0);
            if (_fd < 0) return false;

            IoctlInt(_fd, UiSetEvBit, EvSyn);
            IoctlInt(_fd, UiSetEvBit, EvKey);
            IoctlInt(_fd, UiSetEvBit, EvRel);
            IoctlInt(_fd, UiSetEvBit, EvAbs);

            IoctlInt(_fd, UiSetKeyBit, BtnLeft);
            IoctlInt(_fd, UiSetKeyBit, BtnRight);
            IoctlInt(_fd, UiSetKeyBit, BtnMiddle);
            for (var k = 1; k < 128; k++)
                IoctlInt(_fd, UiSetKeyBit, k);

            IoctlInt(_fd, UiSetRelBit, RelX);
            IoctlInt(_fd, UiSetRelBit, RelY);
            IoctlInt(_fd, UiSetRelBit, RelWheel);
            IoctlInt(_fd, UiSetRelBit, RelHWheel);
            IoctlInt(_fd, UiSetAbsBit, AbsX);
            IoctlInt(_fd, UiSetAbsBit, AbsY);

            var setup = new byte[NameSize + 8 + 4];
            var nameBytes = Encoding.ASCII.GetBytes("FlyScreen LockInput");
            Array.Copy(nameBytes, 0, setup, 0, Math.Min(nameBytes.Length, NameSize - 1));
            BitConverter.TryWriteBytes(setup.AsSpan(80), (ushort)3);
            BitConverter.TryWriteBytes(setup.AsSpan(82), (ushort)0xF15C);
            BitConverter.TryWriteBytes(setup.AsSpan(84), (ushort)1);
            BitConverter.TryWriteBytes(setup.AsSpan(86), (ushort)1);

            var setupOk = false;
            var handle = GCHandle.Alloc(setup, GCHandleType.Pinned);
            try
            {
                setupOk = ioctl(_fd, UiDevSetup, handle.AddrOfPinnedObject()) == 0;
            }
            finally
            {
                handle.Free();
            }

            if (!setupOk)
            {
                var legacy = new byte[NameSize + 8 + 4 + AbsCnt * 4 * 4];
                Array.Copy(setup, 0, legacy, 0, NameSize + 8 + 4);
                BitConverter.TryWriteBytes(legacy.AsSpan(NameSize + 8 + 4 + AbsX * 4), AbsMax);
                BitConverter.TryWriteBytes(legacy.AsSpan(NameSize + 8 + 4 + AbsY * 4), AbsMax);
                if (write(_fd, legacy, legacy.Length) < 0)
                {
                    Close();
                    return false;
                }
            }

            if (ioctl(_fd, UiDevCreate, IntPtr.Zero) != 0)
            {
                Close();
                return false;
            }

            Thread.Sleep(40);
            return true;
        }
        catch
        {
            Close();
            return false;
        }
    }

    public void MoveAbsoluteNormalized(double nx, double ny)
    {
        if (!IsAvailable) return;
        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);
        Emit(EvAbs, AbsX, (int)Math.Round(nx * AbsMax));
        Emit(EvAbs, AbsY, (int)Math.Round(ny * AbsMax));
        Emit(EvSyn, SynReport, 0);
    }

    public void MoveRelative(int dx, int dy)
    {
        if (!IsAvailable) return;
        if (dx != 0) Emit(EvRel, RelX, dx);
        if (dy != 0) Emit(EvRel, RelY, dy);
        Emit(EvSyn, SynReport, 0);
    }

    public void MouseButton(int button, bool down)
    {
        if (!IsAvailable) return;
        var code = button switch
        {
            1 => BtnRight,
            2 => BtnMiddle,
            _ => BtnLeft
        };
        Emit(EvKey, code, down ? 1 : 0);
        Emit(EvSyn, SynReport, 0);
    }

    public void MouseWheel(int delta, bool horizontal)
    {
        if (!IsAvailable) return;
        var notches = delta / 120;
        if (notches == 0) notches = Math.Sign(delta);
        Emit(EvRel, horizontal ? RelHWheel : RelWheel, notches);
        Emit(EvSyn, SynReport, 0);
    }

    public void Key(ushort vk, bool down)
    {
        if (!IsAvailable) return;
        var code = MapVkToLinuxKey(vk);
        if (code == 0) return;
        Emit(EvKey, code, down ? 1 : 0);
        Emit(EvSyn, SynReport, 0);
    }

    public void InsertText(string text)
    {
        if (!IsAvailable || string.IsNullOrEmpty(text)) return;
        foreach (var ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                Key(0x0D, true);
                Key(0x0D, false);
                continue;
            }

            if (ch is >= 'a' and <= 'z')
            {
                var vk = (ushort)('A' + (ch - 'a'));
                Key(vk, true);
                Key(vk, false);
            }
            else if (ch is >= 'A' and <= 'Z')
            {
                Key(0x10, true);
                Key(ch, true);
                Key(ch, false);
                Key(0x10, false);
            }
            else if (ch is >= '0' and <= '9')
            {
                Key(ch, true);
                Key(ch, false);
            }
            else if (ch == ' ')
            {
                Key(0x20, true);
                Key(0x20, false);
            }
        }
    }

    public void Dispose() => Close();

    private void Close()
    {
        if (_fd < 0) return;
        try { ioctl(_fd, UiDevDestroy, IntPtr.Zero); } catch { /* ignore */ }
        try { close(_fd); } catch { /* ignore */ }
        _fd = -1;
    }

    private void Emit(ushort type, ushort code, int value)
    {
        var buf = new byte[24];
        BitConverter.TryWriteBytes(buf.AsSpan(16), type);
        BitConverter.TryWriteBytes(buf.AsSpan(18), code);
        BitConverter.TryWriteBytes(buf.AsSpan(20), value);
        _ = write(_fd, buf, buf.Length);
    }

    private static void IoctlInt(int fd, uint request, int bit)
    {
        var v = bit;
        _ = ioctl(fd, request, ref v);
    }

    private static ushort MapVkToLinuxKey(ushort vk)
        => vk switch
        {
            0x08 => 14,
            0x09 => 15,
            0x0D => 28,
            0x10 => 42,
            0x11 => 29,
            0x12 => 56,
            0x1B => 1,
            0x20 => 57,
            0x25 => 105,
            0x26 => 103,
            0x27 => 106,
            0x28 => 108,
            0x2E => 111,
            0x30 => 11,
            >= 0x31 and <= 0x39 => (ushort)(vk - 0x31 + 2),
            >= 0x41 and <= 0x5A => (ushort)(vk - 0x41 + 30),
            0x70 => 59,
            0x71 => 60,
            0x72 => 61,
            0x73 => 62,
            _ => 0
        };

    private const int OpenRdwr = 2;
    private const int OpenNonblock = 0x800;

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int write(int fd, byte[] buffer, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, IntPtr arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, ref int arg);
}
