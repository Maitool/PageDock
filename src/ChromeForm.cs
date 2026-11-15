using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PageDock
{
    /// <summary>
    /// 无边框但保留系统原生能力（边缘调整大小、Aero Snap、DWM 阴影、圆角）的窗体基类。
    /// 手法：保留 WS_THICKFRAME/WS_CAPTION 样式，用 WM_NCCALCSIZE 返回 0 隐藏非客户区；
    /// 客户区四周留 6px 深色内边距用于鼠标调整窗口大小。
    /// </summary>
    public class ChromeForm : Form
    {
        private const int WM_NCCALCSIZE = 0x83;
        private const int WM_NCHITTEST = 0x84;

        private const int WS_CAPTION = 0x00C00000;
        private const int WS_SYSMENU = 0x00080000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_CLIPCHILDREN = 0x02000000;

        public const int ResizeGutter = 6;

        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                          HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        public ChromeForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Theme.WindowBg;
            Padding = new Padding(ResizeGutter);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= WS_THICKFRAME | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_CLIPCHILDREN;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.PreferRoundCorners(Handle);
            Native.EnableShadow(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                // 客户区 = 整个窗口；最大化时按系统边框宽度收缩，避免内容溢出屏幕
                if (WindowState == FormWindowState.Maximized)
                {
                    int pad = Native.ResizePad();
                    NCCALCSIZE_PARAMS nc = (NCCALCSIZE_PARAMS)Marshal.PtrToStructure(m.LParam, typeof(NCCALCSIZE_PARAMS));
                    nc.rgrc0.Left += pad;
                    nc.rgrc0.Top += pad;
                    nc.rgrc0.Right -= pad;
                    nc.rgrc0.Bottom -= pad;
                    Marshal.StructureToPtr(nc, m.LParam, false);
                }
                m.Result = IntPtr.Zero;
                return;
            }

            if (m.Msg == WM_NCHITTEST && WindowState != FormWindowState.Maximized)
            {
                long lp = m.LParam.ToInt64();
                Point screenPt = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                int hit = EdgeHit(PointToClient(screenPt));
                if (hit != 0)
                {
                    m.Result = (IntPtr)hit;
                    return;
                }
            }

            base.WndProc(ref m);
        }

        private int EdgeHit(Point p)
        {
            int g = ResizeGutter;
            bool l = p.X <= g;
            bool r = p.X >= ClientSize.Width - g;
            bool t = p.Y <= g;
            bool b = p.Y >= ClientSize.Height - g;
            if (t && l) return HTTOPLEFT;
            if (t && r) return HTTOPRIGHT;
            if (b && l) return HTBOTTOMLEFT;
            if (b && r) return HTBOTTOMRIGHT;
            if (l) return HTLEFT;
            if (r) return HTRIGHT;
            if (t) return HTTOP;
            if (b) return HTBOTTOM;
            return 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NCCALCSIZE_PARAMS { public RECT rgrc0, rgrc1, rgrc2; public IntPtr lppos; }
    }
}
