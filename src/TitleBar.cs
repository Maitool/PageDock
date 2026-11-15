using System;
using System.Drawing;
using System.Windows.Forms;

namespace PageDock
{
    /// <summary>
    /// 独立应用窗口标题栏：统一 IconButton（─ ▢ ✕，关闭靠右，与主窗口一致）。
    /// 按下标题栏即拖动窗口（ReleaseCapture + HTCAPTION），双击切换最大化。
    /// </summary>
    internal class TitleBar : Panel
    {
        public readonly Label Title;
        private readonly IconButton btnPin;
        private readonly IconButton btnFull;
        private readonly IconButton btnDock;
        private readonly IconButton btnMin;
        private readonly IconButton btnMax;
        private readonly IconButton btnClose;
        private readonly ToolTip tips = new ToolTip();

        /// <summary>双击标题栏时触发（由窗体切换最大化状态）。</summary>
        public event EventHandler MaxToggled;
        /// <summary>点击“移入工作台”按钮时触发。</summary>
        public event EventHandler DockClicked;
        /// <summary>点击“窗口置顶”图钉时触发。</summary>
        public event EventHandler PinClicked;
        public event EventHandler FullscreenClicked;

        public TitleBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            Dock = DockStyle.Top;
            Height = 36;
            BackColor = Theme.WindowBg;
            Font = new Font("Microsoft YaHei UI", 9f);

            Title = new Label();
            Title.Text = "页坞";
            Title.AutoSize = false;
            Title.Dock = DockStyle.Fill;
            Title.TextAlign = ContentAlignment.MiddleLeft;
            Title.Padding = new Padding(12, 0, 0, 0);
            Title.ForeColor = Theme.TitleText;
            Title.Font = new Font("Microsoft YaHei UI", 9.5f);
            Title.AutoEllipsis = true;

            btnClose = new IconButton();
            btnClose.Kind = IconButton.IconKind.Close;
            btnClose.DangerHover = true;
            btnClose.Dock = DockStyle.Right;
            btnClose.Size = new Size(40, 36);
            btnMax = new IconButton();
            btnMax.Kind = IconButton.IconKind.Max;
            btnMax.Dock = DockStyle.Right;
            btnMax.Size = new Size(34, 36);
            btnMin = new IconButton();
            btnMin.Kind = IconButton.IconKind.Min;
            btnMin.Dock = DockStyle.Right;
            btnMin.Size = new Size(34, 36);
            btnDock = new IconButton();
            btnDock.Kind = IconButton.IconKind.DockIn;
            btnDock.Dock = DockStyle.Right;
            btnDock.Size = new Size(34, 36);
            btnPin = new IconButton();
            btnPin.Kind = IconButton.IconKind.Pin;
            btnPin.Dock = DockStyle.Right;
            btnPin.Size = new Size(34, 36);
            btnFull = new IconButton();
            btnFull.Kind = IconButton.IconKind.Fullscreen;
            btnFull.Dock = DockStyle.Right;
            btnFull.Size = new Size(34, 36);

            // Dock=Right 时后添加者靠右：最终视觉顺序 [全屏][置顶][移入工作台] ─ ▢ ✕（与主窗口一致）
            Controls.Add(Title);
            Controls.Add(btnFull);
            Controls.Add(btnPin);
            Controls.Add(btnDock);
            Controls.Add(btnMin);
            Controls.Add(btnMax);
            Controls.Add(btnClose);

            btnPin.Click += delegate { if (PinClicked != null) PinClicked(this, EventArgs.Empty); };
            btnFull.Click += delegate { if (FullscreenClicked != null) FullscreenClicked(this, EventArgs.Empty); };
            btnDock.Click += delegate { if (DockClicked != null) DockClicked(this, EventArgs.Empty); };
            btnMin.Click += delegate { if (MinClicked != null) MinClicked(this, EventArgs.Empty); };
            btnMax.Click += delegate { if (MaxClicked != null) MaxClicked(this, EventArgs.Empty); };
            btnClose.Click += delegate { if (CloseClicked != null) CloseClicked(this, EventArgs.Empty); };

            MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    Form f = FindForm();
                    if (f != null) Native.StartWindowDrag(f.Handle);
                }
            };
            MouseDoubleClick += delegate(object s, MouseEventArgs e)
            {
                if (MaxToggled != null) MaxToggled(this, EventArgs.Empty);
            };
            // 标题文本覆盖整条标题栏，拖动/双击也要接管（否则只能从按钮缝隙拖动）
            Title.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    Form f = FindForm();
                    if (f != null) Native.StartWindowDrag(f.Handle);
                }
            };
            Title.MouseDoubleClick += delegate(object s, MouseEventArgs e)
            {
                if (MaxToggled != null) MaxToggled(this, EventArgs.Empty);
            };

            ApplyTheme();
            tips.SetToolTip(btnPin, "窗口置顶");
            tips.SetToolTip(btnFull, "全屏（隐藏顶栏，鼠标移到屏幕顶部退出）");
            tips.SetToolTip(btnDock, "移入工作台标签");
            tips.SetToolTip(btnMin, "最小化");
            tips.SetToolTip(btnMax, "最大化 / 还原");
            tips.SetToolTip(btnClose, "关闭");
        }

        public event EventHandler MinClicked;
        public event EventHandler MaxClicked;
        public event EventHandler CloseClicked;

        public void SetMaximized(bool max)
        {
            btnMax.Kind = max ? IconButton.IconKind.Restore : IconButton.IconKind.Max;
        }

        /// <summary>置顶生效时点亮图钉（强调色 + 帽内实心点）。</summary>
        public void SetPinned(bool on)
        {
            btnPin.Active = on;
            btnPin.Invalidate();
        }

        /// <summary>小组件等无最大化需求的窗口隐藏最大化按钮。</summary>
        public void HideMax()
        {
            btnMax.Visible = false;
        }

        public void ApplyTheme()
        {
            BackColor = Theme.WindowBg;
            Title.ForeColor = Theme.TitleText;
            foreach (IconButton b in new IconButton[] { btnFull, btnPin, btnDock, btnMin, btnMax, btnClose })
                b.ApplyTheme();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
        }
    }
}
