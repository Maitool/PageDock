using System;
using System.Runtime.InteropServices;

namespace PageDock
{
    /// <summary>Win32 原生调用：DWM 阴影/圆角、无边框拖动。</summary>
    internal static class Native
    {
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("dwmapi.dll")]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string cls, string win);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string win);

        [DllImport("user32.dll")]
        public static extern IntPtr SetParent(IntPtr child, IntPtr parent);

        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);

        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int idx, int val);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public delegate bool EnumProc(IntPtr h, IntPtr l);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumProc cb, IntPtr l);

        public const int SW_RESTORE = 9;

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS { public int Left, Right, Top, Bottom; }

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 2;
        private const int SM_CXSIZEFRAME = 32;      // 0x20
        private const int SM_CXPADDEDBORDER = 92;   // 0x5C
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        /// <summary>系统边框宽度（含内边距），最大化时用于收缩客户区。</summary>
        public static int ResizePad()
        {
            return GetSystemMetrics(SM_CXSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
        }

        /// <summary>Win11 圆角窗口（旧系统静默失败）。</summary>
        public static void PreferRoundCorners(IntPtr handle)
        {
            try
            {
                int v = DWMWCP_ROUND;
                DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, 4);
            }
            catch { }
        }

        /// <summary>向 DWM 延展 1px 以获得系统阴影。</summary>
        public static void EnableShadow(IntPtr handle)
        {
            try
            {
                MARGINS m; m.Left = 0; m.Right = 0; m.Top = 0; m.Bottom = 1;
                DwmExtendFrameIntoClientArea(handle, ref m);
            }
            catch { }
        }

        /// <summary>经典手法：释放鼠标捕获并向下发 HTCAPTION，进入系统移动循环。</summary>
        public static void StartWindowDrag(IntPtr handle)
        {
            ReleaseCapture();
            SendMessage(handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        /// <summary>
        /// 把窗口强行拉到最前并聚焦。全局热键从别的程序触发时，本进程并非前台，
        /// 直接 SetForegroundWindow / WinForms Activate() 会被系统"前台锁定"策略忽略
        /// （表现就是"按了快捷键没反应"）。这里用 AttachThreadInput 把本线程输入队列
        /// 临时接到当前前台线程上，绕过该限制后再 BringWindowToTop + SetForegroundWindow。
        /// </summary>
        public static void ForceForeground(IntPtr hwnd)
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                uint tidFg = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
                uint tidCur = GetCurrentThreadId();
                bool attached = tidFg != 0 && tidFg != tidCur && AttachThreadInput(tidCur, tidFg, true);
                try
                {
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
                finally
                {
                    if (attached) AttachThreadInput(tidCur, tidFg, false);
                }
            }
            catch { try { SetForegroundWindow(hwnd); } catch { } }
        }

        /// <summary>真·钉到桌面：直接挂 Progman（桌面宿主，常驻不消失），
        /// 小组件成为图标的兄弟层，显示桌面/不被窗口遮挡时可见。
        /// 不用 WorkerW 方案：新系统结构会变且临时 WorkerW 会被回收导致小组件消失。</summary>
        public static IntPtr GetDesktopHost()
        {
            return FindWindow("Progman", null);
        }
    }
}
