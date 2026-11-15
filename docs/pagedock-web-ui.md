---
name: pagedock-web-ui
description: PageDock（页坞）主程序首页与内置网页程序（时钟、便签等）的统一界面风格指南——双主题 CSS 变量色板、字体/圆角/阴影/间距刻度、常用组件配方（按钮/磁贴/右键菜单/弹窗/开关/分段控件/提示/滚动条）、首帧主题握手与 localStorage 命名约定。在本仓库开发或修改任何 ui/ 或 samples/ 下的 HTML/CSS、新增内置小工具或网页程序、需要与页坞现有视觉语言保持一致时使用。
---

# PageDock 网页界面统一风格

页坞（PageDock）的所有网页界面——首页 `ui/` 与内置程序 `samples/*.html`——共用同一套设计语言。
本 skill 提炼该语言，使新页面开箱即与现有界面一致，而不是各画各的。

## 何时使用

- 新增一个内置网页程序 / 小工具（`samples/*.html`）
- 修改首页 `ui/app.css`、`ui/index.html` 的样式或组件
- 让任意嵌入页坞的 HTML 与主程序视觉统一（配色、圆角、字体、控件手感）

## 核心原则

1. **一切走 CSS 变量，不写死颜色。** 深色为默认（`:root`），浅色用 `html[data-theme="light"]` 覆盖。
   页面里只引用 `var(--text)` 等，绝不在组件上硬编码 `#xxxxxx`。
2. **主题三态：跟随 / 浅色 / 深色。** “跟随”=不设置 `data-theme`，交给 `prefers-color-scheme`（页坞会同步 WebView 色彩偏好）；手动则设 `data-theme="light|dark"`。
3. **首帧不闪色。** 页坞宿主会在 document 创建前注入 `window.__pdTheme`；页面首帧脚本应优先读它，避免浅色系统上先渲染一帧深色。
4. **圆角/间距/阴影用刻度值**，不要随手取整。见下方刻度表。
5. **数据按工具隔离**：`localStorage` 键一律 `pagedock-<工具名>-<用途>`。

## 设计令牌（速查）

> 完整双主题变量清单、原生镜像色板、全部组件 CSS 见 [reference.md](reference.md)；可直接粘贴的起步骨架见 [template.html](template.html)。

| 令牌 | 深色 | 浅色 | 用途 |
|---|---|---|---|
| `--bg` | `#0B0C0E` | `#F6F7F8` | 窗口/页面底 |
| `--panel` | `#101216` | `#FFFFFF` | 面板/输入底/弹窗 |
| `--card` / `--card-hover` | `#131519` / `#171A1F` | `#FFFFFF` / `#F1F3F5` | 卡片/磁贴底 |
| `--border` / `--border-strong` | `#23262D` / `#2E323B` | `#E4E7EB` / `#D3D8DE` | 描边 |
| `--text` / `--text-2` / `--text-3` | `#E7EAEE` / `#9AA1AB` / `#6B7280` | `#17191C` / `#565D66` / `#7A828C` | 主/次/弱文字 |
| `--accent` / `--accent-ink` | `#D7DCE2` / `#0B0C0E` | `#17191C` / `#FFFFFF` | 主按钮底/前景（近白↔近黑反转） |
| `--danger` | `#F87171` | `#DC2626` | 危险操作 |

**品牌强调色（青，用于选中态/焦点/原生栏点亮，区别于上面的 `--accent`）**：深色 `#2DD4BF`、浅色 `#0B8074`（对应原生 `Theme.Accent`）。首页空态 SVG、标签选中、图钉点亮都用它。

**字体**：`--sans:"Microsoft YaHei UI","Microsoft YaHei",system-ui,sans-serif`；正文基准 `13px`，`-webkit-font-smoothing:antialiased`。

**圆角刻度**：按钮/输入/分段 `9px`（mini `7px`）；磁贴 `17px`(68px)；菜单 `12px`；弹窗 `14px`；浮层 `16px`；药丸/开关轨道 `999px`。

**阴影**：`--shadow-sm/md/lg` 三档，深浅色各一套（见 reference）。

## 起步做法

新建内置页时，**复制 [template.html](template.html)**，它已含：双主题变量块、首帧主题脚本、`--sans` 字体、以及开关/分段控件等常用配方。把内容区换成你的功能即可，无需重新设计样式。

## 关键约定

- **首帧脚本**（放在 `<head>`、早于任何样式与脚本）：
  ```html
  <script>(function(){try{var t=window.__pdTheme||(matchMedia('(prefers-color-scheme: light)').matches?'light':'dark');document.documentElement.setAttribute('data-theme',t);}catch(e){}})();</script>
  ```
- **主题切换**（页面内设置项“跟随/浅/深”）：跟随=移除 `data-theme`，手动=`setAttribute('data-theme',v)`；结果存 `localStorage`。
- **过渡**：颜色/背景/边框统一 `transition:background-color .18s ease,border-color .18s ease,color .18s ease`；磁贴悬停用弹性曲线 `cubic-bezier(.34,1.56,.64,1)`。
- **可访问性**：`@media (forced-colors: active)` 给浮层补 `border:1px solid CanvasText`；交互元素留 `:focus-visible` 轮廓。
- **DPI**：网页用响应式单位（`clamp()`、`%`、`rem`）即可，WebView2 自动缩放；不要照搬原生栏的固定像素。

## 参考

- [reference.md](reference.md) — 完整令牌表、原生 `Theme.cs` 镜像色、全部组件 CSS 配方、主题握手细节、localStorage 命名。
- [template.html](template.html) — 可直接复制的内置页起步骨架。
