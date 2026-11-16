# 页坞 PageDock

> 轻量网页应用工作台 —— 把网址、HTML 单文件、离线小应用停进同一个码头。
> Windows 原生壳 + 系统 WebView2 运行时，主程序 77 KB，免安装、免运行库、单实例共享一个浏览器进程。

**当前版本：v0.9.28** · License: MIT

## 名称释义

**页坞 · PageDock**：「页」即网页，「坞」即船坞——网页应用的停泊处，点一下即起航。

## 功能亮点

- **一体化标签栏**：logo 回主页、应用标签 + 搜索框 + 窗口控制同栏，高分屏按 DPI 缩放
- **主页卡片与分组**：拖拽排序、拖叠成组、悬停展开、内置程序化图标生成（5 形状 × 4 底色 × 4 纹理，1–4 字自动排版）、全站统一取色面板
- **双形态打开**：标签页常驻工作台，或独立成窗 / 钉到桌面小部件（真桌面层，位置尺寸记忆，重启还原）
- **多主题**：深色默认、浅色、跟随系统（原生栏与页面同帧同步，无闪色）
- **全局呼出**：`Ctrl+Alt+D`（可自定义组合键）+ 单实例唤起；窗口置顶图钉
- **数据隔离**：每个工具独立 origin，localStorage/IndexedDB 天然分桶；`data\apps\<id>\` 三桶目录（源 / 导出成果 / 历史版本）
- **备份与迁移**：整包（含 WebView 运行数据）/ 轻量两种导出导入；开机自启
- **拖拽升级**：新版 HTML 拖到卡片先出比对表再覆盖，id/origin/数据不变，旧版自动进 `.history`
- **省内存**：后台标签自动挂起，空闲 10 分钟释放 WebView，激活时重建；所有窗口共享一个浏览器进程
- **全屏模式**：主窗口与独立窗口一键全屏，鼠标上移屏幕顶部浮现退出按钮，Esc 也可退出

### 内置工具

**便签**：富文本（字体/字号/行距/颜色/高亮/对齐/缩进/列表/引用）、表格（Word 式工具条：行列/合并/边框/底纹/排序/文字方向/标题行重复）、待办勾选框、图片拖角缩放与裁剪、插入 64 常用符号、多便签侧边栏、导出 txt/md/html/doc、桌面小部件形态

**时钟**：12/24 制、显示秒与日期、字号三档、防烧屏微移、桌面小部件形态

## 快速开始

1. 从 [Releases](https://github.com/Maitool/PageDock/releases) 下载 `PageDock-vX.Y.Z-win64.zip`，解压即用
2. 首次启动自动注册全局热键 `Ctrl+Alt+D`；把网址或 HTML 文件拖进窗口即可加入
3. exe 未签名，SmartScreen 提示时选「更多信息 → 仍要运行」

## 快捷键

| 快捷键 | 作用 |
|---|---|
| `Ctrl+Alt+D`（默认，可自定义） | 全局呼出 / 隐藏 |
| `Ctrl+K` | 聚焦搜索框 |
| `Ctrl+Alt+←/→` | 循环切换标签 |
| `Esc` | 关闭弹窗 / 退出全屏 |

## 架构

```
PageDock.exe (77KB, C#/WinForms, .NET Framework 4.8 系统自带)
 ├─ ui/index.html + app.css + app.js     工作台界面（双主题设计令牌）
 ├─ src/TabStrip.cs                      自绘导航/标签栏（每标签独立 WebView2）
 ├─ src/TitleBar.cs + IconButton.cs      独立窗口标题栏与统一线性图标按钮
 ├─ src/TabLifecycle.cs                  后台挂起 / 空闲释放生命周期管理
 ├─ Microsoft.Web.WebView2.*.dll         WebView2 托管互操作
 ├─ WebView2Loader.dll                   WebView2 原生加载器
 └─ msedgewebview2.exe（系统共享运行时）  共享浏览器进程，多标签/多窗口省内存
```

- 标签页与独立子窗口共享一个 `CoreWebView2Environment`；`RegisterHotKey` 实现全局呼出
- 主题经 `set-theme` 消息同步原生 UI，并写入 `Profile.PreferredColorScheme` 让页面跟随
- 钉到桌面的小部件走真桌面层（Progman 子窗口），字符串 `Navigate()` 携带 `?widget=1`

## 设计规范

主程序与内置工具的界面语言提炼在 [`docs/pagedock-web-ui.md`](docs/pagedock-web-ui.md)（总则 + 反模式清单）、[`docs/pagedock-web-ui-reference.md`](docs/pagedock-web-ui-reference.md)（完整令牌/组件/机制）、[`docs/pagedock-web-ui-template.html`](docs/pagedock-web-ui-template.html)（可直接复制的新工具起步骨架）。

想给自己的工具做配套界面（配色、hover、弹窗、小部件、数据存储），照着这三份做即可与主程序融为一体。

## 构建与开发

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

系统自带 `csc.exe` 编译，无需 SDK；依赖 `lib\` 下 WebView2 互操作 DLL（已就位）。
目录：`src\` 宿主 · `ui\` 工作台 · `samples\` 内置工具 · `docs\` 设计规范 · `assets\` 图标清单。

## 常见问题

- **SmartScreen**：exe 未签名，「更多信息 → 仍要运行」
- **热键无反应**：`Ctrl+Alt+D` 可能被其他软件占用，`data\hotkey.log` 可查注册结果
- **favicon 抓不到**：自动回退，也可在编辑里指定本地图片
- **数据在哪**：`data\apps.json`（清单）+ `data\apps\<id>\`（各工具源与导出成果）+ `data\webview\`（内核运行数据），删除即重置

## 开源信息

- 许可证：MIT License（见 [LICENSE](LICENSE)）
- 贡献：见 [CONTRIBUTING.md](CONTRIBUTING.md)；安全漏洞报告：见 [SECURITY.md](SECURITY.md)
- 程序设置页底部有 GitHub 版权信息，点击直达本仓库