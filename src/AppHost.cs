using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.IO.Compression;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace PageDock
{
    /// <summary>
    /// 工具身份与数据分桶中枢（v0.8.3）：
    /// - 以应用 id 为稳定身份，把每个本地工具映射到唯一 origin（https://app-&lt;id&gt;.local/），
    ///   靠 WebView2 的虚拟主机映射按 origin 天然隔离 localStorage/IndexedDB；
    /// - 源/数据/导出三桶：源与导出落在 data\apps\&lt;id&gt;\{html,output}，运行数据在共享 profile 内按 origin 分区；
    /// - 换机整包/轻量备份、HTML 更新不丢数据、开机自启等系统级能力。
    /// </summary>
    internal static class AppHost
    {
        private static readonly JavaScriptSerializer ser = new JavaScriptSerializer();
        private const string HostPrefix = "app-";
        private const string HostSuffix = ".local";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "PageDock";

        /* ---------------- apps.json 读取 ---------------- */

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return null;
        }

        private static List<Dictionary<string, object>> LoadApps()
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            try
            {
                if (!File.Exists(Data.ConfigPath)) return list;
                object root = ser.Deserialize<object>(File.ReadAllText(Data.ConfigPath));
                Dictionary<string, object> ro = root as Dictionary<string, object>;
                if (ro == null) return list;
                object apps;
                if (!ro.TryGetValue("apps", out apps)) return list;
                object[] arr = apps as object[];
                if (arr == null) return list;
                foreach (object o in arr)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d != null) list.Add(d);
                }
            }
            catch { }
            return list;
        }

        public static Dictionary<string, object> FindApp(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (Dictionary<string, object> d in LoadApps())
                if (string.Equals(Str(d, "id"), id, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        /// <summary>kind 为 file / folder 的本地工具才需要 origin 隔离（url 走它自己的源）。</summary>
        public static bool IsLocalApp(string id)
        {
            Dictionary<string, object> app = FindApp(id);
            if (app == null) return false;
            string kind = Str(app, "kind");
            return kind == "file" || kind == "folder";
        }

        /* ---------------- 源纳管与入口解析 ---------------- */

        private static string RelUnder(string baseDir, string fullPath)
        {
            try
            {
                string bd = Path.GetFullPath(baseDir).TrimEnd('\\') + "\\";
                string fp = Path.GetFullPath(fullPath);
                if (fp.StartsWith(bd, StringComparison.OrdinalIgnoreCase))
                    return fp.Substring(bd.Length).Replace('\\', '/');
                return Path.GetFileName(fp);
            }
            catch { return Path.GetFileName(fullPath); }
        }

        /// <summary>确保工具源就绪，返回服务根目录与相对入口（正斜杠）。失败返回 false。</summary>
        public static bool EnsureEntry(string id, out string srcDir, out string entryRel)
        {
            srcDir = null; entryRel = null;
            Dictionary<string, object> app = FindApp(id);
            if (app == null) return false;
            string kind = Str(app, "kind");
            string target = Str(app, "target");
            string entry = Str(app, "entry");
            string resolved = Data.ResolveTarget(target);
            try { Directory.CreateDirectory(Data.AppOutputDir(id)); } catch { }

            if (kind == "folder")
            {
                if (string.IsNullOrEmpty(resolved) || !Directory.Exists(resolved)) return false;
                string dir = resolved;
                string entryFile = null;
                if (!string.IsNullOrEmpty(entry))
                {
                    string e = Data.ResolveTarget(entry);
                    if (File.Exists(e)) entryFile = e;
                }
                if (entryFile == null)
                {
                    string idx = Path.Combine(dir, "index.html");
                    if (File.Exists(idx)) entryFile = idx;
                    else
                    {
                        string[] cand = Directory.GetFiles(dir, "*.html", SearchOption.TopDirectoryOnly);
                        if (cand.Length == 0) return false;
                        entryFile = cand[0];
                    }
                }
                srcDir = dir;
                entryRel = RelUnder(dir, entryFile);
                return true;
            }

            // kind == file：单文件自包含，纳管拷贝进 html/（源与外部路径解耦，可迁移/可热更新）
            string htmlDir = Data.AppHtmlDir(id);
            Directory.CreateDirectory(htmlDir);
            string wantName = string.IsNullOrEmpty(resolved) ? null : Path.GetFileName(resolved);
            string want = wantName == null ? null : Path.Combine(htmlDir, wantName);
            if (want != null && File.Exists(want))
            {
                srcDir = htmlDir; entryRel = wantName; return true;
            }
            // html/ 里已有别的入口（更新过、改了名）→ 沿用第一个 html
            string[] existing = Directory.GetFiles(htmlDir, "*.html", SearchOption.TopDirectoryOnly);
            if (existing.Length > 0)
            {
                srcDir = htmlDir; entryRel = Path.GetFileName(existing[0]); return true;
            }
            // 首次纳管：从原 target 拷入
            if (resolved != null && File.Exists(resolved))
            {
                File.Copy(resolved, want, true);
                srcDir = htmlDir; entryRel = wantName; return true;
            }
            return false;
        }

        public static string BuildHost(string id) { return HostPrefix + Data.SafeId(id) + HostSuffix; }

        private static string UrlEncodeRel(string rel)
        {
            string[] segs = rel.Split('/');
            for (int i = 0; i < segs.Length; i++)
                segs[i] = Uri.EscapeDataString(segs[i]);
            return string.Join("/", segs);
        }

        /// <summary>供 UI 解析应用入口 URL：本地工具走 https origin，url 类原样返回 target。</summary>
        public static string ResolveAppUrl(string id)
        {
            Dictionary<string, object> app = FindApp(id);
            if (app == null) return null;
            string kind = Str(app, "kind");
            if (kind == "url") return Str(app, "target");
            string srcDir, entryRel;
            if (EnsureEntry(id, out srcDir, out entryRel))
                return "https://" + BuildHost(id) + "/" + UrlEncodeRel(entryRel);
            // 兜底：源缺失仍按老的 file:// 打开，保证可用性
            try
            {
                string r = Data.ResolveTarget(Str(app, "target"));
                if (File.Exists(r) || Directory.Exists(r)) return new Uri(r).AbsoluteUri;
            }
            catch { }
            return null;
        }

        /// <summary>导航前在给定 core 上注册虚拟主机映射（仅对 app-*.local 源生效）。失败静默回退。</summary>
        public static void RegisterMappingIfNeeded(CoreWebView2 core, string url)
        {
            if (core == null || string.IsNullOrEmpty(url)) return;
            Uri u;
            try { u = new Uri(url); } catch { return; }
            if (!string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return;
            string host = u.Host;
            if (!host.StartsWith(HostPrefix, StringComparison.OrdinalIgnoreCase) ||
                !host.EndsWith(HostSuffix, StringComparison.OrdinalIgnoreCase)) return;
            string id = host.Substring(HostPrefix.Length, host.Length - HostPrefix.Length - HostSuffix.Length);
            string srcDir, entryRel;
            if (!EnsureEntry(id, out srcDir, out entryRel)) return;
            try { core.SetVirtualHostNameToFolderMapping(host, srcDir, CoreWebView2HostResourceAccessKind.Allow); }
            catch { }
        }

        /// <summary>用新 HTML 覆盖工具入口（id/origin 不变 → 数据保留）。仅处理单文件类。</summary>
        public static bool UpdateSource(string id, string newPath)
        {
            Dictionary<string, object> app = FindApp(id);
            if (app == null) throw new Exception("应用不存在");
            string kind = Str(app, "kind");
            string srcFile = Data.ResolveTarget(newPath);
            if (!File.Exists(srcFile)) throw new Exception("找不到源文件");

            if (kind == "folder")
            {
                // 文件夹类：把整个新目录内容覆盖进原目录入口所在根（保守：仅换 index.html）
                string srcDir, entryRel;
                if (EnsureEntry(id, out srcDir, out entryRel))
                {
                    string dest = Path.Combine(srcDir, entryRel.Replace('/', Path.DirectorySeparatorChar));
                    File.Copy(srcFile, dest, true);
                    return true;
                }
                throw new Exception("文件夹应用无法定位入口");
            }

            string htmlDir = Data.AppHtmlDir(id);
            Directory.CreateDirectory(htmlDir);
            string curEntry;
            string srcDir2, entryRel2;
            if (EnsureEntry(id, out srcDir2, out entryRel2)) curEntry = entryRel2;
            else curEntry = Path.GetFileName(srcFile);
            string destPath = Path.Combine(htmlDir, curEntry);
            File.Copy(srcFile, destPath, true);
            // 清理 html/ 内其它游离入口，确保下次仍服务这个文件
            foreach (string f in Directory.GetFiles(htmlDir, "*.html", SearchOption.TopDirectoryOnly))
                if (!string.Equals(Path.GetFullPath(f), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { }
            return true;
        }

        /* ---------------- 拖入新版 HTML 的升级：先比对 → 用户确认 → 原地覆盖且数据保留 ---------------- */

        /// <summary>当前入口的完整路径；找不到返回 null。</summary>
        private static string EntryFileOf(string id)
        {
            string srcDir, entryRel;
            if (!EnsureEntry(id, out srcDir, out entryRel)) return null;
            try
            {
                string p = Path.Combine(srcDir, entryRel.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(p) ? p : null;
            }
            catch { return null; }
        }

        /// <summary>按 UTF-8 严格解码读文本，失败回退本地 ANSI（兼容 GBK 老页面）。</summary>
        public static string ReadTextAuto(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                byte[] all = File.ReadAllBytes(path);
                try { return new System.Text.UTF8Encoding(false, true).GetString(all); }
                catch { }
                return System.Text.Encoding.Default.GetString(all);
            }
            catch { return null; }
        }

        private static string[] LinesOf(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            return s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        /// <summary>去掉文件开头的 BOM 字符：浏览器 FileReader 交上来的正文是不带 BOM 的，
        /// 不归一就会把“同一份文件”误判成“改了 1 行”。</summary>
        private static string StripBom(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.Length > 0 && s[0] == '\uFEFF') return s.Substring(1);
            return s;
        }

        /// <summary>行级增删统计（多重集合差）+ 首个差异行号：够判断“是不是同一份、改了多少”。</summary>
        private static void DiffLines(string oldText, string newText, out int added, out int removed, out int firstDiff)
        {
            string[] a = LinesOf(oldText), b = LinesOf(newText);
            Dictionary<string, int> bag = new Dictionary<string, int>();
            for (int i = 0; i < a.Length; i++)
            {
                string k = a[i];
                if (bag.ContainsKey(k)) bag[k] = bag[k] + 1; else bag[k] = 1;
            }
            added = 0;
            for (int i = 0; i < b.Length; i++)
            {
                string k = b[i];
                if (bag.ContainsKey(k) && bag[k] > 0) bag[k] = bag[k] - 1; else added++;
            }
            removed = 0;
            foreach (KeyValuePair<string, int> kv in bag) if (kv.Value > 0) removed += kv.Value;
            firstDiff = -1;
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++) if (a[i] != b[i]) { firstDiff = i + 1; break; }
            if (firstDiff < 0 && a.Length != b.Length) firstDiff = n + 1;
        }

        private static string Hash10(string s)
        {
            try
            {
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s ?? ""));
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < 5; i++) sb.Append(h[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return null; }
        }

        private static string AttrOf(string tag, string name)
        {
            int i = tag.ToLowerInvariant().IndexOf(name + "=\"");
            if (i < 0) return null;
            int j = i + name.Length + 2;
            int e = tag.IndexOf('"', j);
            if (e < 0) return null;
            return tag.Substring(j, e - j);
        }

        /// <summary>页面自报版本（meta name="app-version" 或 "version"），把差异讲成人话。</summary>
        public static string MetaVersion(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            string lower = html.ToLowerInvariant();
            int from = 0;
            while (from < lower.Length)
            {
                int i = lower.IndexOf("<meta", from);
                if (i < 0) break;
                int gt = html.IndexOf('>', i);
                if (gt < 0) break;
                string tag = html.Substring(i, gt - i);
                from = gt + 1;
                string nm = AttrOf(tag, "name");
                if (nm == "app-version" || nm == "version")
                {
                    string v = AttrOf(tag, "content");
                    if (!string.IsNullOrEmpty(v) && v.Trim().Length > 0) return v.Trim();
                }
            }
            return null;
        }

        private static Dictionary<string, object> SideInfo(string file, string text)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["file"] = string.IsNullOrEmpty(file) ? null : Path.GetFileName(file);
            d["size"] = text == null ? 0 : System.Text.Encoding.UTF8.GetByteCount(text);
            d["lines"] = LinesOf(text).Length;
            d["title"] = MainFrame.HtmlTitleFromText(text);
            d["version"] = MetaVersion(text);
            d["hash"] = Hash10(text);
            d["mtime"] = null;
            try { if (!string.IsNullOrEmpty(file) && File.Exists(file)) d["mtime"] = File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm"); }
            catch { }
            return d;
        }

        /// <summary>升级前比对（只读不写盘）。非本地页面类返回 null，由调用方给出提示。</summary>
        public static Dictionary<string, object> CompareUpgrade(string id, string newText)
        {
            Dictionary<string, object> app = FindApp(id);
            if (app == null) throw new Exception("应用不存在");
            string kind = Str(app, "kind");
            if (kind != "file" && kind != "folder") return null;
            if (newText == null) throw new Exception("新版本文件读不到内容");

            string entry = EntryFileOf(id);
            string oldText = entry != null ? StripBom(ReadTextAuto(entry)) : null;
            newText = StripBom(newText);

            int added, removed, firstDiff;
            DiffLines(oldText ?? "", newText, out added, out removed, out firstDiff);

            Dictionary<string, object> r = new Dictionary<string, object>();
            r["name"] = Str(app, "name");
            r["kind"] = kind;
            r["cur"] = SideInfo(entry, oldText);
            r["nw"] = SideInfo(null, newText);
            r["exists"] = oldText != null;
            r["same"] = oldText != null && string.Equals(Hash10(oldText), Hash10(newText), StringComparison.OrdinalIgnoreCase);
            r["added"] = added;
            r["removed"] = removed;
            r["firstDiff"] = firstDiff;

            // 用户最关心的一句话：数据会不会没。把保留项如实列出来。
            Dictionary<string, object> kept = new Dictionary<string, object>();
            kept["origin"] = "https://" + BuildHost(id) + "/";
            kept["dir"] = Data.AppDir(id);
            int files = 0; long bytes = 0;
            try
            {
                string od = Data.AppOutputDir(id);
                if (Directory.Exists(od))
                {
                    foreach (string f in Directory.GetFiles(od, "*", SearchOption.AllDirectories))
                    {
                        files++;
                        try { bytes += new FileInfo(f).Length; } catch { }
                    }
                }
            }
            catch { }
            kept["outputFiles"] = files;
            kept["outputBytes"] = bytes;
            r["kept"] = kept;
            return r;
        }

        /// <summary>执行升级：旧入口先备份到 html\.history（每个入口滚动留 5 份），再原地覆盖。
        /// id/origin 不变 → localStorage/IndexedDB 与 output 成果全部保留。返回备份路径（无旧文件则 null）。</summary>
        public static string ApplyUpgrade(string id, string newText)
        {
            Dictionary<string, object> app = FindApp(id);
            if (app == null) throw new Exception("应用不存在");
            string kind = Str(app, "kind");
            if (kind != "file" && kind != "folder") throw new Exception("该应用不是本地页面，无法用文件升级");
            if (newText == null) throw new Exception("新版本内容为空");

            string entry = EntryFileOf(id);
            string backup = null;
            if (entry != null)
            {
                string hist = Path.Combine(Path.GetDirectoryName(entry), ".history");
                try
                {
                    Directory.CreateDirectory(hist);
                    backup = Path.Combine(hist, UniqueHistoryName(hist, Path.GetFileNameWithoutExtension(entry), Path.GetExtension(entry)));
                    File.Copy(entry, backup, true);
                    // File.Copy 会沿用源文件的修改时间（可能是几周前），滚动清理就会按“内容年龄”
                    // 排序而误删刚生成的备份 → 备份时间统一记为当下
                    try { File.SetLastWriteTime(backup, DateTime.Now); } catch { }
                    TrimHistory(hist, Path.GetFileNameWithoutExtension(entry));
                }
                catch { backup = null; }
            }
            else
            {
                // 入口缺失（换机/误删）：按 target 文件名在 html/ 重建，origin 仍不变
                string htmlDir = Data.AppHtmlDir(id);
                Directory.CreateDirectory(htmlDir);
                string nm = null;
                try { nm = Path.GetFileName(Data.ResolveTarget(Str(app, "target"))); } catch { }
                if (string.IsNullOrEmpty(nm)) nm = "index.html";
                entry = Path.Combine(htmlDir, nm);
            }
            File.WriteAllText(entry, newText, new System.Text.UTF8Encoding(false));
            return backup;
        }

        /// <summary>备份文件名：同一秒内连续升级两次也要各留一份，否则旧备份会被覆盖掉。</summary>
        private static string UniqueHistoryName(string hist, string stem, string ext)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string name = stem + "." + stamp + ext;
            int n = 1;
            while (File.Exists(Path.Combine(hist, name)))
            {
                name = stem + "." + stamp + "-" + n + ext;
                n++;
                if (n > 999) { name = stem + "." + stamp + "-" + DateTime.Now.Millisecond + ext; break; }
            }
            return name;
        }

        private static void TrimHistory(string dir, string stem)
        {
            try
            {
                string[] all = Directory.GetFiles(dir, stem + ".*.html");
                if (all.Length <= 5) return;
                Array.Sort(all, delegate(string x, string y) { return File.GetLastWriteTimeUtc(x).CompareTo(File.GetLastWriteTimeUtc(y)); });
                for (int i = 0; i < all.Length - 5; i++) try { File.Delete(all[i]); } catch { }
            }
            catch { }
        }

        /// <summary>下载归位：返回该工具 output/ 下不重名的完整路径。</summary>
        public static string UniqueOutputPath(string id, string fileName)
        {
            string dir = Data.AppOutputDir(id);
            Directory.CreateDirectory(dir);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "download";
            fileName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
            string path = Path.Combine(dir, fileName);
            int n = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(dir, Path.GetFileNameWithoutExtension(fileName) + "-" + n + Path.GetExtension(fileName));
                n++;
            }
            return path;
        }

        /// <summary>把本地工具触发的下载重定向到 data\apps\&lt;id\&gt;\output\（“导出成果”一桶），无需再弹保存框。</summary>
        public static void WireDownload(CoreWebView2 core, string id)
        {
            if (core == null || string.IsNullOrEmpty(id)) return;
            core.DownloadStarting += delegate(object s, CoreWebView2DownloadStartingEventArgs ev)
            {
                try
                {
                    if (!IsLocalApp(id)) return; // 仅本地工具归位；url 类工具保持浏览器默认
                    ev.ResultFilePath = UniqueOutputPath(id, SuggestName(ev.DownloadOperation));
                }
                catch { }
            };
        }

        /// <summary>本 SDK 无 SuggestedFileName：从下载 Uri 末段推文件名，推不出则用时间戳名。</summary>
        private static string SuggestName(CoreWebView2DownloadOperation op)
        {
            try
            {
                if (op != null && !string.IsNullOrEmpty(op.Uri))
                {
                    string seg = new Uri(op.Uri).Segments.Length > 0
                        ? op.Uri.Substring(op.Uri.LastIndexOf('/') + 1) : "";
                    seg = Uri.UnescapeDataString(seg);
                    int q = seg.IndexOfAny(new char[] { '?', '#' });
                    if (q >= 0) seg = seg.Substring(0, q);
                    if (!string.IsNullOrEmpty(seg) && seg.IndexOf('.') > 0) return seg;
                }
            }
            catch { }
            return "export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        }

        /* ---------------- 开机自启（HKCU Run，无需额外引用） ---------------- */

        public static bool GetAutostart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(RunValue);
                    return v != null && !string.IsNullOrEmpty(Convert.ToString(v));
                }
            }
            catch { return false; }
        }

        public static void SetAutostart(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) throw new Exception("无法打开启动项注册表");
                if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RunValue, false);
            }
        }

        /* ---------------- 备份导出 / 导入（整包 + 轻量） ---------------- */

        public static long ExportBackup(string destZip, string level, out int entries)
        {
            entries = 0;
            using (FileStream fs = File.Create(destZip))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                if (File.Exists(Data.ConfigPath))
                    if (AddEntry(zip, Data.ConfigPath, "apps.json", true)) entries++;
                entries += AddTree(zip, Data.AppRootDir, "apps", false);
                if (string.Equals(level, "full", StringComparison.OrdinalIgnoreCase))
                    entries += AddTree(zip, Path.Combine(Data.WebDataDir, "EBWebView"), "webview/EBWebView", true);
            }
            try { return new FileInfo(destZip).Length; } catch { return 0; }
        }

        private static bool AddEntry(ZipArchive zip, string file, string entryName, bool tolerant)
        {
            try
            {
                using (FileStream src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    ZipArchiveEntry e = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    using (Stream dst = e.Open())
                        src.CopyTo(dst);
                }
                return true;
            }
            catch
            {
                if (tolerant) return false; // 整包里的被锁缓存文件跳过
                throw;
            }
        }

        private static int AddTree(ZipArchive zip, string baseDir, string prefix, bool tolerant)
        {
            int count = 0;
            if (!Directory.Exists(baseDir)) return 0;
            foreach (string file in Directory.GetFiles(baseDir, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(baseDir.Length).TrimStart('\\').Replace('\\', '/');
                string entry = prefix + "/" + rel;
                if (AddEntry(zip, file, entry, tolerant)) count++;
            }
            return count;
        }

        /// <summary>
        /// 导入备份。apps.json 与 apps/ 立即覆盖生效；若含内核数据（整包），暂存到待应用目录，
        /// 重启时（ApplyPendingWebview）替换 WebView2 profile，从而完整恢复各工具 localStorage。
        /// 返回是否为整包（含内核数据）。
        /// </summary>
        public static bool ImportBackup(string srcZip)
        {
            bool hasWebview = false;
            string pending = Path.Combine(Data.DataDir, "webview-import-pending");
            using (FileStream fs = File.Open(srcZip, FileMode.Open, FileAccess.Read))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                // 先判定档位
                foreach (ZipArchiveEntry e in zip.Entries)
                    if (e.FullName.StartsWith("webview/EBWebView/", StringComparison.OrdinalIgnoreCase)) { hasWebview = true; break; }

                // 清空当前 apps 目录（换机/覆盖导入）
                try
                {
                    if (Directory.Exists(Data.AppRootDir)) Directory.Delete(Data.AppRootDir, true);
                    Directory.CreateDirectory(Data.AppRootDir);
                }
                catch { }

                if (hasWebview && Directory.Exists(pending)) { try { Directory.Delete(pending, true); } catch { } }

                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name) && e.FullName.EndsWith("/")) continue; // 目录项
                    string name = e.FullName.Replace('\\', '/');
                    string dest;
                    if (name.Equals("apps.json", StringComparison.OrdinalIgnoreCase))
                        dest = Data.ConfigPath;
                    else if (name.StartsWith("apps/", StringComparison.OrdinalIgnoreCase))
                        dest = Path.Combine(Data.DataDir, name); // data/apps/...
                    else if (name.StartsWith("webview/EBWebView/", StringComparison.OrdinalIgnoreCase))
                        dest = Path.Combine(pending, "EBWebView", name.Substring("webview/EBWebView/".Length));
                    else
                        continue;
                    dest = Path.GetFullPath(dest);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        using (Stream s = e.Open())
                        using (FileStream d = File.Create(dest))
                            s.CopyTo(d);
                    }
                    catch { }
                }
            }
            return hasWebview;
        }

        /// <summary>启动早期（建窗口/环境前）应用上次导入暂存的内核数据，完成工具数据换机迁移。</summary>
        public static void ApplyPendingWebview()
        {
            try
            {
                string pending = Path.Combine(Data.DataDir, "webview-import-pending");
                string srcEb = Path.Combine(pending, "EBWebView");
                if (!Directory.Exists(srcEb)) return;
                string dstEb = Path.Combine(Data.WebDataDir, "EBWebView");
                if (Directory.Exists(dstEb)) { try { Directory.Delete(dstEb, true); } catch { } }
                CopyDir(srcEb, dstEb);
                try { Directory.Delete(pending, true); } catch { }
            }
            catch { }
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
            {
                try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); } catch { }
            }
            foreach (string d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }
    }
}
