using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PageDock
{
    /// <summary>
    /// 桌面小组件：把应用“钉”在电脑桌面（挂 Progman 桌面层，显示桌面时可见、不占任务栏）。
    /// 界面极简：无标题栏；鼠标进窗显示顶部工具条（拖动区 + 移回工作台 + 关闭）；
    /// 右下角缩放角可自由调整大小；位置尺寸变化实时回写主页配置。
    /// </summary>
    internal class WidgetFrame : ChromeForm
    {
        private readonly WebView2 web = new WebView2();
        private readonly Panel topBar = new Panel();
        private readonly IconButton btnDock, btnClose;
        private readonly Panel grip = new Panel();
        private readonly string appId;
        private readonly string baseUrl;
        private readonly int wantX, wantY;
        private readonly Timer bottomTimer = new Timer();
        private readonly Timer hoverTimer = new Timer();
        private bool dragging, resizing;
        private Point dragStart, formStart;
        private Size sizeStart;

        public WidgetFrame(string url, string title, string appId, int x, int y, int w, int h)
        {
            this.appId = appId;
            this.baseUrl = url;
            wantX = x; wantY = y;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            w = Clamp(w <= 0 ? 380 : w, 260, wa.Width - 24);
            h = Clamp(h <= 0 ? 280 : h, 200, wa.Height - 24);
            StartPosition = FormStartPosition.Manual;
            Size = new Size(w, h);
            Location = new Point(wa.Left + 40, wa.Top + 60); // 占位，Shown 时按配置精确定位
            ShowInTaskbar = false;
            Text = string.IsNullOrEmpty(title) ? "小组件" : title;
            try { Icon = PageDock.AppIcon.FormIcon(); } catch { }

            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = Theme.WindowBg;
            Controls.Add(web);

            // 顶部工具条（悬停显示）：左侧大块拖动区 + 右侧两个按钮
            topBar.Height = 28;
            topBar.Cursor = Cursors.SizeAll;
            topBar.Visible = false;
            btnDock = new IconButton { Kind = IconButton.IconKind.DockIn, Size = new Size(30, 26), TabStop = false };
            btnClose = new IconButton { Kind = IconButton.IconKind.Close, Size = new Size(30, 26), TabStop = false };
            topBar.Controls.Add(btnDock);
            topBar.Controls.Add(btnClose);
            topBar.Resize += delegate
            {
                btnClose.Location = new Point(topBar.ClientSize.Width - 32, 1);
                btnDock.Location = new Point(topBar.ClientSize.Width - 64, 1);
            };
            topBar.MouseDown += BarDragStart;
            topBar.MouseMove += BarDragMove;
            topBar.MouseUp += BarDragEnd;
            Controls.Add(topBar);
            topBar.BringToFront();

            // 右下角缩放角（自由调整大小）
            grip.Size = new Size(18, 18);
            grip.Cursor = Cursors.SizeNWSE;
            grip.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen p = new Pen(Theme.TextDim, 1.6f))
                {
                    for (int i = 1; i <= 3; i++)
                        e.Graphics.DrawLine(p, grip.Width - i * 5 + 2, grip.Height - 2,
                                            grip.Width - 2, grip.Height - i * 5 + 2);
                }
            };
            grip.MouseDown += GripDown;
            grip.MouseMove += GripMove;
            grip.MouseUp += GripEnd;
            grip.Visible = false;
            Controls.Add(grip);
            grip.BringToFront();

            btnDock.Click += delegate { DockBack(); };
            btnClose.Click += delegate { Close(); };

            // 工具条与缩放角“按需出现”：只有光标靠近窗口顶部／右下角时才浮出，
            // 平时完全不遮盖内容。不能依赖 Form 的 MouseEnter/MouseLeave：WebView2 铺满
            // 客户区，鼠标在页面区域时父窗体收不到进入事件，这里改为轻量轮询光标位置。
            hoverTimer.Interval = 150;
            hoverTimer.Tick += delegate
            {
                try
                {
                    if (!IsHandleCreated || IsDisposed) { hoverTimer.Stop(); return; }
                    if (dragging || resizing) return; // 拖动/缩放中不切换，避免打断捕获
                    Point pt = PointToClient(Cursor.Position);
                    bool inWin = ClientRectangle.Contains(pt);
                    bool nearTop = inWin && pt.Y <= 34;
                    bool nearGrip = inWin && pt.X >= ClientSize.Width - 34 && pt.Y >= ClientSize.Height - 34;
                    if (nearTop != topBar.Visible)
                    {
                        topBar.Visible = nearTop;
                        if (nearTop) topBar.BringToFront();
                    }
                    if (nearGrip != grip.Visible)
                    {
                        grip.Visible = nearGrip;
                        if (nearGrip) grip.BringToFront();
                    }
                }
                catch { }
            };

            ApplyTheme();

            // 贴底定时器：未被用户操作时持续压到 Z 序最底（桌面之上、其他窗口之下），
            // “显示桌面”后也保持可见；用户正在交互（有焦点）时暂停压底，避免被盖住
            bottomTimer.Interval = 1200;
            bottomTimer.Tick += delegate
            {
                try
                {
                    if (!IsHandleCreated || IsDisposed) { bottomTimer.Stop(); return; }
                    if (ContainsFocus) return;
                    if (Native.IsIconic(Handle)) Native.ShowWindow(Handle, 8); // SW_SHOWNOACTIVATE
                    Native.SetWindowPos(Handle, (IntPtr)1, 0, 0, 0, 0, 0x0013); // HWND_BOTTOM | NOSIZE | NOMOVE | NOACTIVATE
                }
                catch { }
            };

            // Show 之后再定位（构造期设置 Location 会被 WinForms 忽略导致位置随机）
            Shown += delegate
            {
                Rectangle wa2 = Screen.PrimaryScreen.WorkingArea;
                int px = (wantX >= wa2.Left && wantX + Width <= wa2.Right) ? wantX : wa2.Left + 40;
                int py = (wantY >= wa2.Top && wantY + Height <= wa2.Bottom) ? wantY : wa2.Top + 60;
                Location = new Point(px, py);
                Native.SetWindowPos(Handle, (IntPtr)1, 0, 0, 0, 0, 0x0013);
                bottomTimer.Start();
                hoverTimer.Start();
            };
            Load += async (s, e) => await InitAsync();
        }

        public string AppId { get { return appId; } }
        public int SavedX { get; private set; }
        public int SavedY { get; private set; }
        public int SavedW { get; private set; }
        public int SavedH { get; private set; }

        private void RepositionBarButtons()
        {
            if (btnClose == null || btnDock == null || topBar == null) return; // 基类构造期 OnResize 早于字段初始化
            btnClose.Location = new Point(topBar.ClientSize.Width - 32, 1);
            btnDock.Location = new Point(topBar.ClientSize.Width - 64, 1);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (grip == null || topBar == null || btnClose == null || btnDock == null) return;
            topBar.Width = ClientSize.Width;
            RepositionBarButtons();
            grip.Location = new Point(ClientSize.Width - grip.Width - 2, ClientSize.Height - grip.Height - 2);
        }

        /* 顶部工具条拖动（手动移动，桌面子窗口不走系统 HTCAPTION） */
        private void BarDragStart(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (btnDock.Bounds.Contains(e.Location) || btnClose.Bounds.Contains(e.Location)) return;
            dragging = true;
            dragStart = Cursor.Position;
            formStart = Location;
            topBar.Capture = true;
        }
        private void BarDragMove(object s, MouseEventArgs e)
        {
            if (!dragging) return;
            Location = new Point(formStart.X + Cursor.Position.X - dragStart.X,
                                 formStart.Y + Cursor.Position.Y - dragStart.Y);
        }
        private void BarDragEnd(object s, MouseEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            topBar.Capture = false;
            SavePos();
        }

        /* 右下角缩放 */
        private void GripDown(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            resizing = true;
            dragStart = Cursor.Position;
            sizeStart = Size;
            grip.Capture = true;
        }
        private void GripMove(object s, MouseEventArgs e)
        {
            if (!resizing) return;
            int w = Clamp(sizeStart.Width + Cursor.Position.X - dragStart.X, 260, 4000);
            int h = Clamp(sizeStart.Height + Cursor.Position.Y - dragStart.Y, 200, 3000);
            Size = new Size(w, h);
        }
        private void GripEnd(object s, MouseEventArgs e)
        {
            if (!resizing) return;
            resizing = false;
            grip.Capture = false;
            SavePos();
        }

        private void SavePos()
        {
            if (WindowState != FormWindowState.Normal) return;
            SavedX = Location.X; SavedY = Location.Y;
            SavedW = Width; SavedH = Height;
            MainFrame mf = Program.MainForm as MainFrame;
            if (mf != null && !string.IsNullOrEmpty(appId))
                mf.PushWidgetPos(appId, SavedX, SavedY, SavedW, SavedH);
        }

        /// <summary>主题切换后重刷原生部分。</summary>
        public void ApplyTheme()
        {
            BackColor = Theme.WindowBg;
            web.DefaultBackgroundColor = Theme.WindowBg;
            topBar.BackColor = Theme.WindowBg;
            foreach (IconButton b in new IconButton[] { btnDock, btnClose }) b.ApplyTheme();
            grip.Invalidate();
        }

        private async Task InitAsync()
        {
            try
            {
                CoreWebView2Environment env = await Program.GetEnvironmentAsync();
                await web.EnsureCoreWebView2Async(env);
                CoreWebView2 core = web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                // 不单独指定 PreferredColorScheme：它属于共享 profile，小部件强制写值会连带
                // 改变主窗口（钉到桌面后主界面跟着变浅）。主题由主窗口按设置/系统统一决定。
                // 页面进入“小组件模式”：隐藏常驻工具栏，改为悬浮按钮主动呼出
                string pageUrl = baseUrl + (baseUrl.IndexOf('?') >= 0 ? "&" : "?") + "widget=1";
                core.NewWindowRequested += delegate(object s, CoreWebView2NewWindowRequestedEventArgs ev)
                {
                    ev.Handled = true;
                    string u2 = ev.Uri == null ? "" : ev.Uri;
                    if (u2.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
                    WindowManager.OpenChild(u2, null, 1080, 720, null);
                    Close();
                };
                // 首帧主题：与主窗口同一判断（设置 + 系统），先注入再导航
                await core.AddScriptToExecuteOnDocumentCreatedAsync(Theme.FirstPaintScript());
                // 本地工具：按 id 注册唯一 origin 映射（用 baseUrl，不带 widget 参数）+ 下载归位
                AppHost.RegisterMappingIfNeeded(core, baseUrl);
                AppHost.WireDownload(core, appId);
                // 必须用 Navigate(字符串)：.NET 的 Uri 会把 file:// 的 "?" 转义成 "%3F"，
                // 本地文件会被当成“…时钟.html?widget=1”而报“未找到文件”
                core.Navigate(pageUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法加载页面：\n" + ex.Message, "页坞 PageDock",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private void Unparent()
        {
            // v0.7.4 起不再挂桌面层，保留空实现兼容旧调用
        }

        /// <summary>小组件内容移回主窗口标签（去掉 widget 参数）。</summary>
        private void DockBack()
        {
            bottomTimer.Stop();
            // 用 CoreWebView2.Source 原始字符串（web.Source 经 .NET Uri 会把 file:// 的 ? 变成 %3F）
            string url = null;
            try { if (web.CoreWebView2 != null) url = web.CoreWebView2.Source; } catch { }
            if (string.IsNullOrEmpty(url)) url = baseUrl;
            if (!string.IsNullOrEmpty(url))
                url = System.Text.RegularExpressions.Regex.Replace(url, "(?:[?&]|%3F|%26)widget=1", "");
            MainFrame mf = Program.MainForm as MainFrame;
            if (mf != null && !string.IsNullOrEmpty(url)) mf.OpenTabFromChild(url, Text, appId);
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            bottomTimer.Stop();
            hoverTimer.Stop();
            if (WindowState == FormWindowState.Normal)
            {
                SavedX = Location.X; SavedY = Location.Y;
                SavedW = Width; SavedH = Height;
                MainFrame mf = Program.MainForm as MainFrame;
                if (mf != null && !string.IsNullOrEmpty(appId))
                    mf.PushWidgetPos(appId, SavedX, SavedY, SavedW, SavedH);
            }
            base.OnFormClosing(e);
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }
}
