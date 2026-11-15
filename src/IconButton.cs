using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PageDock
{
    /// <summary>
    /// 统一风格的线性图标按钮（1.6px 圆头线条），与 Web 端 SVG 图标同一视觉语言。
    /// </summary>
    internal class IconButton : Control
    {
        public enum IconKind { Settings, Min, Max, Restore, Close, DockIn, Pin, Fullscreen, ExitFullscreen, Download }

        private bool hover;
        private bool pressed;

        public IconKind Kind = IconKind.Min;
        public bool DangerHover = false;   // 关闭钮悬停红底
        public bool Rounded = true;        // 悬停底色圆角
        public bool Active = false;        // 切换型按钮的"已开启"态（如图钉=置顶生效），用强调色描画
        public int PillHeight = 0;         // 悬停/按下时圆角胶囊底色的高度（0=自动：按钮高内缩）。由 TabStrip 传入以与搜索框同高
        public int PillRadius = 0;         // 胶囊圆角半径（0=自动）

        public IconButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.WindowBg;
            ForeColor = Theme.BtnFg;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        private Color IconColor
        {
            get
            {
                if (hover && DangerHover) return Color.White;
                if (Active) return Theme.Accent;   // 置顶生效：图钉用强调色
                return hover ? Theme.Text : Theme.BtnFg;
            }
        }

        private Color BgColor
        {
            get
            {
                if (!hover && !pressed) return BackColor;
                if (DangerHover) return Theme.CloseHover;
                return pressed ? Theme.Hover : Theme.HoverSoft;
            }
        }

        /// <summary>DPI 缩放比（96=1）：图标线条按 13px 视觉盒绘制，高分屏要整体放大。</summary>
        private float Ui
        {
            get
            {
                int dpi = 96;
                try { dpi = DeviceDpi; } catch { }
                if (dpi <= 0) dpi = 96;
                return dpi / 96f;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 先铺底色（不透明），再在需要时叠加圆角胶囊——不再用 g.Clear(BgColor) 铺满整个矩形，
            // 这样悬停高亮就是与搜索框同高的圆角胶囊，而不是直角满格矩形。
            g.Clear(BackColor);
            Color bg = BgColor;
            if (bg != BackColor)
            {
                int inset = (int)Math.Round(3 * Ui);
                int ph = PillHeight > 0 ? PillHeight : Height - inset * 2;
                if (ph < 6) ph = 6;
                int pw = Math.Min(Width, ph);           // 近正方形胶囊，宽度不超过按钮本身
                int pr = PillRadius > 0 ? PillRadius : (int)Math.Round(9 * Ui);
                if (pr > ph / 2) pr = ph / 2;
                if (pr > pw / 2) pr = pw / 2;
                RectangleF pill = new RectangleF((Width - pw) / 2f, (Height - ph) / 2f, pw, ph);
                using (GraphicsPath path = RoundPath(Rectangle.Round(pill), pr))
                using (SolidBrush bb = new SolidBrush(bg))
                    g.FillPath(bb, path);
            }

            int w = Width, h = Height;
            float cx = w / 2f, cy = h / 2f;
            float ui = Ui;
            if (ui > 1.01f || ui < 0.99f)
            {
                // 以按钮中心为基准整体缩放，字形坐标仍按 13px 视觉盒书写
                g.TranslateTransform(cx, cy);
                g.ScaleTransform(ui, ui);
                g.TranslateTransform(-cx, -cy);
            }

            using (Pen p = new Pen(IconColor, 1.6f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;

                switch (Kind)
                {
                    case IconKind.Settings:
                        // 三条短横线（简洁的设置/列表符号）
                        g.DrawLine(p, cx - 5.5f, cy - 5f, cx + 5.5f, cy - 5f);
                        g.DrawLine(p, cx - 5.5f, cy, cx + 5.5f, cy);
                        g.DrawLine(p, cx - 5.5f, cy + 5f, cx + 5.5f, cy + 5f);
                        break;
                    case IconKind.Min:
                        g.DrawLine(p, cx - 6.5f, cy, cx + 6.5f, cy);
                        break;
                    case IconKind.Max:
                        g.DrawRectangle(p, cx - 6.5f, cy - 6.5f, 13f, 13f);
                        break;
                    case IconKind.Restore:
                        g.DrawRectangle(p, cx - 3.5f, cy - 6.5f, 10f, 10f);
                        g.DrawRectangle(p, cx - 6.5f, cy - 3.5f, 10f, 10f);
                        break;
                    case IconKind.Download:
                        // 向下箭头 + 底部托盘（浏览器下载语义）
                        g.DrawLine(p, cx, cy - 7f, cx, cy + 3f);
                        g.DrawLine(p, cx - 4f, cy - 0.5f, cx, cy + 3.5f);
                        g.DrawLine(p, cx + 4f, cy - 0.5f, cx, cy + 3.5f);
                        g.DrawLine(p, cx - 6.5f, cy + 7f, cx + 6.5f, cy + 7f);
                        break;
                    case IconKind.Close:
                        g.DrawLine(p, cx - 5.5f, cy - 5.5f, cx + 5.5f, cy + 5.5f);
                        g.DrawLine(p, cx + 5.5f, cy - 5.5f, cx - 5.5f, cy + 5.5f);
                        break;
                    case IconKind.Fullscreen:
                        // 四角外扩括号（⛶ 进入全屏）
                        g.DrawLine(p, cx - 6.5f, cy - 3.5f, cx - 6.5f, cy - 6.5f);
                        g.DrawLine(p, cx - 6.5f, cy - 6.5f, cx - 3.5f, cy - 6.5f);
                        g.DrawLine(p, cx + 3.5f, cy - 6.5f, cx + 6.5f, cy - 6.5f);
                        g.DrawLine(p, cx + 6.5f, cy - 6.5f, cx + 6.5f, cy - 3.5f);
                        g.DrawLine(p, cx + 6.5f, cy + 3.5f, cx + 6.5f, cy + 6.5f);
                        g.DrawLine(p, cx + 6.5f, cy + 6.5f, cx + 3.5f, cy + 6.5f);
                        g.DrawLine(p, cx - 3.5f, cy + 6.5f, cx - 6.5f, cy + 6.5f);
                        g.DrawLine(p, cx - 6.5f, cy + 6.5f, cx - 6.5f, cy + 3.5f);
                        break;
                    case IconKind.ExitFullscreen:
                        // 四角内收括号（退出全屏）
                        g.DrawLine(p, cx - 6.5f, cy - 6.5f, cx - 6.5f, cy - 3f);
                        g.DrawLine(p, cx - 6.5f, cy - 6.5f, cx - 3f, cy - 6.5f);
                        g.DrawLine(p, cx + 3f, cy - 6.5f, cx + 6.5f, cy - 6.5f);
                        g.DrawLine(p, cx + 6.5f, cy - 6.5f, cx + 6.5f, cy - 3f);
                        g.DrawLine(p, cx + 6.5f, cy + 6.5f, cx + 6.5f, cy + 3f);
                        g.DrawLine(p, cx + 6.5f, cy + 6.5f, cx + 3f, cy + 6.5f);
                        g.DrawLine(p, cx - 3f, cy + 6.5f, cx - 6.5f, cy + 6.5f);
                        g.DrawLine(p, cx - 6.5f, cy + 6.5f, cx - 6.5f, cy + 3f);
                        break;
                    case IconKind.DockIn:
                        // 托架 + 向下箭头（移入工作台）
                        g.DrawLine(p, cx - 6.5f, cy - 1f, cx - 6.5f, cy + 6.5f);
                        g.DrawLine(p, cx - 6.5f, cy + 6.5f, cx + 6.5f, cy + 6.5f);
                        g.DrawLine(p, cx + 6.5f, cy + 6.5f, cx + 6.5f, cy - 1f);
                        g.DrawLine(p, cx, cy - 8.5f, cx, cy);
                        g.DrawLine(p, cx - 3.5f, cy - 5f, cx, cy - 1.5f);
                        g.DrawLine(p, cx + 3.5f, cy - 5f, cx, cy - 1.5f);
                        break;
                    case IconKind.Pin:
                        // 线描图钉（参用户图）：圆角矩形钉帽 + 外扩钟形裙 + 平底座 + 直针（空心描边）
                        {
                            using (Pen pp = new Pen(IconColor, 1.9f))
                            {
                                pp.StartCap = LineCap.Round;
                                pp.EndCap = LineCap.Round;
                                pp.LineJoin = LineJoin.Round;
                                using (GraphicsPath pin = new GraphicsPath())
                                {
                                    pin.AddLine(cx - 3.2f, cy - 6.5f, cx + 3.2f, cy - 6.5f);   // 顶边
                                    pin.AddLine(cx + 3.2f, cy - 6.5f, cx + 3.2f, cy - 3f);     // 右腿
                                    pin.AddBezier(                                              // 右裙外扩
                                        cx + 3.2f, cy - 3f,
                                        cx + 5.6f, cy - 2.6f,
                                        cx + 6.6f, cy - 0.4f,
                                        cx + 6.6f, cy + 1.2f);
                                    pin.AddLine(cx + 6.6f, cy + 1.2f, cx - 6.6f, cy + 1.2f);   // 底座
                                    pin.AddBezier(                                              // 左裙外扩
                                        cx - 6.6f, cy + 1.2f,
                                        cx - 6.6f, cy - 0.4f,
                                        cx - 5.6f, cy - 2.6f,
                                        cx - 3.2f, cy - 3f);
                                    pin.CloseFigure();                                          // 回接顶边左端（左腿）
                                    g.DrawPath(pp, pin);
                                }
                                g.DrawLine(pp, cx, cy + 1.2f, cx, cy + 7f);                     // 针
                            }
                        }
                        break;
                }
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hover = true; Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = false; pressed = false; Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            pressed = true; Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            pressed = false; Invalidate();
        }

        /// <summary>主题变化时由父容器刷新配色。</summary>
        public void ApplyTheme()
        {
            BackColor = Theme.WindowBg;
            ForeColor = Theme.BtnFg;
            Invalidate();
        }

        /// <summary>圆角矩形路径（胶囊底色用）。</summary>
        private static GraphicsPath RoundPath(Rectangle r, int rad)
        {
            GraphicsPath path = new GraphicsPath();
            int d = rad * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
