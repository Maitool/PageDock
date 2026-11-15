using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace PageDock
{
    /// <summary>数据目录解析与首运行种子数据。</summary>
    internal static class Data
    {
        public const string Version = "0.9.27";

        public static string ExeDir;
        public static string DataDir;
        public static string WebDataDir;
        public static string ConfigPath;
        public static string UiUri;
        public static string AppRootDir; // data\apps —— 每个工具一个稳定身份目录

        public static void Init()
        {
            ExeDir = AppDomain.CurrentDomain.BaseDirectory;
            DataDir = TryMakeWritable(Path.Combine(ExeDir, "data"));
            if (DataDir == null)
            {
                // exe 目录不可写（如放在 Program Files）时退回用户本地目录
                DataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PageDock");
                Directory.CreateDirectory(DataDir);
            }
            ConfigPath = Path.Combine(DataDir, "apps.json");
            WebDataDir = Path.Combine(DataDir, "webview");
            Directory.CreateDirectory(WebDataDir);
            AppRootDir = Path.Combine(DataDir, "apps");
            Directory.CreateDirectory(AppRootDir);
            UiUri = new Uri(Path.Combine(ExeDir, "ui", "index.html")).AbsoluteUri;
            SeedIfMissing();
        }

        /* ---------------- 三桶目录（身份 = 应用 id，与路径无关） ---------------- */

        /// <summary>把 id 规整成安全目录名（id 本为 8 位十六进制，防御性过滤）。</summary>
        public static string SafeId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "_";
            char[] buf = new char[id.Length];
            int n = 0;
            foreach (char c in id)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                buf[n++] = ok ? c : '_';
            }
            return new string(buf, 0, n);
        }

        public static string AppDir(string id) { return Path.Combine(AppRootDir, SafeId(id)); }
        public static string AppHtmlDir(string id) { return Path.Combine(AppDir(id), "html"); }
        public static string AppOutputDir(string id) { return Path.Combine(AppDir(id), "output"); }

        private static string TryMakeWritable(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".wtest");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return dir;
            }
            catch { return null; }
        }

        /// <summary>首次运行时，若目录中带有示例页面则预置两条应用。</summary>
        private static void SeedIfMissing()
        {
            if (File.Exists(ConfigPath)) return;
            List<object> apps = new List<object>();
            AddSample(apps, "时钟", "时钟.html");
            AddSample(apps, "便签", "便签.html");
            if (apps.Count == 0) return;
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["version"] = 1;
            root["apps"] = apps;
            JavaScriptSerializer ser = new JavaScriptSerializer();
            File.WriteAllText(ConfigPath, ser.Serialize(root));
        }

        private static void AddSample(List<object> apps, string name, string file)
        {
            string p = Path.Combine(ExeDir, "samples", file);
            if (!File.Exists(p)) return;
            Dictionary<string, object> a = new Dictionary<string, object>();
            a["id"] = Guid.NewGuid().ToString("N").Substring(0, 8);
            a["name"] = name;
            a["kind"] = "file";
            // 内置示例写相对路径（而不是绝对路径）：整个程序连 data 一起拷到别的电脑、
            // 或换个目录解压时，绝对路径会失效，表现就是“内置的时钟/便签不存在”
            a["target"] = Path.Combine("samples", file);
            a["openMode"] = "inplace";
            a["createdAt"] = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
            a["lastOpenedAt"] = 0;
            a["opens"] = 0;
            apps.Add(a);
        }

        /// <summary>
        /// 把配置里的 target 解析成本机真正可用的路径。
        /// 兼容三种情况：相对路径按 exe 目录展开；绝对路径仍在原位时原样返回；
        /// 绝对路径已失效（换机器/换目录的老配置）时回退到 exe\samples\文件名 与 exe\文件名。
        /// </summary>
        public static string ResolveTarget(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            try
            {
                if (!Path.IsPathRooted(p))
                {
                    string rel = Path.GetFullPath(Path.Combine(ExeDir, p));
                    if (File.Exists(rel) || Directory.Exists(rel)) return rel;
                    return p;
                }
                if (File.Exists(p) || Directory.Exists(p)) return p;
                string name = Path.GetFileName(p);
                if (string.IsNullOrEmpty(name)) return p;
                string cand = Path.Combine(ExeDir, "samples", name);
                if (File.Exists(cand)) return cand;
                cand = Path.Combine(ExeDir, name);
                if (File.Exists(cand)) return cand;
            }
            catch { }
            return p;
        }

        public static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }
    }
}
