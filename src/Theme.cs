using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Web.Script.Serialization;

namespace PageDock
{
    /// <summary>
    /// 与工作台 UI 保持一致的双主题色板（深/浅）。
    /// JS 端切换主题时通过 set-theme 消息同步到原生标题栏等部位。
    /// </summary>
    internal static class Theme
    {
        /// <summary>
        /// 当前是否为深色。初值直接取系统主题，避免任何控件在“还没人设置过”时
        /// 落到硬编码的深色上。启动时的正式取值由 ResolveFromConfig() 给出。
        /// </summary>
        public static bool Dark = IsSystemDark();

        /// <summary>
        /// 解析主题设置（apps.json 的 settings.theme：auto/dark/light）并写入 Dark，返回模式字符串。
        /// 必须在创建任何窗体之前调用——ChromeForm/TitleBar/TabStrip 等都在构造函数里
        /// 就已经读 Theme.WindowBg 定下了底色，晚一步就会先画一帧深色再跳浅。
        /// </summary>
        public static string ResolveFromConfig()
        {
            string mode = "auto";
            try
            {
                if (!string.IsNullOrEmpty(Data.ConfigPath) && File.Exists(Data.ConfigPath))
                {
                    string json = File.ReadAllText(Data.ConfigPath);
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    Dictionary<string, object> root = ser.Deserialize<Dictionary<string, object>>(json);
                    object sv;
                    if (root != null && root.TryGetValue("settings", out sv))
                    {
                        Dictionary<string, object> s = sv as Dictionary<string, object>;
                        object tv;
                        if (s != null && s.TryGetValue("theme", out tv) && tv != null)
                        {
                            string v = Convert.ToString(tv);
                            if (v == "dark" || v == "light" || v == "auto") mode = v;
                        }
                    }
                }
            }
            catch { }
            Dark = mode == "dark" || (mode == "auto" && IsSystemDark());
            return mode;
        }

        /// <summary>系统当前是否为深色（Windows 应用主题）。</summary>
        public static bool IsSystemDark()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    if (v == null) return false;
                    return Convert.ToInt32(v) == 0;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// 首帧主题脚本：注入到每个页面 document 创建时（早于页面自己的脚本）。
        /// 宿主已经读完设置与系统主题、把结论算好了，这里直接写进 html[data-theme]，
        /// 不再让页面去问 prefers-color-scheme——WebView2 的 profile 色彩偏好是异步生效
        /// 且会被上次运行记住的，首帧常常还是旧值，浅色系统上就会先渲染一帧深色再跳浅。
        /// 同时把结论放进 window.__pdTheme，供页面脚本首帧复用。
        /// </summary>
        public static string FirstPaintScript()
        {
            // 先写 window.__pdTheme（页面脚本要用），再设 data-theme；documentElement 尚未就绪时
            // 等 readystatechange 再补一刀，保证不会退回媒体查询拿到旧值。
            return "(function(){try{var t='" + (Dark ? "dark" : "light") + "';window.__pdTheme=t;" +
                   "var a=function(){try{var d=document.documentElement;if(d)d.setAttribute('data-theme',t);}catch(e){}};" +
                   "a();if(!document.documentElement)document.addEventListener('readystatechange',a,false);" +
                   "}catch(e){}})();";
        }

        private static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        public static Color WindowBg
        {
            get { return Dark ? C(11, 12, 14) : C(246, 247, 248); }
        }

        /// <summary>
        /// 弹窗遮罩盖住页面后的等效底色：与 app.css 里 #modal-root 的遮罩同色同 alpha，
        /// 按 alpha 混到窗口底色上。窗体四周的缩放边条用它，遮罩外圈才不会留一圈白线。
        /// </summary>
        public static Color GutterDim
        {
            get
            {
                return Dark
                    ? Blend(C(5, 6, 8), WindowBg, 0.55)      /* 深色 rgba(5,6,8,.55) */
                    : Blend(C(16, 24, 40), WindowBg, 0.30);  /* 浅色 rgba(16,24,40,.3) */
            }
        }

        /// <summary>半透明遮罩色 top（alpha 参与混合）叠在不透明底色 bottom 上的结果。</summary>
        private static Color Blend(Color top, Color bottom, double alpha)
        {
            return Color.FromArgb(
                (int)Math.Round(bottom.R + (top.R - bottom.R) * alpha),
                (int)Math.Round(bottom.G + (top.G - bottom.G) * alpha),
                (int)Math.Round(bottom.B + (top.B - bottom.B) * alpha));
        }

        public static Color TitleText
        {
            get { return Dark ? C(154, 161, 171) : C(86, 94, 104); }
        }

        public static Color Text
        {
            get { return Dark ? C(231, 234, 238) : C(23, 25, 28); }
        }

        public static Color TextDim
        {
            get { return Dark ? C(154, 161, 171) : C(122, 130, 140); }
        }

        public static Color Border
        {
            get { return Dark ? C(35, 38, 45) : C(228, 231, 235); }
        }

        public static Color BtnFg
        {
            get { return Dark ? C(154, 161, 171) : C(120, 128, 138); }
        }

        public static Color Hover
        {
            get { return Dark ? C(42, 45, 51) : C(226, 230, 234); }
        }

        public static Color HoverSoft
        {
            get { return Dark ? C(34, 37, 43) : C(237, 240, 243); }
        }

        public static Color CloseHover
        {
            get { return C(196, 43, 28); }
        }

        public static Color Accent
        {
            get { return Dark ? C(45, 212, 191) : C(11, 128, 116); }
        }

        public static Color AccentInk
        {
            get { return Dark ? C(6, 40, 35) : Color.White; }
        }

        public static Color AccentSoft
        {
            get { return Dark ? C(67, 222, 201) : C(10, 148, 136); }
        }
    }
}
