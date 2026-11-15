using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PageDock
{
    /// <summary>
    /// 标签生命周期管理（省内存）：
    /// - 切走标签时对该 WebView TrySuspendAsync（浏览器进程级挂起，CPU/GPU 停摆、内存回落）；
    /// - 空闲超时（默认 10 分钟）未激活的标签彻底释放 WebView，只留 TabItem 元数据，
    ///   下次激活由 EnsureTabWeb 重建（localStorage/IndexedDB 在磁盘，数据不丢）；
    /// - 首页（工作台/设置）常驻不挂起，避免搜索与设置弹窗要唤醒的开销。
    /// </summary>
    internal class TabLifecycle
    {
        private readonly Func<List<TabItem>> tabsProvider;
        private readonly Func<TabItem> activeProvider;
        private readonly Action<TabItem> releaseWeb;
        private readonly Timer idle;
        private TabItem active;

        private const int IdleMinutes = 10;

        public TabLifecycle(Func<List<TabItem>> tabsProvider, Func<TabItem> activeProvider,
                            Action<TabItem> releaseWeb)
        {
            this.tabsProvider = tabsProvider;
            this.activeProvider = activeProvider;
            this.releaseWeb = releaseWeb;
            idle = new Timer();
            idle.Interval = 60000; // 每分钟巡一遍
            idle.Tick += delegate { Sweep(); };
            idle.Start();
        }

        /// <summary>切签时由 MainFrame 调：激活的恢复 + 记时间，其余全部挂起。</summary>
        public void OnActivated(TabItem t)
        {
            active = t;
            if (t != null) t.LastActive = DateTime.UtcNow;
            foreach (TabItem x in tabsProvider().ToArray())
            {
                if (x == t || x.Web == null || x.IsHome) continue;
                SuspendQuietly(x.Web);
            }
        }

        private void Sweep()
        {
            List<TabItem> list = tabsProvider();
            if (list == null) return;
            foreach (TabItem t in list.ToArray())
            {
                if (t == null || t.IsHome || t.Web == null) continue;
                if (t == active) { t.LastActive = DateTime.UtcNow; continue; }
                // 空闲超时 → 释放整个 WebView（元数据保留，激活时重建）
                if ((DateTime.UtcNow - t.LastActive).TotalMinutes >= IdleMinutes)
                {
                    releaseWeb(t);
                }
                else
                {
                    SuspendQuietly(t.Web);
                }
            }
        }

        private static void SuspendQuietly(WebView2 web)
        {
            System.Threading.Tasks.Task.Run(async delegate
            {
                try { await web.CoreWebView2.TrySuspendAsync(); } catch { }
            });
        }
    }
}
