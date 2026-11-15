using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PageDock
{
    /// <summary>
    /// 独立应用窗口：原生标题栏 + WebView2。
    /// 与主窗口共享同一个 CoreWebView2Environment（同一个浏览器进程，省内存）。
    /// 关闭时记录窗口尺寸，供“独立窗口尺寸记忆”写回应用条目。
    /// </summary>
    internal class ChildFrame : ChromeForm
    {
        private readonly WebView2 web = new WebView2();
        private readonly TitleBar bar = new TitleBar();
        private readonly FullscreenController fullscreen;
        private readonly string startUrl;
        private readonly string appId;

        public ChildFrame(string url, string title, int width, int height, string appId)
        {
            this.appId = appId;
            this.startUrl = url;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int w = Clamp(width <= 0 ? 1080 : width, 460, wa.Width - 24);
            int h = Clamp(height <= 0 ? 720 : height, 360, wa.Height - 24);
            Size = new Size(w, h);
            MinimumSize = new Size(420, 320);
            StartPosition = FormStartPosition.CenterScreen;
            Text = string.IsNullOrEmpty(title) ? "页坞" : title;
            bar.Title.Text = Text;
            try { Icon = PageDock.AppIcon.FormIcon(); } catch { }

            bar.MinClicked += delegate { WindowState = FormWindowState.Minimized; };
            bar.MaxClicked += ToggleMax;
            bar.MaxToggled += ToggleMax;
            bar.CloseClicked += delegate { Close(); };
            bar.DockClicked += delegate { DockBack(); };
            bar.PinClicked += delegate { ToggleTopMost(); };
            fullscreen = new FullscreenController(this, bar);
            bar.FullscreenClicked += delegate { fullscreen.Toggle(); };

            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = Theme.WindowBg;
            Controls.Add(web);
            Controls.Add(bar);

            Load += async (s, e) => await InitAsync();
        }

        public string AppId { get { return appId; } }
        public int SavedWidth { get; private set; }
        public int SavedHeight { get; private set; }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            bar.SetMaximized(WindowState == FormWindowState.Maximized);
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        /// <summary>把当前页面移回主窗口标签：主窗口打开同一 URL 的标签后关闭本窗口。</summary>
        private void DockBack()
        {
            string url = null;
            try { if (web.Source != null) url = web.Source.ToString(); } catch { }
            if (string.IsNullOrEmpty(url)) url = startUrl;
            MainFrame mf = Program.MainForm as MainFrame;
            if (mf != null) mf.OpenTabFromChild(url, Text, appId);
            Close();
        }

        private void ToggleMax(object sender, EventArgs e)
        {
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        /// <summary>置顶本独立窗口（与主窗口及其他子窗口互不影响，即时切换、不跨重启记忆）。</summary>
        private void ToggleTopMost()
        {
            TopMost = !TopMost;
            bar.SetPinned(TopMost);
        }

        /// <summary>主题切换后重刷原生部分。</summary>
        public void ApplyTheme()
        {
            BackColor = Theme.WindowBg;
            bar.ApplyTheme();
        }

        private async Task InitAsync()
        {
            try
            {
                CoreWebView2Environment env = await Program.GetEnvironmentAsync();
                await web.EnsureCoreWebView2Async(env);
                CoreWebView2 core = web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = true;
                core.DocumentTitleChanged += delegate
                {
                    bar.Title.Text = core.DocumentTitle;
                    Text = core.DocumentTitle;
                };
                core.NewWindowRequested += delegate(object s, CoreWebView2NewWindowRequestedEventArgs ev)
                {
                    ev.Handled = true;
                    string u = ev.Uri == null ? "" : ev.Uri;
                    if (u.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
                    WindowManager.OpenChild(u, null, 1080, 720, null);
                };
                // 首帧主题：宿主已经把设置/系统主题算好，先注入再导航，避免首帧用旧颜色偏好
                await core.AddScriptToExecuteOnDocumentCreatedAsync(Theme.FirstPaintScript());
                // 本地工具：导航前按 id 注册唯一 origin 映射 + 把下载归位到 output/
                AppHost.RegisterMappingIfNeeded(core, startUrl);
                AppHost.WireDownload(core, appId);
                web.Source = new Uri(startUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法加载页面：\n" + ex.Message, "页坞 PageDock",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 在窗口真正关闭前记录尺寸（最大化时记不住尺寸，跳过）
            if (WindowState == FormWindowState.Normal)
            {
                SavedWidth = Width;
                SavedHeight = Height;
            }
            base.OnFormClosing(e);
        }
    }
}
