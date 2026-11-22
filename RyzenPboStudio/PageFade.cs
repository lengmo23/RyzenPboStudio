using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RyzenPboStudio;

/// <summary>
/// Cross-fade for a page switch: a click-through, non-activating window holding a screenshot of the old content sits
/// over the new page and fades out, so the live new page shows through as it fades. The screenshot is captured directly
/// into a GDI compatible bitmap and pushed through UpdateLayeredWindow, avoiding GDI+ bitmap conversion and WM_PAINT.
/// </summary>
internal sealed class PageFade : Form
{
    private const int FadeMs = 150;
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const int ULW_ALPHA = 2;
    private const int SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc,
                                                   ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int flags);
    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDst, int xDst, int yDst, int cx, int cy,
                                      IntPtr hdcSrc, int xSrc, int ySrc, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    // Flush only already-dirty regions on visible children; avoid RDW_INVALIDATE/RDW_ERASE which force full repaints.
    private const uint RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100;

    private readonly Rectangle _screenBounds;
    private readonly IntPtr _memDc;
    private readonly IntPtr _hBitmap;
    private readonly IntPtr _oldBitmap;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly Stopwatch _clock = new();
    private bool _released;

    private PageFade(IntPtr memDc, IntPtr hBitmap, IntPtr oldBitmap, Rectangle screenBounds)
    {
        _screenBounds = screenBounds;
        _memDc = memDc;
        _hBitmap = hBitmap;
        _oldBitmap = oldBitmap;

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = screenBounds;
        _timer.Tick += (_, _) =>
        {
            double t = Math.Min(1.0, _clock.ElapsedMilliseconds / (double)FadeMs);
            Push((byte)Math.Round(255 * (1 - t) * (1 - t)));
            if (t >= 1) Close();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    // The content comes only from UpdateLayeredWindow.
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Push(255);   // before the window is first shown
    }

    private void Push(byte alpha)
    {
        if (!IsHandleCreated) return;
        IntPtr screen = GetDC(IntPtr.Zero);
        try
        {
            var dst = new POINT { X = _screenBounds.X, Y = _screenBounds.Y };
            var size = new SIZE { Cx = _screenBounds.Width, Cy = _screenBounds.Height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { SourceConstantAlpha = alpha };
            UpdateLayeredWindow(Handle, screen, ref dst, ref size, _memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
    }

    /// <summary>Captures what is on screen over <paramref name="area"/>; null when it cannot be captured.</summary>
    public static PageFade? FromScreen(Control area)
    {
        try
        {
            var bounds = area.RectangleToScreen(area.DisplayRectangle);
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;
            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            IntPtr memDc = IntPtr.Zero, hBmp = IntPtr.Zero, oldBmp = IntPtr.Zero;
            try
            {
                memDc = CreateCompatibleDC(screen);
                if (memDc == IntPtr.Zero) return null;
                hBmp = CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
                if (hBmp == IntPtr.Zero)
                {
                    DeleteDC(memDc);
                    return null;
                }
                oldBmp = SelectObject(memDc, hBmp);
                BitBlt(memDc, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, SRCCOPY);
                return new PageFade(memDc, hBmp, oldBmp, bounds);
            }
            catch
            {
                if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                return null;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
        catch { return null; }
    }

    /// <summary>Shows the snapshot fully opaque over the old content, before the page is switched.</summary>
    public void Cover(Form owner) => Show(owner);

    /// <summary>Flushes pending paints on the newly revealed page while still covered, then starts the fade.</summary>
    public void Reveal(Control target)
    {
        if (target.IsHandleCreated)
            RedrawWindow(target.Handle, IntPtr.Zero, IntPtr.Zero, RDW_ALLCHILDREN | RDW_UPDATENOW);
        _clock.Start();
        _timer.Start();
    }

    // A modeless form is disposed when it closes; the timer must stop first.
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        if (!_released)
        {
            _released = true;
            SelectObject(_memDc, _oldBitmap);
            DeleteDC(_memDc);
            DeleteObject(_hBitmap);
        }
        base.Dispose(disposing);
    }
}
