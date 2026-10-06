using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace VkBenchmark
{
    public sealed class NativeWindow
    {
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
        [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

        private readonly Process process;
        private readonly CancellationToken token;
        public NativeWindow(Process process, CancellationToken token) { this.process = process; this.token = token; }
        private IntPtr Handle
        {
            get
            {
                CheckAbort();
                if (process.HasExited) throw new InvalidOperationException("Benchmark process exited before results.");
                process.Refresh();
                if (process.MainWindowHandle == IntPtr.Zero) throw new InvalidOperationException("Benchmark window unavailable.");
                return process.MainWindowHandle;
            }
        }
        public void CheckAbort()
        {
            token.ThrowIfCancellationRequested();
            if ((GetAsyncKeyState((int)Keys.F12) & 0x8000) != 0)
                throw new OperationCanceledException("Stopped by F12.");
        }
        public void Focus()
        {
            var handle = Handle;
            if (GetForegroundWindow() != handle)
            {
                ShowWindow(handle, 9);
                SetForegroundWindow(handle);
                Thread.Sleep(200);
                if (GetForegroundWindow() != handle)
                    throw new InvalidOperationException("Cannot focus benchmark. Close overlays and run at the same privilege level as Steam.");
            }
        }
        private Rectangle Bounds()
        {
            Rect rect;
            Point origin = new Point();
            var handle = Handle;
            if (!GetClientRect(handle, out rect) || !ClientToScreen(handle, ref origin)) throw new Win32Exception();
            var bounds = new Rectangle(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
            if (bounds.Width < 640 || bounds.Height < 360) throw new InvalidOperationException("Benchmark window is too small/minimized.");
            if (!Screen.AllScreens.Any(s => s.Bounds.Contains(bounds)))
                throw new InvalidOperationException("Benchmark window must fit entirely on one screen.");
            return bounds;
        }
        public Bitmap Capture()
        {
            Focus();
            var bounds = Bounds();
            var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
            return bitmap;
        }
        public void Click(int x, int y)
        {
            Focus();
            var bounds = Bounds();
            if (x < 0 || x >= bounds.Width || y < 0 || y >= bounds.Height) throw new ArgumentOutOfRangeException("Click outside client area.");
            SetCursorPos(bounds.Left + x, bounds.Top + y);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(60);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        }
        public void Click(Phrase phrase) { Click(phrase.Box.Left + phrase.Box.Width / 2, phrase.Box.Top + phrase.Box.Height / 2); }
        public void Key(Keys key)
        {
            Focus();
            keybd_event((byte)key, 0, 0, UIntPtr.Zero);
            Thread.Sleep(65);
            keybd_event((byte)key, 0, 2, UIntPtr.Zero);
        }
        public void Scroll(int notches)
        {
            Focus();
            var bounds = Bounds();
            SetCursorPos(bounds.Left + (int)(bounds.Width * .55), bounds.Top + bounds.Height / 2);
            mouse_event(0x800, 0, 0, unchecked((uint)(notches * 120)), UIntPtr.Zero);
            Thread.Sleep(250);
        }
        public static byte[] Signature(Bitmap bitmap)
        {
            var result = new byte[64 * 36 * 3];
            using (var small = new Bitmap(bitmap, 64, 36))
                for (int y = 0; y < 36; y++)
                    for (int x = 0; x < 64; x++)
                    {
                        var color = small.GetPixel(x, y);
                        int i = (y * 64 + x) * 3;
                        result[i] = color.R; result[i + 1] = color.G; result[i + 2] = color.B;
                    }
            return result;
        }
        public static double Difference(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != second.Length) return 1;
            long sum = 0;
            for (int i = 0; i < first.Length; i++) sum += Math.Abs(first[i] - second[i]);
            return (double)sum / (first.Length * 255);
        }
    }
}
