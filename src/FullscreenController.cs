using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PageDock
{
    /// <summary>
    /// 全屏模式：隐藏顶部栏（标签栏/标题栏），窗口铺满整屏；
    /// 鼠标上移到屏幕顶部边缘时浮现圆角“退出全屏”悬浮按钮，点击即还原。
    /// 主窗口（TabStrip）与独立程序窗口（TitleBar）共用。
    /// </summary>
    internal class FullscreenController
    {
        private readonly Form form;
        private readonly Control topBar;
        private readonly PillButton btnExit;
        private readonly Timer poll = new Timer();
        private Rectangle savedBounds;
        private FormWindowState savedState;

        public bool Active { get; private set; }

        public FullscreenController(Form form, Control topBar)
        {
            this.form = form;
            this.topBar = topBar;

            btnExit = new PillButton();
            btnExit.Text = "退出全屏";
            btnExit.Visible = false;
            btnExit.Click += delegate { Exit(); };
            form.Controls.Add(btnExit);
            btnExit.BringToFront();

            // Esc 退出全屏（窗体需 KeyPreview 才能收到子控件焦点时的按键）
            form.KeyPreview = true;
            form.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (Active && e.KeyCode == Keys.Escape) { Exit(); e.Handled = true; }
            };

            poll.Interval = 100;
            poll.Tick += delegate { UpdateFloat(); };
        }

        public void Toggle()
        {
            if (Active) Exit(); else Enter();
        }

        private void Enter()
        {
            if (Active) return;
            savedState = form.WindowState;
            savedBounds = form.Bounds;
            topBar.Visible = false;
            // 铺满整个屏幕（含任务栏区域），真正的无边框全屏
            form.Bounds = Screen.FromControl(form).Bounds;
            Active = true;
            btnExit.BringToFront();
            poll.Start();
        }

        private void Exit()
        {
            if (!Active) return;
            Active = false;
            poll.Stop();
            btnExit.Visible = false;
            topBar.Visible = true;
            form.WindowState = savedState;
            if (savedState == FormWindowState.Normal) form.Bounds = savedBounds;
            topBar.Invalidate();
        }

        private void UpdateFloat()
        {
            if (!Active || form.IsDisposed) { poll.Stop(); return; }
            Rectangle scr = Screen.FromControl(form).Bounds;
            Point p = Cursor.Position;
            bool nearTop = p.Y - scr.Top <= 8;
            Rectangle btnScr = new Rectangle(
                form.PointToScreen(btnExit.Location), btnExit.Size);
            bool hovering = !btnExit.Visible ? false :
                btnScr.Contains(p) || btnScr.Contains(p.X, p.Y + 4);
            if (nearTop || hovering)
            {
                int x = scr.Left + (scr.Width - btnExit.Width) / 2 - form.Left;
                int y = scr.Top - form.Top + 10;
                btnExit.Location = new Point(x, y);
                if (!btnExit.Visible) { btnExit.Visible = true; btnExit.BringToFront(); }
            }
            else if (btnExit.Visible)
            {
                btnExit.Visible = false;
            }
        }

        /// <summary>圆角悬浮按钮：深色底 + 图标 + 文案，风格与主题一致。</summary>
        private class PillButton : Control
        {
            public PillButton()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
                Size = new Size(118, 32);
                Cursor = Cursors.Hand;
                BackColor = Color.Transparent;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddArc(0, 0, Height, Height, 90, 180);
                    path.AddArc(Width - Height, 0, Height, Height, 270, 180);
                    path.CloseFigure();
                    using (SolidBrush bg = new SolidBrush(Theme.WindowBg))
                        g.FillPath(bg, path);
                    using (Pen border = new Pen(Theme.Border, 1f))
                        g.DrawPath(border, path);
                }
                using (Pen p = new Pen(Theme.TitleText, 1.6f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                })
                {
                    float cy = Height / 2f;
                    float ix = 18f;
                    g.DrawLine(p, ix, cy - 5, ix, cy - 1.5f);
                    g.DrawLine(p, ix, cy - 5, ix + 3.5f, cy - 5);
                    g.DrawLine(p, ix + 8, cy + 5, ix + 8, cy + 1.5f);
                    g.DrawLine(p, ix + 8, cy + 5, ix + 4.5f, cy + 5);
                }
                TextRenderer.DrawText(g, Text, new Font("Microsoft YaHei UI", 9f),
                    new Rectangle(30, 0, Width - 30, Height),
                    Theme.TitleText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
        }
    }
}
