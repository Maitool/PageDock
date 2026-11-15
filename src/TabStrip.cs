using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace PageDock
{
    /// <summary>一个标签 = 主窗口里的一个常驻页面（独立 WebView2，切换不丢状态）。</summary>
    internal class TabItem
    {
        public string Id;          // "home" 或应用 id
        public string Title;
        public string Url;
        public string AppId;       // null = 主页
        public WebView2 Web;       // 惰性创建
        public DateTime LastActive = DateTime.UtcNow; // 空闲释放判定（TabLifecycle 维护）

        public bool IsHome { get { return Id == "home"; } }
    }

    /// <summary>
    /// 主窗口导航/标签栏（自绘）：左侧 logo 标签 + 应用标签（图标+名称），
    /// 右侧固定簇：搜索框 / ＋添加应用 / 设置 / 最小化 / 最大化 / 关闭。
    /// 空白区域可拖动窗口，双击空白最大化。
    /// </summary>
    internal class TabStrip : Panel
    {
        public Func<List<TabItem>> TabsProvider;
        public Func<TabItem> ActiveProvider;
        public Action<TabItem> OnActivate;
        public Action<TabItem> OnClose;
        public event EventHandler MaxToggled;
        public event EventHandler SettingsClicked;
        public event EventHandler PinClicked;
        public event EventHandler FullscreenClicked;
        public event EventHandler DownloadClicked;
        public event EventHandler<string> SearchChanged;

        public readonly TextBox Search;
        public readonly IconButton BtnSettings, BtnMin, BtnMax, BtnClose, BtnPin, BtnFull, BtnDl;

        private int hoverIndex = -1;
        private readonly List<Rectangle> tabRects = new List<Rectangle>();
        private readonly Label searchPlaceholder;
        private readonly ToolTip tips;
        private int clusterPillH = 34;   // 搜索框可见胶囊与右侧按钮 hover 胶囊的统一高度（由 RelayoutCluster 按 DPI 赋值）
        private Rectangle searchPill = Rectangle.Empty;

        public TabStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            Dock = DockStyle.Top;
            Font = new Font("Microsoft YaHei UI", 9f);

            tips = new ToolTip();

            Search = new TextBox();
            Search.BorderStyle = BorderStyle.None;
            Search.Font = new Font("Microsoft YaHei UI", 9f);
            Search.TextChanged += delegate
            {
                searchPlaceholder.Visible = Search.Text.Length == 0 && !Search.Focused;
                if (SearchChanged != null) SearchChanged(this, Search.Text);
            };
            Search.GotFocus += delegate { searchPlaceholder.Visible = false; };
            Search.LostFocus += delegate { searchPlaceholder.Visible = Search.Text.Length == 0; };

            searchPlaceholder = new Label();
            searchPlaceholder.Text = "搜索应用…  Ctrl K";
            searchPlaceholder.ForeColor = Theme.TextDim;
            searchPlaceholder.BackColor = Color.Transparent;
            searchPlaceholder.Cursor = Cursors.IBeam;
            searchPlaceholder.Click += delegate { Search.Focus(); };

            BtnSettings = new IconButton();
            BtnSettings.Kind = IconButton.IconKind.Settings;
            BtnSettings.Click += delegate { if (SettingsClicked != null) SettingsClicked(this, EventArgs.Empty); };

            BtnPin = new IconButton();
            BtnPin.Kind = IconButton.IconKind.Pin;
            BtnPin.Click += delegate { if (PinClicked != null) PinClicked(this, EventArgs.Empty); };

            BtnFull = new IconButton();
            BtnFull.Kind = IconButton.IconKind.Fullscreen;
            BtnFull.Click += delegate { if (FullscreenClicked != null) FullscreenClicked(this, EventArgs.Empty); };

            BtnDl = new IconButton();
            BtnDl.Kind = IconButton.IconKind.Download;
            BtnDl.Click += delegate { if (DownloadClicked != null) DownloadClicked(this, EventArgs.Empty); };

            BtnMin = new IconButton();
            BtnMin.Kind = IconButton.IconKind.Min;
            BtnMin.Click += delegate { OnMinClicked(); };

            BtnMax = new IconButton();
            BtnMax.Kind = IconButton.IconKind.Max;
            BtnMax.Click += delegate { if (MaxToggled != null) MaxToggled(this, EventArgs.Empty); };

            BtnClose = new IconButton();
            BtnClose.Kind = IconButton.IconKind.Close;
            BtnClose.DangerHover = true;
            BtnClose.Click += delegate { OnCloseClicked(); };

            Controls.Add(Search);
            Controls.Add(searchPlaceholder);
            Controls.Add(BtnSettings);
            Controls.Add(BtnPin);
            Controls.Add(BtnDl);
            Controls.Add(BtnFull);
            Controls.Add(BtnMin);
            Controls.Add(BtnMax);
            Controls.Add(BtnClose);
            searchPlaceholder.BringToFront(); // 否则被不透明的 Search 压在下面

            ApplyScale(); // 栏高/标签/搜索框按 DPI 缩放（Handle 未建时先按当前 DPI 估一次）
            ApplyTheme();
            tips.SetToolTip(BtnSettings, "设置");
            tips.SetToolTip(BtnPin, "窗口置顶");
            tips.SetToolTip(BtnDl, "下载（本地工具的导出成果 / 浏览器下载目录）");
            tips.SetToolTip(BtnFull, "全屏（隐藏顶栏，鼠标移到屏幕顶部退出）");
            tips.SetToolTip(BtnMin, "最小化");
            tips.SetToolTip(BtnMax, "最大化 / 还原");
            tips.SetToolTip(BtnClose, "关闭");
        }

        public event EventHandler MinClicked;
        public event EventHandler CloseClicked;

        private void OnMinClicked()
        {
            if (MinClicked != null) MinClicked(this, EventArgs.Empty);
        }

        private void OnCloseClicked()
        {
            if (CloseClicked != null) CloseClicked(this, EventArgs.Empty);
        }

        /// <summary>右侧固定簇宽度（标签绘制止步于此）。</summary>
        private int RightReserve()
        {
            return Width - Search.Left - 10;
        }

        private void RelayoutCluster()
        {
            if (Search == null) return; // 构造早期设 Height 会先触发 OnResize
            int bh = S(40);
            clusterPillH = S(34);                     // 统一：搜索框胶囊与按钮 hover 胶囊同高
            int pillY = (Height - clusterPillH) / 2;  // 统一垂直中心 = 栏高中心
            int right = Width - S(6);
            BtnClose.Bounds = new Rectangle(right - S(40), (Height - bh) / 2, S(40), bh); right -= S(40);
            BtnMax.Bounds = new Rectangle(right - S(38), (Height - bh) / 2, S(38), bh); right -= S(38);
            BtnMin.Bounds = new Rectangle(right - S(38), (Height - bh) / 2, S(38), bh); right -= S(38);
            BtnSettings.Bounds = new Rectangle(right - S(38), (Height - bh) / 2, S(38), bh); right -= S(38);
            BtnDl.Bounds = new Rectangle(right - S(38), (Height - bh) / 2, S(38), bh); right -= S(38);
            BtnPin.Bounds = new Rectangle(right - S(38), (Height - bh) / 2, S(38), bh); right -= S(38);
            BtnFull.Bounds = new Rectangle(right - S(38), (Height - bh) / 2, S(38), bh); right -= S(38);
            // 按钮 hover 胶囊与搜索框同高同圆角（IconButton 自身按 Height/2 居中，按钮 bounds 也已居中 → 胶囊中心对齐）
            foreach (IconButton b in new IconButton[] { BtnClose, BtnMax, BtnMin, BtnSettings, BtnPin, BtnDl, BtnFull })
            { b.PillHeight = clusterPillH; b.PillRadius = S(9); b.Invalidate(); }
            // 输入框内缩进胶囊的直边区：单行 Edit 顶部对齐且高耗尺随字体，整块贴满胶囊
            // 会盖住圆角并把光标裁掉，这里只在四边留出内边距，圆角全部由底色胶囊呈现。
            searchPill = new Rectangle(right - S(280), pillY, S(280), clusterPillH);
            int inputH = Search.Font.Height + S(2);
            if (inputH > clusterPillH - S(4)) inputH = clusterPillH - S(4);
            Search.Bounds = new Rectangle(
                searchPill.Left + S(12), pillY + (clusterPillH - inputH) / 2,
                searchPill.Width - S(24), inputH);
            right -= S(288);
            // 占位文字：垂直居中到统一胶囊中心（此前从 Search.Top+S(2) 起、Label 默认顶部对齐 → 文字偏高，与右侧图标不齐）
            searchPlaceholder.TextAlign = ContentAlignment.MiddleLeft;
            searchPlaceholder.Bounds = new Rectangle(searchPill.Left + S(12), pillY, searchPill.Width - S(70), clusterPillH);
        }

        // ---- DPI 缩放 ----------------------------------------------------
        // 无边框窗体的自绘界面用的是设备像素：WebView2 页面会跟随系统缩放，原生栏若不缩放，
        // 高分屏上就只剩一条细缝（例如 250% 时 44px ≈ 17.6 逻辑像素）。这里统一按 DPI 换算。
        private float ui = 1f;
        private int tabH = 32, tabIcon = 22, tabGap = 3;

        private int S(int v) { return (int)Math.Round(v * ui); }

        // 标签内文字起点：左距 5 + 图标 + 间 2
        private int TextX() { return S(5) + tabIcon + S(2); }

        private void ApplyScale()
        {
            int dpi = 96;
            try { dpi = DeviceDpi; } catch { }
            if (dpi <= 0) dpi = 96;
            float f = dpi / 96f;
            bool changed = Math.Abs(f - ui) > 0.001f;
            ui = f;
            tabH = S(32);
            tabIcon = S(22);
            tabGap = S(3);
            if (changed) { LetterCache.Clear(); AppIcon.ResetCache(); }
            int barH = S(56); // 48 → 56：标签与窗口按钮上下更宽松，高分屏按比例一起放大
            if (Height != barH) Height = barH;
            RelayoutCluster();
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyScale();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplyScale();
        }

        public void FocusSearch()
        {
            Search.Focus();
            Search.SelectAll();
        }

        public void ClearSearch()
        {
            Search.Text = "";
        }

        public void SetMaximized(bool max)
        {
            BtnMax.Kind = max ? IconButton.IconKind.Restore : IconButton.IconKind.Max;
            BtnMax.Invalidate();
        }

        /// <summary>置顶生效时把图钉按钮点亮（强调色 + 帽内实心点）。</summary>
        public void SetPinned(bool on)
        {
            BtnPin.Active = on;
            BtnPin.Invalidate();
        }

        public void ApplyTheme()
        {
            BackColor = Theme.WindowBg;
            Search.BackColor = Theme.HoverSoft;
            Search.ForeColor = Theme.Text;
            searchPlaceholder.ForeColor = Theme.TextDim;
            foreach (IconButton b in new IconButton[] { BtnSettings, BtnPin, BtnMin, BtnMax, BtnClose, BtnDl, BtnFull })
                b.ApplyTheme();
            Invalidate();
        }

        private int TabWidth(TabItem t)
        {
            // 标签 = 图标 + 名称，自然宽度（首页标签显示名固定为“页坞”，不再按长标题撑宽）
            // 布局：左距 5 + 图标 + 间 4 + 文本 + 右距（首页 6，应用标签 16 留关闭点）
            string text = t.IsHome ? "页坞" : (string.IsNullOrEmpty(t.Title) ? "新标签" : t.Title);
            int w = TextX() + TextRenderer.MeasureText(text, Font).Width + (t.IsHome ? S(6) : S(16));
            return Math.Min(w, S(170));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RelayoutCluster();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.WindowBg);

            // 搜索框圆角底：与 TextBox 完全重合，只负责绘制 9px 圆角；文本框同宽同高后右端不会再露方角
            using (SolidBrush b = new SolidBrush(Theme.HoverSoft))
                FillRound(g, b, searchPill, S(9));

            List<TabItem> tabs = TabsProvider != null ? TabsProvider() : null;
            TabItem active = ActiveProvider != null ? ActiveProvider() : null;
            tabRects.Clear();
            if (tabs == null) return;

            int tabsMaxX = searchPill.Left - S(18);
            int x = S(8);
            for (int i = 0; i < tabs.Count; i++)
            {
                TabItem t = tabs[i];
                int w = TabWidth(t);
                if (x + w > tabsMaxX) break;
                Rectangle r = new Rectangle(x, (Height - tabH) / 2, w, tabH);
                tabRects.Add(r);

                bool isActive = active == t;
                bool isHover = i == hoverIndex && !isActive;

                // logo 标签永远不带底色（避免左上角灰矩形）
                if (!t.IsHome && (isActive || isHover))
                {
                    using (SolidBrush b = new SolidBrush(
                        isActive ? Blend(Theme.WindowBg, Theme.Text, 0.07f) :
                        Blend(Theme.WindowBg, Theme.Text, 0.045f)))
                        FillRound(g, b, r, S(8));
                }

                // 图标：主页 = 程序自己的 app.ico（与任务栏/Alt-Tab 同一份图），
                // 应用 = 首字母色块
                Rectangle ir = new Rectangle(r.X + S(5), r.Y + (tabH - tabIcon) / 2, tabIcon, tabIcon);
                if (t.IsHome) DrawLogo(g, ir);
                else DrawLetterTile(g, t, ir);

                Color txt = isActive ? Theme.Text : Theme.TextDim;
                int tx = TextX();
                TextRenderer.DrawText(g, t.IsHome ? "页坞" : t.Title, Font,
                    new Rectangle(r.X + tx, r.Y, r.Width - tx - (t.IsHome ? S(6) : S(16)), r.Height),
                    txt, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                // 应用标签：悬停显示关闭点
                if (!t.IsHome && i == hoverIndex)
                {
                    Rectangle cr = new Rectangle(r.Right - S(16), r.Y, S(16), r.Height);
                    TextRenderer.DrawText(g, "✕", Font, cr, Theme.TextDim,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                }

                x += w + tabGap;
            }

            using (Pen p = new Pen(Theme.Border))
                g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }

        /// <summary>左上角 logo：直接用程序图标（与 exe/任务栏同一张图），不再另画一套。</summary>
        private void DrawLogo(Graphics g, Rectangle r)
        {
            Bitmap ico = AppIcon.Get(r.Width);
            if (ico != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(ico, r);
                return;
            }
            // 资源缺失时的兜底：强调色圆角块
            using (SolidBrush b = new SolidBrush(Theme.Accent))
                FillRound(g, b, r, S(6));
        }

        private static readonly Dictionary<string, Bitmap> LetterCache = new Dictionary<string, Bitmap>();

        private void DrawLetterTile(Graphics g, TabItem t, Rectangle r)
        {
            Color c = HashColor(t.Title);
            using (SolidBrush b = new SolidBrush(Blend(Theme.WindowBg, c, 0.85f)))
                FillRound(g, b, r, S(6));
            string s = (t.Title ?? "?").Trim();
            if (s.Length == 0) s = "?";
            string letter = s.Substring(0, 1).ToUpper();
            Bitmap icon = GetLetterIcon(letter);
            if (icon == null) return;
            // 与左上角的 app.png 同宽高，不再让超采样字母从图标框里“胀”出去
            Rectangle letterRect = new Rectangle(r.X, r.Y, r.Width, r.Height);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(icon, letterRect);
        }

        /// <summary>字母图标：超采样渲染后按墨迹包围盒光学居中（GDI 行高/字体链接会让 DrawText 偏移）。</summary>
        private Bitmap GetLetterIcon(string letter)
        {
            Bitmap cached;
            if (LetterCache.TryGetValue(letter, out cached)) return cached;
            int big = Math.Max(48, tabIcon * 4); // 超采样尺寸跟着 DPI/图标尺寸走，高分屏不发虚
            Bitmap src = new Bitmap(big, big);
            using (Graphics g = Graphics.FromImage(src))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                // 0.62 是整张画布的字号，再按墨迹包围盒铺满 22px 标签框后会明显大于 logo；
                // 缩到 0.38 并保留内边距，字母视觉面积才与程序 logo 一致。
                using (Font f = new Font("Microsoft YaHei UI", big * 0.38f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(letter, f, Brushes.White, new RectangleF(0, 0, big, big), sf);
                }
            }
            int minX = big, minY = big, maxX = -1, maxY = -1;
            for (int y = 0; y < big; y++)
                for (int x = 0; x < big; x++)
                {
                    if (src.GetPixel(x, y).A > 40)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            int pad = Math.Max(2, big / 24);
            minX = Math.Max(0, minX - pad); minY = Math.Max(0, minY - pad);
            maxX = Math.Min(big - 1, maxX + pad); maxY = Math.Min(big - 1, maxY + pad);
            Bitmap icon = new Bitmap(tabIcon * 3, tabIcon * 3);
            using (Graphics g = Graphics.FromImage(icon))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, new Rectangle(0, 0, icon.Width, icon.Height),
                    new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1), GraphicsUnit.Pixel);
            }
            src.Dispose();
            LetterCache[letter] = icon;
            return icon;
        }

        private static readonly Color[] Palette = new Color[] {
            Color.FromArgb(45,212,191), Color.FromArgb(122,162,247), Color.FromArgb(245,158,11),
            Color.FromArgb(167,139,250), Color.FromArgb(52,211,153), Color.FromArgb(244,114,182),
            Color.FromArgb(96,165,250), Color.FromArgb(251,191,36), Color.FromArgb(74,222,128),
            Color.FromArgb(251,146,60)
        };

        private static Color HashColor(string s)
        {
            uint h = 0;
            s = s ?? "?";
            for (int i = 0; i < s.Length; i++) h = (uint)((h * 31 + s[i]) & 0xFFFFFFFF);
            return Palette[h % Palette.Length];
        }

        private static void FillRound(Graphics g, Brush b, Rectangle r, int rad)
        {
            using (GraphicsPath path = RoundPath(r, rad))
                g.FillPath(b, path);
        }

        internal static GraphicsPath RoundPath(Rectangle r, int rad)
        {
            GraphicsPath path = new GraphicsPath();
            int d = rad * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < tabRects.Count; i++)
                if (tabRects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            // 空白区（非标签、非右侧控件）按下 → 拖动窗口；双击最大化由 OnMouseDoubleClick 处理
            if (e.Button == MouseButtons.Left && HitTest(e.Location) < 0)
            {
                Form f = FindForm();
                if (f != null) Native.StartWindowDrag(f.Handle);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int idx = HitTest(e.Location);
            if (idx != hoverIndex)
            {
                hoverIndex = idx;
                Cursor = idx >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverIndex != -1) { hoverIndex = -1; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            List<TabItem> tabs = TabsProvider != null ? TabsProvider() : null;
            if (tabs == null) return;
            int idx = HitTest(e.Location);
            if (idx < 0 || idx >= tabs.Count) return;
            TabItem t = tabs[idx];

            bool overClose = false;
            if (!t.IsHome)
            {
                Rectangle r = tabRects[idx];
                Rectangle cr = new Rectangle(r.Right - S(18), r.Y, S(18), r.Height);
                overClose = cr.Contains(e.Location);
            }

            if (e.Button == MouseButtons.Middle) { if (OnClose != null && !t.IsHome) OnClose(t); return; }
            if (e.Button != MouseButtons.Left) return;
            if (overClose) { if (OnClose != null) OnClose(t); }
            else if (OnActivate != null) OnActivate(t);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (HitTest(e.Location) < 0 && MaxToggled != null) MaxToggled(this, EventArgs.Empty);
        }
    }
}
