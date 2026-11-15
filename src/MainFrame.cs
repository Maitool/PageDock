using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PageDock
{
    /// <summary>
    /// 主窗口（v0.3 多标签架构）：
    /// - 主页（工作台）+ 每个应用一个标签，各自持有独立 WebView2，切换/回首页不销毁页面状态；
    /// - 同一应用始终复用同一标签；
    /// - 全局热键 Ctrl+Alt+D 呼出；主题切换同步原生部分并跟随页面色彩偏好（PreferredColorScheme）。
    /// JS 通过 window.chrome.webview.postMessage 与宿主通信。
    /// </summary>
    internal class MainFrame : ChromeForm
    {
        private readonly JavaScriptSerializer ser = new JavaScriptSerializer();
        private readonly List<TabItem> tabs = new List<TabItem>();
        private TabItem activeTab;
        private TabStrip strip;
        private FullscreenController fullscreen;
        private TabLifecycle lifecycle;
        private NotifyIcon tray;
        private bool forceExit = false;
        private bool trayEnabled = false;
        private bool hotkeyEnabled = true;
        private bool hotkeyRegistered = false;
        private string themeMode = "auto"; // auto | dark | light（来自 apps.json 的 theme 设置）
        private TabItem settingsReturnTab; // 设置弹窗以覆盖层打开时，关闭后要回到的标签

        private const int HOTKEY_ID = 0xB00B;
        private const uint MOD_ALT = 0x1;
        private const uint MOD_CONTROL = 0x2;
        private const uint MOD_SHIFT = 0x4;
        private const uint MOD_WIN = 0x8;
        private const uint MOD_NOREPEAT = 0x4000;
        private const uint VK_D = 0x44;
        private const int WM_HOTKEY = 0x0312;

        // 当前生效的全局呼出组合键（可被设置里的 set-hotkey 覆盖；默认 Ctrl+Alt+D）
        private uint hkMod = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
        private uint hkVk = VK_D;
        // 第二实例唤起主实例的跨进程消息（同名字符串在各进程解析为同一编号）
        private static readonly uint SUMMON_MSG = Native.RegisterWindowMessage("PageDock.Summon");

        public MainFrame()
        {
            // 主题已在 Program.Main 里（建窗之前）算好，这里只取模式。
            // 再刷一次底色：基类构造函数可能在主题定下来之前就跑过了。
            themeMode = Theme.ResolveFromConfig();
            BackColor = Theme.WindowBg;

            Text = "页坞 PageDock";
            StartPosition = FormStartPosition.CenterScreen;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            // 默认尺寸放大一档（1180×760 下图标网格与弹窗都偏挤），同时留 60px 边距，
            // 保证 1366×768 的笔记本也能完整放下
            Size = new Size(Math.Min(1440, wa.Width - 60), Math.Min(900, wa.Height - 60));
            MinimumSize = new Size(720, 500);
            try { Icon = PageDock.AppIcon.FormIcon(); } catch { }

            strip = new TabStrip();
            strip.TabsProvider = delegate() { return tabs; };
            strip.ActiveProvider = delegate() { return activeTab; };
            strip.OnActivate = delegate(TabItem t) { System.Threading.Tasks.Task op = ActivateTab(t); };
            strip.OnClose = delegate(TabItem t) { CloseTab(t); };
            strip.SearchChanged += async delegate(object s, string q)
            {
                // 搜索结果只呈现在首页工作台上：首页 WebView 处于隐藏状态时先把它切回前台，
                // 否则消息发进了看不见的页面，表现为“搜索没反应”。焦点要留在原生搜索框里继续打字。
                await ShowHome(false);
                TabItem home = HomeTab();
                if (home == null || home.Web == null || home.Web.CoreWebView2 == null) return;
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["event"] = "search";
                d["q"] = q;
                try { home.Web.CoreWebView2.PostWebMessageAsJson(ser.Serialize(d)); } catch { }
            };
            strip.SettingsClicked += async delegate
            {
                TabItem home = HomeTab();
                if (home == null) return;
                if (activeTab != home)
                {
                    // 设置弹窗渲染在首页里：把首页 WebView 临时盖在当前标签上（不切换活动标签），
                    // 首页 WebView 背景透明 + solo 模式只渲染弹窗，当前页面透出成为弹窗背景；
                    // 关闭时由 settings-closed 消息复位，避免“点设置就跳回工作台”。
                    await EnsureTabWeb(home);
                    if (home.Web == null) return;
                    settingsReturnTab = activeTab;
                    home.Web.Visible = true;
                    home.Web.BringToFront();
                    Dictionary<string, object> ov = new Dictionary<string, object>();
                    ov["overlay"] = true;
                    PushHomeEvent("open-settings", ov);
                }
                else
                {
                    PushHomeEvent("open-settings");
                }
            };
            strip.MinClicked += delegate { WindowState = FormWindowState.Minimized; };
            strip.MaxToggled += delegate { ToggleMaxState(); };
            strip.CloseClicked += delegate { Close(); };
            // 置顶开关：状态由首页工作台统一持久化（settings.topmost），原生图钉只发翻转事件，
            // 页面翻转后回推 set-topmost，宿主据此应用 TopMost 并点亮图钉，避免两处各写各的。
            strip.PinClicked += delegate { PushHomeEvent("toggle-topmost"); };
            Controls.Add(strip);

            // 全屏：隐藏标签栏铺满整屏，鼠标上移到屏幕顶部浮现退出按钮
            fullscreen = new FullscreenController(this, strip);
            strip.FullscreenClicked += delegate { fullscreen.Toggle(); };
            strip.DownloadClicked += delegate { OpenDownloadsTarget(); };

            BuildTrayIcon();

            // 标签生命周期：后台挂起 + 空闲 10 分钟释放 WebView（激活时惰性重建，数据在磁盘不丢）
            lifecycle = new TabLifecycle(
                delegate() { return tabs; },
                delegate() { return activeTab; },
                ReleaseTabWeb);

            Load += async (s, e) => await InitAsync();
            FormClosed += delegate { WindowManager.CloseAll(); };
        }

        /* ---------------- 托盘 ---------------- */

        private void BuildTrayIcon()
        {
            tray = new NotifyIcon();
            try { tray.Icon = Icon; } catch { }
            tray.Text = "页坞 PageDock";
            tray.Visible = false;

            MenuItem miShow = new MenuItem("显示页坞");
            miShow.Click += delegate { ShowFromTray(); };
            MenuItem miExit = new MenuItem("退出");
            miExit.Click += delegate { forceExit = true; Close(); };
            tray.ContextMenu = new ContextMenu(new MenuItem[] { miShow, new MenuItem("-"), miExit });
            tray.DoubleClick += delegate { ShowFromTray(); };
        }

        private void ShowFromTray()
        {
            // 兼容外部/系统级最小化：直接看物理状态而不是 WinForms 缓存的 WindowState
            if (WindowState == FormWindowState.Minimized || Native.IsIconic(Handle))
                Native.ShowWindow(Handle, Native.SW_RESTORE);
            Show();
            if (!ShowInTaskbar) ShowInTaskbar = true;
            // 从别的应用按热键唤起时本进程不是前台，Activate() 会被"前台锁定"忽略，
            // 表现为"按了快捷键没反应"——改用强拉前台。
            Native.ForceForeground(Handle);
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // “关闭时最小化到托盘”：拦截用户关闭，隐藏窗口常驻托盘
            if (!forceExit && e.CloseReason == CloseReason.UserClosing && trayEnabled)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
                return;
            }
            base.OnFormClosing(e);
            if (!e.Cancel) Program.Exiting = true; // 之后的关闭都是退出流程，小组件不再被当作“取消钉住”
            if (tray != null) tray.Visible = false;
            if (hotkeyRegistered)
            {
                try { Native.UnregisterHotKey(Handle, HOTKEY_ID); } catch { }
                hotkeyRegistered = false;
            }
        }

        /* ---------------- 全局热键 ---------------- */

        private void TryRegisterHotKey()
        {
            if (hotkeyRegistered) return;
            try
            {
                hotkeyRegistered = Native.RegisterHotKey(Handle, HOTKEY_ID, hkMod, hkVk);
            }
            catch { hotkeyRegistered = false; }
            // 自定义组合键被系统占用而注册失败时，回退到默认 Ctrl+Alt+D，保证仍能唤起
            if (!hotkeyRegistered && (hkVk != VK_D || hkMod != (MOD_CONTROL | MOD_ALT | MOD_NOREPEAT)))
            {
                hkMod = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT; hkVk = VK_D;
                try { hotkeyRegistered = Native.RegisterHotKey(Handle, HOTKEY_ID, hkMod, hkVk); }
                catch { hotkeyRegistered = false; }
            }
            try
            {
                File.WriteAllText(Path.Combine(Data.DataDir, "hotkey.log"),
                    "register=" + hotkeyRegistered + " mod=" + hkMod + " vk=" + hkVk + " at " + DateTime.Now.ToLongTimeString());
            }
            catch { }
        }

        private void UnregisterHotKeyNow()
        {
            if (!hotkeyRegistered) return;
            try { Native.UnregisterHotKey(Handle, HOTKEY_ID); } catch { }
            hotkeyRegistered = false;
        }

        /// <summary>
        /// 句柄（重）建后重新注册全局热键。改 ShowInTaskbar / FormBorderStyle 等会触发
        /// WinForms RecreateHandle，而 RegisterHotKey 是绑在窗口句柄上的——旧句柄销毁时其
        /// 热键被系统一并注销，若不重注册，"缩到托盘→呼出"来回一次后快捷键就彻底失效。
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (hotkeyEnabled)
            {
                hotkeyRegistered = false;
                TryRegisterHotKey();
            }
        }

        /// <summary>写下主窗口句柄与 PID，供第二实例（再次启动）定位并唤起本实例。</summary>
        private void WriteInstanceInfo()
        {
            try
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["hwnd"] = (long)Handle;
                d["pid"] = Process.GetCurrentProcess().Id;
                File.WriteAllText(Path.Combine(Data.DataDir, "instance.json"), ser.Serialize(d));
            }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                ShowFromTray();
                return;
            }
            if (SUMMON_MSG != 0 && m.Msg == (int)SUMMON_MSG)
            {
                // 第二实例发来的唤起：无论当前隐藏/最小化，都拉回前台
                ShowFromTray();
                return;
            }
            base.WndProc(ref m);
        }

        /* ---------------- WebView2 初始化 ---------------- */

        private async System.Threading.Tasks.Task InitAsync()
        {
            try
            {
                TabItem home = new TabItem();
                home.Id = "home";
                home.Title = "页坞工作台";
                home.Url = Data.UiUri;
                tabs.Add(home);
                activeTab = home;
                await ActivateTab(home);
                WriteInstanceInfo();
                TryRegisterHotKey();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "无法初始化 WebView2：\n" + ex.Message +
                    "\n\n请确认系统已安装 Microsoft Edge WebView2 Runtime（Win10/11 通常自带）。",
                    "页坞 PageDock", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private static void HandleNewWindow(CoreWebView2NewWindowRequestedEventArgs e)
        {
            // 页面里的 target=_blank / window.open 一律开独立应用窗口
            e.Handled = true;
            string u = e.Uri == null ? "" : e.Uri;
            if (u.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
            WindowManager.OpenChild(u, null, 1080, 720, null);
        }

        /* ---------------- 标签页 ---------------- */

        private TabItem HomeTab()
        {
            foreach (TabItem t in tabs) if (t.IsHome) return t;
            return null;
        }

        private async System.Threading.Tasks.Task EnsureTabWeb(TabItem t)
        {
            if (t.Web != null) return;

            WebView2 w = new WebView2();
            // 首页 WebView 需要透明背景：设置弹窗以覆盖层盖在其他标签上时，当前页面从它透出
            w.DefaultBackgroundColor = t.IsHome ? Color.Transparent : Theme.WindowBg;
            w.Visible = false;
            Controls.Add(w);

            CoreWebView2Environment env = await Program.GetEnvironmentAsync();
            await w.EnsureCoreWebView2Async(env);
            CoreWebView2 core = w.CoreWebView2;

            ApplyColorScheme(core);

            try { core.Settings.IsNonClientRegionSupportEnabled = true; } catch { }
            try { core.Settings.IsZoomControlEnabled = false; } catch { }
            // 吃掉浏览器自带加速键（Ctrl+K 等），让快捷键直达页面脚本
            try { core.Settings.AreBrowserAcceleratorKeysEnabled = false; } catch { }
            core.Settings.AreDefaultContextMenusEnabled = false;
            // 代码保护：发布版关闭 DevTools（右键菜单已禁用），拦截 view-source 查看/另存源码
            core.Settings.AreDevToolsEnabled = false;
            core.NavigationStarting += delegate(object s, CoreWebView2NavigationStartingEventArgs ev)
            {
                try { if (ev.Uri != null && ev.Uri.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase)) ev.Cancel = true; } catch { }
            };

            core.WebMessageReceived += delegate(object s, CoreWebView2WebMessageReceivedEventArgs e)
            {
                HandleMessage(t, e);
            };
            core.NewWindowRequested += delegate(object s, CoreWebView2NewWindowRequestedEventArgs ev)
            {
                HandleNewWindow(ev);
            };
            core.DocumentTitleChanged += delegate(object s, object e)
            {
                if (!string.IsNullOrEmpty(core.DocumentTitle)) t.Title = core.DocumentTitle;
                if (activeTab == t) Text = t.Title;
                strip.Invalidate();
            };

            // 注意：这里不再预设 PreferredColorScheme——
            // 默认交给 WebView2 跟随系统；页面加载后若选了固定主题，会经 set-theme 再覆盖。

            // 首帧主题脚本必须先于页面自己的脚本执行，否则会先按 WebView2 的旧色彩偏好渲染一帧
            await core.AddScriptToExecuteOnDocumentCreatedAsync(Theme.FirstPaintScript() + "\n" + ShortcutScript);

            // 本地工具：导航前注册虚拟主机映射（按 id 分配唯一 origin，天然隔离数据）
            AppHost.RegisterMappingIfNeeded(core, t.Url);
            AppHost.WireDownload(core, t.AppId);

            if (!string.IsNullOrEmpty(t.Url)) w.Source = new Uri(t.Url);

            t.Web = w;
            LayoutWebviews();
        }

        private async System.Threading.Tasks.Task ActivateTab(TabItem t) { await ActivateTab(t, true); }

        /// <summary>focusWeb=false 用于标题栏搜索：把首页亮出来但别把焦点从搜索框抢走。</summary>
        private async System.Threading.Tasks.Task ActivateTab(TabItem t, bool focusWeb)
        {
            if (t == null) return;
            if (t.IsHome) settingsReturnTab = null; // 手动回工作台视为放弃“关闭设置后返回”的记录
            await EnsureTabWeb(t);
            activeTab = t;
            Invalidate(); // 边条压暗与标签挂钩，切签后重绘
            foreach (TabItem x in tabs.ToArray())
            {
                if (x.Web == null) continue;
                bool vis = x == t;
                if (x.Web.Visible != vis) x.Web.Visible = vis;
            }
            t.Web.BringToFront();
            if (focusWeb) { try { t.Web.Focus(); } catch { } } // 焦点给页面内容，避免停在原生搜索框上
            Text = string.IsNullOrEmpty(t.Title) ? "页坞 PageDock" : t.Title;
            LayoutWebviews();
            strip.Invalidate();
            if (lifecycle != null) lifecycle.OnActivated(t);
        }

        /// <summary>空闲释放：卸载该标签的 WebView 只留元数据；下次 EnsureTabWeb 重建（磁盘数据不丢）。</summary>
        private void ReleaseTabWeb(TabItem t)
        {
            if (t == null || t.Web == null || t.IsHome) return;
            if (t == activeTab) return; // 活动标签不释放
            Controls.Remove(t.Web);
            try { t.Web.Dispose(); } catch { }
            t.Web = null;
        }

        /// <summary>下载按钮：本地工具打开其“导出成果”目录，其余打开系统下载目录。</summary>
        private void OpenDownloadsTarget()
        {
            try
            {
                string dir = null;
                TabItem t = activeTab;
                if (t != null && !string.IsNullOrEmpty(t.AppId) && AppHost.IsLocalApp(t.AppId))
                {
                    dir = Data.AppOutputDir(t.AppId);
                    Directory.CreateDirectory(dir);
                }
                if (string.IsNullOrEmpty(dir))
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch { }
        }

        private async System.Threading.Tasks.Task OpenAppTab(string url, string title, string appId)
        {
            if (string.IsNullOrEmpty(url)) return;
            TabItem existing = null;
            if (!string.IsNullOrEmpty(appId))
            {
                foreach (TabItem t in tabs.ToArray())
                {
                    if (t.AppId == appId) { existing = t; break; }
                }
            }
            if (existing == null)
            {
                TabItem t = new TabItem();
                t.Id = string.IsNullOrEmpty(appId) ? "tab:" + Guid.NewGuid().ToString("N") : "app:" + appId;
                t.AppId = appId;
                t.Title = string.IsNullOrEmpty(title) ? "新标签" : title;
                t.Url = url;
                tabs.Add(t);
                existing = t;
            }
            else
            {
                existing.Url = url; // 同一应用刷新到最新入口
            }
            await ActivateTab(existing);
        }

        /// <summary>独立窗口“移入工作台”：主窗口打开同一页面标签并前置。</summary>
        public void OpenTabFromChild(string url, string title, string appId)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (WindowState == FormWindowState.Minimized || Native.IsIconic(Handle))
                Native.ShowWindow(Handle, Native.SW_RESTORE);
            Show();
            if (!ShowInTaskbar) ShowInTaskbar = true;
            Activate();
            System.Threading.Tasks.Task op = OpenAppTab(url, title, appId);
        }

        /// <summary>升级后把该应用已打开的标签刷一遍，否则看到的还是旧版。</summary>
        private void ReloadAppTabs(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return;
            foreach (TabItem t in tabs.ToArray())
            {
                if (t.AppId != appId || t.Web == null || t.Web.CoreWebView2 == null) continue;
                try { t.Web.CoreWebView2.Reload(); } catch { }
            }
        }

        /// <summary>升级用的新版正文：拖进来时页面只能给文本（拿不到绝对路径），
        /// 右键选文件时给路径、由宿主自己读（还兼容 GBK）。</summary>
        private static string UpgradeTextOf(Dictionary<string, object> msg)
        {
            string t = Str(msg, "text");
            if (!string.IsNullOrEmpty(t)) return t;
            string p = Str(msg, "path");
            if (string.IsNullOrEmpty(p)) throw new Exception("没带上新版本的内容");
            string txt = AppHost.ReadTextAuto(Data.ResolveTarget(p));
            if (txt == null) throw new Exception("读不到这个文件");
            return txt;
        }

        private void CloseTab(TabItem t)
        {
            if (t == null || t.IsHome) return;
            int idx = tabs.IndexOf(t);
            tabs.Remove(t);
            if (t.Web != null)
            {
                Controls.Remove(t.Web);
                try { t.Web.Dispose(); } catch { }
                t.Web = null;
            }
            if (activeTab == t)
            {
                TabItem next = idx < tabs.Count ? tabs[idx] : tabs[tabs.Count - 1];
                System.Threading.Tasks.Task op = ActivateTab(next);
            }
            LayoutWebviews();
            strip.Invalidate();
        }

        private void CycleTab(int dir)
        {
            if (tabs.Count < 2) return;
            int idx = 0;
            if (activeTab != null) idx = tabs.IndexOf(activeTab);
            idx = ((idx + dir) % tabs.Count + tabs.Count) % tabs.Count;
            System.Threading.Tasks.Task op = ActivateTab(tabs[idx]);
        }

        private void ToggleMaxState()
        {
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            strip.SetMaximized(WindowState == FormWindowState.Maximized);
        }

        private void LayoutWebviews()
        {
            if (strip == null) return; // 构造早期 set_Size 会先触发 OnResize
            int top = ChromeForm.ResizeGutter + strip.Height;
            // 最大化时窗口已铺满工作区、无边可拖，那圈 ResizeGutter 只会让内嵌页面
            // （如时钟）贴不到真实屏幕边缘，令“边缘滑动呼出”热区凭空收窄 6px——故铺满。
            int side = WindowState == FormWindowState.Maximized ? 0 : ChromeForm.ResizeGutter;
            foreach (TabItem t in tabs.ToArray())
            {
                if (t.Web == null) continue;
                t.Web.Bounds = new Rectangle(
                    side, top,
                    Math.Max(1, ClientSize.Width - side * 2),
                    Math.Max(1, ClientSize.Height - top - ChromeForm.ResizeGutter));
            }
        }

        /* ---------------- 弹窗期间的边条配色 ---------------- */

        private TabItem gutterDimTab = null;

        /// <summary>
        /// 弹窗遮罩打开时，把该标签的 WebView 临时铺满左/右/下三侧缩放边条，
        /// 遮罩连同边缘一起盖住。不再原生压暗边条——原生绘制与 WebView 渲染节奏不同，
        /// 会先看到边条变灰、遮罩后到的两段式闪烁。遮罩关闭即恢复常规布局。
        /// </summary>
        public void SetGutterDim(TabItem tab, bool on)
        {
            TabItem want = on ? tab : null;
            if (gutterDimTab == want) return;
            gutterDimTab = want;
            if (tab == null || tab.Web == null) return;
            if (on)
            {
                int top = ChromeForm.ResizeGutter + (strip == null ? 0 : strip.Height);
                tab.Web.Bounds = new Rectangle(0, top, ClientSize.Width,
                    Math.Max(1, ClientSize.Height - top));
            }
            else LayoutWebviews();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutWebviews();
            if (strip != null) strip.SetMaximized(WindowState == FormWindowState.Maximized);
        }

        /* ---------------- 宿主 → UI 事件推送 ---------------- */

        public void PushChildSize(string appId, int w, int h)
        {
            TabItem home = HomeTab();
            if (home == null || home.Web == null || home.Web.CoreWebView2 == null) return;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["event"] = "child-size";
            d["appId"] = appId;
            d["w"] = w;
            d["h"] = h;
            try { home.Web.CoreWebView2.PostWebMessageAsJson(ser.Serialize(d)); } catch { }
        }

        /// <summary>小组件关闭：回传位置尺寸并同步主页的钉住状态。</summary>
        public void PushWidgetClosed(string appId, int x, int y, int w, int h)
        {
            if (Program.Exiting) return; // 退出程序时的小组件关闭不算用户取消钉住
            TabItem home = HomeTab();
            if (home == null || home.Web == null || home.Web.CoreWebView2 == null) return;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["event"] = "widget-closed";
            d["appId"] = appId;
            d["x"] = x;
            d["y"] = y;
            d["w"] = w;
            d["h"] = h;
            try { home.Web.CoreWebView2.PostWebMessageAsJson(ser.Serialize(d)); } catch { }
        }

        /// <summary>小组件拖动/缩放：位置尺寸实时回写主页配置。</summary>
        public void PushWidgetPos(string appId, int x, int y, int w, int h)
        {
            TabItem home = HomeTab();
            if (home == null || home.Web == null || home.Web.CoreWebView2 == null) return;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["event"] = "widget-pos";
            d["appId"] = appId;
            d["x"] = x;
            d["y"] = y;
            d["w"] = w;
            d["h"] = h;
            try { home.Web.CoreWebView2.PostWebMessageAsJson(ser.Serialize(d)); } catch { }
        }

        /// <summary>向主页工作台推送 UI 事件（打开添加弹窗 / 设置等）。</summary>
        private void PushHomeEvent(string name)
        {
            PushHomeEvent(name, null);
        }

        private void PushHomeEvent(string name, Dictionary<string, object> extra)
        {
            TabItem home = HomeTab();
            if (home == null || home.Web == null || home.Web.CoreWebView2 == null) return;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["event"] = name;
            if (extra != null) foreach (KeyValuePair<string, object> kv in extra) d[kv.Key] = kv.Value;
            try { home.Web.CoreWebView2.PostWebMessageAsJson(ser.Serialize(d)); } catch { }
        }

        /// <summary>
        /// 把工作台首页切到前台。首页的 WebView 在非活动标签时是 Visible=false，
        /// 此时往它里面弹窗是完全看不见的（设置／添加／搜索都踩过这个坑）。
        /// </summary>
        private async System.Threading.Tasks.Task ShowHome(bool focusWeb)
        {
            TabItem home = HomeTab();
            if (home == null || activeTab == home) return;
            await ActivateTab(home, focusWeb);
        }

        /* ---------------- JS → 宿主 消息 ---------------- */

        private async void HandleMessage(TabItem tab, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                Dictionary<string, object> msg = null;
                try { msg = ser.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); }
                catch
                {
                    // 兜底：JS 端误发字符串化 JSON 时先剥一层
                    try
                    {
                        string inner = ser.Deserialize<string>(e.WebMessageAsJson);
                        if (inner != null) msg = ser.Deserialize<Dictionary<string, object>>(inner);
                    }
                    catch { }
                }
                if (msg == null) return;

                string op = Str(msg, "op");
                string rid = Str(msg, "rid");
                bool hasReply = !string.IsNullOrEmpty(rid);
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["reply"] = rid;

                switch (op)
                {
                    case "get-state":
                        r["ok"] = true;
                        r["stateJson"] = LoadStateJson();
                        r["dataDir"] = Data.DataDir;
                        r["exeDir"] = Data.ExeDir;
                        r["version"] = Data.Version;
                        break;

                    case "save-state":
                        {
                            string json = Str(msg, "stateJson");
                            try { File.WriteAllText(Data.ConfigPath, json); r["ok"] = true; }
                            catch (Exception ex) { r["ok"] = false; r["error"] = ex.Message; }
                            break;
                        }

                    case "save-drop":
                        {
                            // 拖拽 HTML 文件：内容由页面读出后存入数据目录副本
                            string dname = Str(msg, "name");
                            string dtext = Str(msg, "text");
                            try
                            {
                                string dir = Path.Combine(Data.WebDataDir, "dropped");
                                Directory.CreateDirectory(dir);
                                string safe = string.Join("_", (dname ?? "page.html").Split(Path.GetInvalidFileNameChars()));
                                if (string.IsNullOrWhiteSpace(safe)) safe = "page.html";
                                string path = Path.Combine(dir, safe);
                                int copy = 1;
                                while (File.Exists(path))
                                {
                                    path = Path.Combine(dir, Path.GetFileNameWithoutExtension(safe) + "-" + copy + Path.GetExtension(safe));
                                    copy++;
                                }
                                File.WriteAllText(path, dtext ?? "", new System.Text.UTF8Encoding(false));
                                r["ok"] = true;
                                r["path"] = path;
                                // 默认名统一用网页内容的名称（与标签页 DocumentTitle 一致）
                                string dt = HtmlTitleFromText(dtext);
                                if (!string.IsNullOrEmpty(dt)) r["name"] = dt;
                            }
                            catch (Exception ex)
                            {
                                r["ok"] = false;
                                r["error"] = ex.Message;
                            }
                            break;
                        }

                    case "pick-file":
                        {
                            using (OpenFileDialog dlg = new OpenFileDialog())
                            {
                                dlg.Title = "选择 HTML 文件";
                                dlg.Filter = "网页 (*.html;*.htm)|*.html;*.htm|所有文件 (*.*)|*.*";
                                if (dlg.ShowDialog(this) == DialogResult.OK)
                                {
                                    r["ok"] = true;
                                    r["path"] = dlg.FileName;
                                    string ht = HtmlTitleFromFile(dlg.FileName);
                                    r["name"] = string.IsNullOrEmpty(ht)
                                        ? Path.GetFileNameWithoutExtension(dlg.FileName) : ht;
                                }
                                else { r["ok"] = false; r["cancel"] = true; }
                            }
                            break;
                        }

                    case "pick-image":
                        {
                            using (OpenFileDialog dlg = new OpenFileDialog())
                            {
                                dlg.Title = "选择图标图片";
                                dlg.Filter = "图片 (*.png;*.jpg;*.jpeg;*.ico;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.ico;*.gif;*.webp|所有文件 (*.*)|*.*";
                                if (dlg.ShowDialog(this) == DialogResult.OK)
                                {
                                    r["ok"] = true;
                                    r["path"] = dlg.FileName;
                                    r["name"] = Path.GetFileNameWithoutExtension(dlg.FileName);
                                }
                                else { r["ok"] = false; r["cancel"] = true; }
                            }
                            break;
                        }

                    case "pick-folder":
                        {
                            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
                            {
                                dlg.Description = "选择包含 index.html 的应用文件夹";
                                dlg.ShowNewFolderButton = false;
                                if (dlg.ShowDialog(this) == DialogResult.OK)
                                {
                                    r["ok"] = true;
                                    r["path"] = dlg.SelectedPath;
                                    string idx = Path.Combine(dlg.SelectedPath, "index.html");
                                    r["entry"] = File.Exists(idx) ? idx : null;
                                    // 同样优先用入口页的内容名称，否则退回文件夹名
                                    string ft = r["entry"] != null ? HtmlTitleFromFile(idx) : null;
                                    r["name"] = string.IsNullOrEmpty(ft)
                                        ? new DirectoryInfo(dlg.SelectedPath).Name : ft;
                                }
                                else { r["ok"] = false; r["cancel"] = true; }
                            }
                            break;
                        }

                    case "to-file-url":
                        r["ok"] = true;
                        r["url"] = new Uri(Data.ResolveTarget(Str(msg, "path"))).AbsoluteUri;
                        break;

                    case "app-url":
                        // 本地工具返回其专属 origin 入口 URL（https://app-<id>.local/...），url 类原样
                        {
                            string au = AppHost.ResolveAppUrl(Str(msg, "appId"));
                            r["ok"] = au != null;
                            if (au != null) r["url"] = au;
                            else r["error"] = "无法定位应用入口";
                        }
                        break;

                    case "update-app-source":
                        // 升级：用新 HTML 覆盖入口，id/origin 不变 → 工具数据自动保留
                        try
                        {
                            AppHost.UpdateSource(Str(msg, "appId"), Str(msg, "path"));
                            r["ok"] = true;
                        }
                        catch (Exception ex) { r["ok"] = false; r["error"] = ex.Message; }
                        break;

                    case "compare-upgrade":
                        // 拖入/选入新版 HTML：先只读比对，页面据此弹窗让用户确认，绝不静默覆盖
                        try
                        {
                            Dictionary<string, object> cmp = AppHost.CompareUpgrade(Str(msg, "appId"), UpgradeTextOf(msg));
                            if (cmp == null) { r["ok"] = false; r["error"] = "该应用不是本地页面，无法用文件升级"; }
                            else { r["ok"] = true; r["cmp"] = cmp; }
                        }
                        catch (Exception ex) { r["ok"] = false; r["error"] = ex.Message; }
                        break;

                    case "apply-upgrade":
                        // 用户确认过了才写盘：覆盖入口 + 旧版自动备份，origin 不变 → 数据保留
                        try
                        {
                            string aid = Str(msg, "appId");
                            string bk = AppHost.ApplyUpgrade(aid, UpgradeTextOf(msg));
                            ReloadAppTabs(aid);
                            r["ok"] = true;
                            if (bk != null) r["backup"] = bk;
                        }
                        catch (Exception ex) { r["ok"] = false; r["error"] = ex.Message; }
                        break;

                    case "export-backup":
                        {
                            string level = Str(msg, "level") == "lite" ? "lite" : "full";
                            using (SaveFileDialog dlg = new SaveFileDialog())
                            {
                                dlg.Title = "导出备份";
                                dlg.Filter = "页坞备份包 (*.zip)|*.zip";
                                dlg.FileName = "PageDock-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip";
                                if (dlg.ShowDialog(this) == DialogResult.OK)
                                {
                                    try
                                    {
                                        int cnt;
                                        long size = AppHost.ExportBackup(dlg.FileName, level, out cnt);
                                        r["ok"] = true; r["path"] = dlg.FileName; r["entries"] = cnt; r["size"] = size;
                                    }
                                    catch (Exception ex) { r["ok"] = false; r["error"] = ex.Message; }
                                }
                                else { r["ok"] = false; r["cancel"] = true; }
                            }
                            break;
                        }

                    case "import-backup":
                        {
                            using (OpenFileDialog dlg = new OpenFileDialog())
                            {
                                dlg.Title = "导入备份";
                                dlg.Filter = "页坞备份包 (*.zip)|*.zip|所有文件 (*.*)|*.*";
                                if (dlg.ShowDialog(this) == DialogResult.OK)
                                {
                                    try
                                    {
                                        bool needRestart = AppHost.ImportBackup(dlg.FileName);
                                        r["ok"] = true; r["needRestart"] = needRestart;
                                    }
                                    catch (Exception ex) { r["ok"] = false; r["error"] = ex.Message; }
                                }
                                else { r["ok"] = false; r["cancel"] = true; }
                            }
                            break;
                        }

                    case "set-autostart":
                        try { AppHost.SetAutostart(BoolVal(msg, "on", false)); r["ok"] = true; r["on"] = AppHost.GetAutostart(); }
                        catch (Exception ex) { r["ok"] = false; r["error"] = ex.Message; }
                        break;

                    case "get-autostart":
                        r["ok"] = true; r["on"] = AppHost.GetAutostart();
                        break;
                    
                    case "set-dim":
                        // 弹窗遮罩打开时，把窗口四周的缩放边条刷成遮罩色，否则边条会留出一圈白线
                        SetGutterDim(tab, BoolVal(msg, "on", false));
                        r["ok"] = true;
                        break;

                    case "settings-closed":
                        if (settingsReturnTab != null)
                        {
                            TabItem back = settingsReturnTab;
                            settingsReturnTab = null;
                            if (back != activeTab && tabs.Contains(back)) await ActivateTab(back);
                            else
                            {
                                TabItem home2 = HomeTab();
                                if (home2 != null && home2.Web != null && home2.Web.Visible && activeTab != home2)
                                    home2.Web.Visible = false;
                            }
                            // 首页 WebView 已隐藏/复位，此时再撤销 solo（只留弹窗）模式，
                            // 顺序反了会让工作台内容在关闭瞬间闪现在当前标签上
                            PushHomeEvent("settings-solo-off");
                        }
                        r["ok"] = true;
                        break;
                    
                    case "open-child":
                        WindowManager.OpenChild(Str(msg, "url"), Str(msg, "title"),
                            IntVal(msg, "width", 1080), IntVal(msg, "height", 720), Str(msg, "appId"));
                        r["ok"] = true;
                        break;

                    case "open-widget":
                        WindowManager.OpenWidget(Str(msg, "url"), Str(msg, "title"), Str(msg, "appId"),
                            IntVal(msg, "x", -1), IntVal(msg, "y", -1),
                            IntVal(msg, "width", 0), IntVal(msg, "height", 0));
                        r["ok"] = true;
                        break;

                    case "close-widget":
                        WindowManager.CloseWidget(Str(msg, "appId"));
                        r["ok"] = true;
                        break;

                    case "open-tab":
                        await OpenAppTab(Str(msg, "url"), Str(msg, "title"), Str(msg, "appId"));
                        r["ok"] = true;
                        break;

                    case "go-home":
                        await ActivateTab(HomeTab());
                        r["ok"] = true;
                        break;

                    case "focus-search":
                        strip.FocusSearch();
                        hasReply = false;
                        break;

                    case "clear-search":
                        strip.ClearSearch();
                        hasReply = false;
                        break;

                    case "cycle-tab":
                        CycleTab(IntVal(msg, "dir", 1));
                        hasReply = false;
                        break;

                    case "open-external":
                        try
                        {
                            Process.Start(new ProcessStartInfo(Str(msg, "url")) { UseShellExecute = true });
                            r["ok"] = true;
                        }
                        catch (Exception ex) { r["ok"] = false; r["error"] = ex.Message; }
                        break;

                    case "reveal":
                        try
                        {
                            // 路径归一（修复尾随反斜杠拼出的 \\spec 导致 explorer 落到桌面的问题）；
                            // 文件不存在时回退到 exe 目录下的规范文档，再不行就打开所在目录
                            string rp = Str(msg, "path");
                            if (string.IsNullOrEmpty(rp)) throw new Exception("缺少路径");
                            rp = Path.GetFullPath(rp);
                            if (Directory.Exists(rp))
                            {
                                // 目录：直接打开（数据目录等），绝不当文件回退
                                Process.Start("explorer.exe", "\"" + rp + "\"");
                                r["ok"] = true;
                                break;
                            }
                            else if (File.Exists(rp))
                                Process.Start("explorer.exe", "/select,\"" + rp + "\"");
                            else
                            {
                                string alt = Path.Combine(Data.ExeDir ?? "", "spec", "pagedock-web-ui.md");
                                if (File.Exists(alt)) { Process.Start("explorer.exe", "/select,\"" + alt + "\""); r["ok"] = true; break; }
                                string dir = Path.GetDirectoryName(rp);
                                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                                    dir = Data.ExeDir ?? "";
                                Process.Start("explorer.exe", "\"" + dir + "\"");
                            }
                            r["ok"] = true;
                        }
                        catch (Exception ex) { r["ok"] = false; r["error"] = ex.Message; }
                        break;

                    case "open-devtools":
                        if (tab.Web != null) tab.Web.CoreWebView2.OpenDevToolsWindow();
                        r["ok"] = true;
                        break;

                    case "set-theme":
                        {
                            string mode = Str(msg, "mode");
                            if (mode == "auto" || mode == "dark" || mode == "light") themeMode = mode;
                            else themeMode = BoolVal(msg, "dark", true) ? "dark" : "light";
                            Theme.Dark = BoolVal(msg, "dark", true);
                            BackColor = Theme.WindowBg;
                            WindowManager.ApplyThemeToAll();
                            ApplyColorSchemeToAll();
                            if (strip != null) strip.ApplyTheme(); // 同步刷新搜索框/按钮等子控件配色
                            r["ok"] = true;
                        }
                        break;

                    case "set-close-mode":
                        trayEnabled = Str(msg, "mode") == "tray";
                        if (tray != null) tray.Visible = trayEnabled && !forceExit;
                        r["ok"] = true;
                        break;

                    case "set-hotkey":
                        hotkeyEnabled = BoolVal(msg, "enabled", true);
                        {
                            int mk = IntVal(msg, "mod", -1);
                            int vk = IntVal(msg, "vk", -1);
                            if (mk >= 0) hkMod = (uint)mk | MOD_NOREPEAT;
                            if (vk >= 0) hkVk = (uint)vk;
                        }
                        UnregisterHotKeyNow();
                        if (hotkeyEnabled) TryRegisterHotKey();
                        r["ok"] = true;
                        r["registered"] = hotkeyRegistered;
                        break;

                    case "set-topmost":
                        {
                            bool on = BoolVal(msg, "on", false);
                            TopMost = on;
                            if (strip != null) strip.SetPinned(on);
                            r["ok"] = true;
                        }
                        break;

                    case "drag-window":
                        Native.StartWindowDrag(Handle);
                        hasReply = false;
                        break;

                    case "toggle-max":
                        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                        hasReply = false;
                        break;

                    case "min-window":
                        WindowState = FormWindowState.Minimized;
                        hasReply = false;
                        break;

                    case "close-window":
                        Close();
                        hasReply = false;
                        break;

                    default:
                        r["ok"] = false;
                        r["error"] = "未知操作: " + op;
                        break;
                }

                if (hasReply) PostReply(tab, r);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "处理消息出错：" + ex.Message, "页坞 PageDock",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ApplyColorSchemeToAll()
        {
            foreach (TabItem t in tabs.ToArray())
            {
                try
                {
                    if (t.Web != null && t.Web.CoreWebView2 != null)
                        ApplyColorScheme(t.Web.CoreWebView2);
                }
                catch { }
            }
        }

        /// <summary>
        /// 按当前主题模式设置页面色彩偏好。
        /// 关键：auto 必须设为 Auto（跟随系统）——PreferredColorScheme 是 profile 级且会被记住，
        /// 一旦残留 Dark，页面的 prefers-color-scheme 就恒为深色，“跟随系统”永远算不出浅色。
        /// </summary>
        private void ApplyColorScheme(CoreWebView2 core)
        {
            try
            {
                core.Profile.PreferredColorScheme = themeMode == "auto"
                    ? CoreWebView2PreferredColorScheme.Auto
                    : (Theme.Dark ? CoreWebView2PreferredColorScheme.Dark
                                  : CoreWebView2PreferredColorScheme.Light);
            }
            catch { }
        }

        /// <summary>读取保存的主题设置：auto / dark / light（缺省 auto）。</summary>
        private string ReadThemeMode()
        {
            return Theme.ResolveFromConfig();
        }

        private void PostReply(TabItem tab, Dictionary<string, object> r)
        {
            if (tab == null || tab.Web == null || tab.Web.CoreWebView2 == null) return;
            tab.Web.CoreWebView2.PostWebMessageAsJson(ser.Serialize(r));
        }

        /// <summary>
        /// 取网页内容的名称（优先 &lt;title&gt;，其次 H1），与标签页显示的 DocumentTitle 保持一致，
        /// 避免“卡片用文件名、标签用网页名”两套叫法。拿不到则返回 null 由调用方回退文件名。
        /// </summary>
        private static string HtmlTitleFromFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                byte[] all = File.ReadAllBytes(path);
                int n = Math.Min(all.Length, 65536);          // 标题必在头部，不必读完整个文件
                string head = null;
                try { head = new System.Text.UTF8Encoding(false, true).GetString(all, 0, n); }
                catch { head = null; }                          // 非 UTF-8（如 GBK 页面）
                string t = HtmlTitleFromText(head);
                if (t == null)
                {
                    try { t = HtmlTitleFromText(System.Text.Encoding.Default.GetString(all, 0, n)); }
                    catch { }
                }
                return t;
            }
            catch { return null; }
        }

        internal static string HtmlTitleFromText(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string t = BetweenTag(text, "title");
            if (string.IsNullOrEmpty(t)) t = BetweenTag(text, "h1");
            return CleanTitle(t);
        }

        private static string BetweenTag(string s, string tag)
        {
            string lower = s.ToLowerInvariant();
            int a = lower.IndexOf("<" + tag);
            if (a < 0) return null;
            int gt = s.IndexOf('>', a);
            if (gt < 0) return null;
            int b = lower.IndexOf("</" + tag, gt + 1);
            if (b < 0) return null;
            return s.Substring(gt + 1, b - gt - 1);
        }

        /// <summary>洗掉内嵌标签与实体，压缩空白，并限制到名称输入框的 40 字上限。</summary>
        private static string CleanTitle(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string s = raw;
            int guard = 0;
            while (true)
            {
                int lt = s.IndexOf('<');
                if (lt < 0) break;
                int gt = s.IndexOf('>', lt);
                if (gt < 0) { s = s.Substring(0, lt); break; }
                s = s.Remove(lt, gt - lt + 1);
                if (++guard > 32) break;
            }
            s = DecodeEntities(s);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            bool space = false;
            foreach (char ch in s)
            {
                if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n' || ch == '\u00a0')
                {
                    if (sb.Length > 0) space = true;
                    continue;
                }
                if (ch < ' ') continue;
                if (space) { sb.Append(' '); space = false; }
                sb.Append(ch);
            }
            string outS = sb.ToString().Trim();
            if (outS.Length == 0) return null;
            if (outS.Length > 40) outS = outS.Substring(0, 40).Trim();
            return outS.Length == 0 ? null : outS;
        }

        private static string DecodeEntities(string s)
        {
            if (s.IndexOf('&') < 0) return s;
            s = s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                 .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&nbsp;", " ");
            int i = 0;
            while (true)
            {
                int a = s.IndexOf("&#", i);
                if (a < 0) break;
                int b = s.IndexOf(';', a);
                if (b < 0) break;
                string num = s.Substring(a + 2, b - a - 2);
                bool hex = num.StartsWith("x", StringComparison.OrdinalIgnoreCase);
                int code;
                try { code = hex ? Convert.ToInt32(num.Substring(1), 16) : int.Parse(num); }
                catch { i = b + 1; continue; }
                string rep;
                try { rep = (code > 0 && code < 0x10000) ? char.ConvertFromUtf32(code) : ""; }
                catch { rep = ""; }
                s = s.Substring(0, a) + rep + s.Substring(b + 1);
                i = a + rep.Length;
            }
            return s;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return null;
        }

        private static int IntVal(Dictionary<string, object> d, string key, int def)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToInt32(v); } catch { }
            }
            return def;
        }

        private static bool BoolVal(Dictionary<string, object> d, string key, bool def)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToBoolean(v); } catch { }
            }
            return def;
        }

        private static string LoadStateJson()
        {
            try
            {
                if (File.Exists(Data.ConfigPath)) return File.ReadAllText(Data.ConfigPath);
            }
            catch { }
            return null;
        }

        /// <summary>注入到所有标签（含工作台）：Ctrl+Alt+→/← 循环切换标签，Ctrl+K 聚焦搜索框（避开浏览器加速键）。</summary>
        private const string ShortcutScript = @"
(function () {
  window.addEventListener('keydown', function (e) {
    if (e.ctrlKey && e.altKey && (e.key === 'ArrowRight' || e.key === 'ArrowLeft')) {
      e.preventDefault();
      try { window.chrome.webview.postMessage({ op: 'cycle-tab', dir: e.key === 'ArrowRight' ? 1 : -1 }); } catch (err) { }
    }
    if (e.ctrlKey && (e.key === 'k' || e.key === 'K')) {
      e.preventDefault();
      try { window.chrome.webview.postMessage({ op: 'focus-search' }); } catch (err) { }
    }
  });
})();";
    }
}
