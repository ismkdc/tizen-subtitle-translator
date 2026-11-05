using System;
using System.IO;
using System.Runtime.InteropServices;

internal static class PpiDump
{
    internal static int LastWidth;
    internal static int LastHeight;
    internal static int LastStride;

    [DllImport("libdisplay-capture-api.so.0", EntryPoint = "_Z10ppicaptureP13RequestedDataPhS1_i", CallingConvention = CallingConvention.Cdecl)]
    private static extern int PpiCapture(IntPtr rd, IntPtr ybuf, IntPtr cbuf, int bufsize);

    public static int Capture()
    {
        int w = 960, h = 540;
        IntPtr rd = Marshal.AllocHGlobal(512);
        IntPtr ybuf = Marshal.AllocHGlobal(w * h);
        IntPtr cbuf = Marshal.AllocHGlobal(w * h);
        try {
        Marshal.Copy(new byte[512], 0, rd, 512);
        Marshal.WriteInt32(rd, 0x00, w); Marshal.WriteInt32(rd, 0x04, h);
        Marshal.WriteInt32(rd, 0x1c, 0); Marshal.WriteInt32(rd, 0x24, 2); Marshal.WriteInt32(rd, 0x28, 6);
        int r = PpiCapture(rd, ybuf, cbuf, w * h);
        int rw = Marshal.ReadInt32(rd, 0), rh = Marshal.ReadInt32(rd, 4);
        int ysz = Marshal.ReadInt32(rd, 0x0c), csz = Marshal.ReadInt32(rd, 0x10);
        IntPtr yp = Marshal.ReadIntPtr(rd, 0x14), cp = Marshal.ReadIntPtr(rd, 0x18);
        Console.WriteLine("r=" + r + " " + rw + "x" + rh + " ysz=" + ysz + " csz=" + csz);
        if (r != 0 || rw <= 0 || rh <= 0 || rw > w || (long)rw*rh > ysz || ysz <= 0 || csz <= 0 || ysz > w*h || csz > w*h || yp == IntPtr.Zero || cp == IntPtr.Zero) return 2;
        var y = new byte[ysz]; Marshal.Copy(yp, y, 0, ysz);
        var c = new byte[csz]; Marshal.Copy(cp, c, 0, csz);
        LastWidth = rw;
        LastHeight = rh;
        LastStride = rw;
        File.WriteAllBytes("/tmp/subtitle-probe/frame.y", y);
        File.WriteAllBytes("/tmp/subtitle-probe/frame.c", c);
        File.WriteAllText("/tmp/subtitle-probe/frame.meta", rw + " " + rh);
        Console.WriteLine("dumped " + ysz + "+" + csz);
        return 0;
        } finally { Marshal.FreeHGlobal(cbuf); Marshal.FreeHGlobal(ybuf); Marshal.FreeHGlobal(rd); }
    }
}
