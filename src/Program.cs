using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace PageDock
{
    internal static class Program
    {
        public static Form MainForm;
        /// <summary>主窗口已进入关闭流程：此时小组件会被动关闭，不应视为“取消钉住”。</summary>
        public static bool Exiting;
        private static Task<CoreWebView2Environment> envTask;

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\PageDock.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    // 再次启动（双击 exe / 快捷方式）不是报错，而是把已运行的实例唤起聚焦（冷启动→唤起）
                    if (!SummonExisting())
                        MessageBox.Show("页坞已在运行。", "页坞 PageDock",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                AppDomain.CurrentDomain.FirstChanceException += delegate(object s, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
                {
                    NullReferenceException nre = e.Exception as NullReferenceException;
                    if (nre != null)
                    {
                        try
                        {
                            System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nre.log"),
                                DateTime.Now.ToString("HH:mm:ss.fff ") + nre.StackTrace + "\r\n\r\n");
                        }
                        catch { }
                    }
                };
                Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
                {
                    try { System.IO.File.AppendAllText(System.IO.Path.Combine(Data.WebDataDir, "error.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + e.Exception + "\r\n\r\n"); } catch { }
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    try { System.IO.File.AppendAllText(System.IO.Path.Combine(Data.WebDataDir, "error.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + e.ExceptionObject + "\r\n\r\n"); } catch { }
                };
                Data.Init();
                // 上次导入的“整包”内核数据（含各工具 localStorage）在此落地：必须早于环境/profile 创建
                AppHost.ApplyPendingWebview();
                // 主题必须在建窗之前定下：窗体基类构造函数里就会读 Theme.WindowBg，
                // 晚一步就会先画一帧深色、等页面发 set-theme 才跳成浅色。
                Theme.ResolveFromConfig();
                MainForm = new MainFrame();
                Application.Run(MainForm);
                GC.KeepAlive(mutex);
            }
        }

        /// <summary>向已运行的主实例发消息唤起；成功返回 true。读不到实例信息则返回 false。</summary>
        private static bool SummonExisting()
        {
            try
            {
                Data.Init();
                string f = Path.Combine(Data.DataDir, "instance.json");
                if (!File.Exists(f)) return false;
                Dictionary<string, object> d =
                    new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
                object hv;
                if (d == null || !d.TryGetValue("hwnd", out hv) || hv == null) return false;
                IntPtr hwnd = new IntPtr(Convert.ToInt64(hv));
                if (hwnd == IntPtr.Zero) return false;
                uint msg = Native.RegisterWindowMessage("PageDock.Summon");
                return Native.PostMessage(hwnd, msg, IntPtr.Zero, IntPtr.Zero);
            }
            catch { return false; }
        }

        /// <summary>
        /// 全进程共享一个 CoreWebView2Environment：
        /// 主窗口与所有独立窗口共用同一个 WebView2 浏览器进程，显著降低内存占用。
        /// </summary>
        public static Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            if (envTask == null)
            {
                CoreWebView2EnvironmentOptions opt = new CoreWebView2EnvironmentOptions();
                // 允许 file:// 页面通过 XHR/fetch 读取同目录资源，兼容更多离线单页应用；
                // 禁用遮挡节流：桌面小组件（挂 WorkerW/Progman 层）与被遮挡窗口也能持续渲染
                opt.AdditionalBrowserArguments = "--allow-file-access-from-files --disable-backgrounding-occluded-windows --disable-renderer-backgrounding --disable-features=CalculateNativeWinOcclusion";
                envTask = CoreWebView2Environment.CreateAsync(null, Data.WebDataDir, opt);
            }
            return envTask;
        }
    }

    /// <summary>独立应用窗口与桌面小组件的创建与生命周期管理。</summary>
    internal static class WindowManager
    {
        private static readonly List<ChildFrame> Children = new List<ChildFrame>();
        private static readonly List<WidgetFrame> Widgets = new List<WidgetFrame>();

        public static bool IsPinned(string appId)
        {
            foreach (WidgetFrame w in Widgets.ToArray())
                if (w.AppId == appId) return true;
            return false;
        }

        public static void OpenWidget(string url, string title, string appId, int x, int y, int w, int h)
        {
            if (string.IsNullOrEmpty(url)) return;
            foreach (WidgetFrame old in Widgets.ToArray())
            {
                if (old.AppId == appId) { try { old.Activate(); } catch { } return; } // 已钉住 → 聚焦
            }
            WidgetFrame f = new WidgetFrame(url, title, appId, x, y, w, h);
            Widgets.Add(f);
            f.FormClosed += delegate(object s, FormClosedEventArgs e)
            {
                Widgets.Remove((WidgetFrame)s);
                MainFrame mf = Program.MainForm as MainFrame;
                if (mf != null && !string.IsNullOrEmpty(appId))
                    mf.PushWidgetClosed(appId, f.SavedX, f.SavedY, f.SavedW, f.SavedH);
            };
            f.Show(); // 不挂 owner：避免 owned 窗口跨进程重挂后的 z 序/消息异常
        }

        public static void CloseWidget(string appId)
        {
            foreach (WidgetFrame w in Widgets.ToArray())
                if (w.AppId == appId) { try { w.Close(); } catch { } }
        }

        public static void OpenChild(string url, string title, int width, int height, string appId)
        {
            if (string.IsNullOrEmpty(url)) return;
            ChildFrame f = new ChildFrame(url, title, width, height, appId);
            Children.Add(f);
            f.FormClosed += delegate(object s, FormClosedEventArgs e)
            {
                Children.Remove((ChildFrame)s);
                // 独立窗口尺寸记忆：关闭时把尺寸推回 UI 层写进配置（最小化关闭时尺寸无效，跳过）
                if (!string.IsNullOrEmpty(appId) && f.SavedWidth > 0 && f.SavedHeight > 0)
                {
                    MainFrame mf = Program.MainForm as MainFrame;
                    if (mf != null) mf.PushChildSize(appId, f.SavedWidth, f.SavedHeight);
                }
            };
            f.Show(Program.MainForm);
        }

        public static void ApplyThemeToAll()
        {
            ChildFrame[] snapshot = Children.ToArray();
            foreach (ChildFrame f in snapshot)
            {
                try { f.ApplyTheme(); } catch { }
            }
            WidgetFrame[] ws = Widgets.ToArray();
            foreach (WidgetFrame f in ws)
            {
                try { f.ApplyTheme(); } catch { }
            }
        }

        public static void CloseAll()
        {
            for (int i = Widgets.Count - 1; i >= 0; i--)
            {
                try { Widgets[i].Close(); } catch { }
            }
            Widgets.Clear();
            for (int i = Children.Count - 1; i >= 0; i--)
            {
                try { Children[i].Close(); } catch { }
            }
            Children.Clear();
        }
    }
}
