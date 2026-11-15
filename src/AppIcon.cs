using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace PageDock
{
    /// <summary>
    /// 程序图标：内嵌 assets\app.ico（exe/任务栏/Alt-Tab 用，多尺寸）+ assets\app.png（512，界面内自绘用）。
    /// 界面内 logo、窗口图标、任务栏都源自同一张图，内外观感一致且不发虚。
    /// 注：ico 的 128/256 帧是 PNG 压缩的，GDI+ 的 Icon.ToBitmap() 解不了，所以自绘一律走 PNG。
    /// </summary>
    internal static class AppIcon
    {
        private const string IconRes = "PageDock.app.ico";
        private const string PngRes = "PageDock.app.png";

        private static Icon baseIcon;
        private static bool iconLoaded;
        private static Stream iconStream;   // Icon 会延迟读流，必须一直留着
        private static Bitmap source;       // 512 的 PNG 母图
        private static bool pngLoaded;
        private static readonly Dictionary<int, Bitmap> cache = new Dictionary<int, Bitmap>();

        /// <summary>多帧图标（窗口/任务栏用，系统会按场景自己挑合适尺寸）。</summary>
        public static Icon FormIcon()
        {
            Icon ico = LoadIcon();
            if (ico != null)
            {
                try { return (Icon)ico.Clone(); } // 各窗体各持一份，谁先关闭都不会牵连别人
                catch { return ico; }
            }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return null; }
        }

        /// <summary>取指定像素尺寸的位图（自绘 logo 用）。</summary>
        public static Bitmap Get(int px)
        {
            if (px <= 0) px = 16;
            Bitmap b;
            if (cache.TryGetValue(px, out b) && b != null) return b;
            b = Render(px);
            if (b != null) cache[px] = b;
            return b;
        }

        /// <summary>DPI 变化后丢弃旧尺寸的位图。</summary>
        public static void ResetCache()
        {
            foreach (Bitmap b in cache.Values) if (b != null) b.Dispose();
            cache.Clear();
        }

        private static Bitmap LoadSource()
        {
            if (pngLoaded) return source;
            pngLoaded = true;
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(PngRes))
                {
                    if (s != null)
                        using (Bitmap tmp = new Bitmap(s)) source = new Bitmap(tmp); // 解码完即可脱离流
                }
            }
            catch { source = null; }
            return source;
        }

        private static Icon LoadIcon()
        {
            if (iconLoaded) return baseIcon;
            iconLoaded = true;
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(IconRes))
                {
                    if (s != null)
                    {
                        // 先把资源拷到内存流并保留引用：Icon(Stream) 是懒加载，流一释放再取帧就会拿到乱码
                        MemoryStream ms = new MemoryStream();
                        s.CopyTo(ms);
                        ms.Position = 0;
                        iconStream = ms;
                        baseIcon = new Icon(ms);
                    }
                }
            }
            catch { baseIcon = null; }
            return baseIcon;
        }

        private static Bitmap Render(int px)
        {
            Bitmap src = LoadSource();
            try
            {
                if (src != null)
                {
                    Bitmap outp = new Bitmap(px, px);
                    using (Graphics g = Graphics.FromImage(outp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.Clear(Color.Transparent);
                        g.DrawImage(src, new Rectangle(0, 0, px, px));
                    }
                    return outp;
                }
                // 没有 PNG 资源时退回 ico 里能解码的最近一帧
                Icon ico = LoadIcon();
                if (ico == null) return null;
                int pick = px <= 16 ? 16 : px <= 32 ? 32 : 64;
                using (Icon near = new Icon(ico, new Size(pick, pick)))
                using (Bitmap hi = near.ToBitmap())
                {
                    Bitmap outp = new Bitmap(px, px);
                    using (Graphics g = Graphics.FromImage(outp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.Clear(Color.Transparent);
                        g.DrawImage(hi, new Rectangle(0, 0, px, px));
                    }
                    return outp;
                }
            }
            catch { return null; }
        }
    }
}
