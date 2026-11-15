# PageDock 界面风格 · 完整参考

来源：`ui/app.css`（首页）、`src/Theme.cs`（原生镜像色板）、`samples/时钟.html`、`samples/便签.html`（内置页实践）。
以下值即为“官方口径”，改动应回到这些文件同步。

## 1. 双主题 CSS 变量（整块复制）

```css
:root{
  --bg:#0B0C0E; --panel:#101216; --card:#131519; --card-hover:#171A1F;
  --border:#23262D; --border-strong:#2E323B;
  --text:#E7EAEE; --text-2:#9AA1AB; --text-3:#6B7280;
  --accent:#D7DCE2; --accent-dim:rgba(255,255,255,.09); --accent-ink:#0B0C0E;
  --danger:#F87171; --danger-bg:rgba(248,113,113,.12);
  --shadow-sm:0 0 0 1px rgba(255,255,255,.015);
  --shadow-md:0 8px 24px rgba(0,0,0,.35);
  --shadow-lg:0 24px 64px rgba(0,0,0,.55);
  --win-hover:rgba(255,255,255,.08);
  --sans:"Microsoft YaHei UI","Microsoft YaHei",system-ui,sans-serif;
  color-scheme:dark;
}
html[data-theme="light"]{
  --bg:#F6F7F8; --panel:#FFFFFF; --card:#FFFFFF; --card-hover:#F1F3F5;
  --border:#E4E7EB; --border-strong:#D3D8DE;
  --text:#17191C; --text-2:#565D66; --text-3:#7A828C;
  --accent:#17191C; --accent-dim:rgba(0,0,0,.06); --accent-ink:#FFFFFF;
  --danger:#DC2626; --danger-bg:rgba(220,38,38,.08);
  --shadow-sm:0 1px 2px rgba(16,24,40,.05);
  --shadow-md:0 4px 16px rgba(16,24,40,.09);
  --shadow-lg:0 24px 64px rgba(16,24,40,.18);
  --win-hover:rgba(0,0,0,.06);
  color-scheme:light;
}
```

内置页若希望“跟随系统”也随媒体查询自动切，可加一段媒体查询版浅色（时钟.html 用法）：

```css
:root{color-scheme:light dark; /* 深色为默认，同上 */}
@media (prefers-color-scheme: light){ :root{ /* 贴浅色那组变量 */ } }
html[data-theme="light"]{ /* 浅色 */ }
html[data-theme="dark"]{ /* 深色 */ }
```

## 2. 原生镜像色板（`src/Theme.cs`，非 CSS）

原生标题栏/标签栏/子窗口用 C# 常量，与上表一一对应。改主题色时两边都要动。

| Theme 属性 | 深色 | 浅色 |
|---|---|---|
| `WindowBg` | `11,12,14` | `246,247,248` |
| `Text` | `231,234,238` | `23,25,28` |
| `TextDim` | `154,161,171` | `122,130,140` |
| `Border` | `35,38,45` | `228,231,235` |
| `BtnFg` | `154,161,171` | `120,128,138` |
| `Hover` / `HoverSoft` | `42,45,51` / `34,37,43` | `226,230,234` / `237,240,243` |
| `CloseHover` | `196,43,28`（不分主题） | 同左 |
| **`Accent`（品牌青）** | `45,212,191` `#2DD4BF` | `11,128,116` `#0B8074` |
| `AccentSoft` | `67,222,201` | `10,148,136` |
| `AccentInk` | `6,40,35` | `#FFFFFF` |

> 注意：CSS 的 `--accent` 是“主按钮底色”（深底近白 / 浅底近黑），与原生 `Theme.Accent`（品牌青）**不是一回事**。选中态、焦点、图钉点亮、空态插画用品牌青；主按钮用 `--accent`。

## 3. 组件配方（CSS）

### 按钮
```css
.btn{height:30px;padding:0 13px;border-radius:9px;border:1px solid var(--border);
  background:var(--panel);color:var(--text);font-size:12.5px;cursor:pointer;
  transition:background .14s,border-color .14s,color .14s}
.btn:hover{background:var(--card-hover);border-color:var(--border-strong)}
.btn.primary{background:var(--accent);border-color:transparent;color:var(--accent-ink);font-weight:600}
.btn.primary:hover{filter:brightness(1.08)}
.btn.ghost{background:transparent}
.btn.mini{height:24px;padding:0 10px;font-size:11.5px;border-radius:7px}
.btn.danger-ghost{color:var(--danger);border-color:rgba(248,113,113,.35);background:transparent}
.btn.danger-ghost:hover{background:var(--danger-bg);border-color:rgba(248,113,113,.55)}
.btn:focus-visible{outline:2px solid var(--text-3);outline-offset:1px}
```

### 输入框
```css
.field input[type=text]{width:100%;height:32px;background:var(--card);border:1px solid var(--border);
  border-radius:9px;color:var(--text);padding:0 10px;font-size:12.5px;outline:none;
  transition:border-color .14s,box-shadow .14s}
.field input[type=text]:focus{border-color:var(--text-3);box-shadow:0 0 0 3px var(--accent-dim)}
```

### 分段控件（seg）
```css
.seg{display:flex;background:var(--card);border:1px solid var(--border);border-radius:9px;padding:3px;gap:3px}
.seg button{flex:1;height:26px;border:none;background:transparent;color:var(--text-2);font-size:12px;
  border-radius:6px;cursor:pointer;transition:background .14s,color .14s}
.seg button:hover{color:var(--text)}
.seg button.on{background:var(--accent-dim);color:var(--text)}
```

### 开关（switch）
```css
.switch .track{width:34px;height:19px;border-radius:999px;background:var(--border);position:relative;transition:background .16s;flex:none}
.switch .track::after{content:"";position:absolute;top:2px;left:2px;width:15px;height:15px;border-radius:50%;
  background:var(--panel);box-shadow:0 1px 2px rgba(0,0,0,.3);transition:transform .16s}
.switch input:checked + .track{background:var(--accent)}
.switch input:checked + .track::after{transform:translateX(15px)}
```

### 右键菜单 / 浮层 / 弹窗
```css
.ctx{position:fixed;z-index:90;min-width:170px;background:var(--bg);border:1px solid var(--border-strong);
  border-radius:12px;padding:6px;box-shadow:var(--shadow-lg);animation:fade .1s ease}
.ctx button{display:flex;width:100%;align-items:center;border:none;background:transparent;color:var(--text);
  font-size:12.5px;padding:7px 11px;border-radius:7px;cursor:pointer}
.ctx button:hover{background:var(--win-hover)}
.ctx button.danger{color:var(--danger)} .ctx button.danger:hover{background:var(--danger-bg)}

#modal-root{position:fixed;inset:0;z-index:50;display:none;align-items:center;justify-content:center;
  background:rgba(5,6,8,.55);backdrop-filter:blur(3px);animation:fade .15s ease}
html[data-theme="light"] #modal-root{background:rgba(16,24,40,.3)}
#modal-root.open{display:flex}
.modal{width:500px;max-width:calc(100vw - 48px);background:var(--panel);border:1px solid var(--border-strong);
  border-radius:14px;box-shadow:var(--shadow-lg);animation:pop .16s ease;overflow:hidden}
```

### 提示 toast / 滚动条 / 动画
```css
.toast{background:var(--card);border:1px solid var(--border-strong);color:var(--text);font-size:12px;
  padding:8px 14px;border-radius:9px;box-shadow:var(--shadow-md);animation:rise .18s ease}
::-webkit-scrollbar{width:10px;height:10px}
::-webkit-scrollbar-thumb{background:var(--border);border-radius:6px;border:2px solid var(--bg)}
::-webkit-scrollbar-thumb:hover{background:var(--border-strong)}
@keyframes fade{from{opacity:0}to{opacity:1}}
@keyframes pop{from{opacity:0;transform:translateY(6px) scale(.98)}to{opacity:1;transform:none}}
@keyframes rise{from{opacity:0;transform:translateY(8px)}to{opacity:1;transform:none}}
```

### 应用磁贴（首页 grid 用；内置页一般不需要）
```css
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(92px,1fr));gap:16px;justify-items:center}
.card{width:68px;height:68px;border-radius:17px;cursor:pointer}
.tile{width:68px;height:68px;border-radius:17px;display:flex;align-items:center;justify-content:center;
  font-weight:600;font-size:22px;box-shadow:var(--shadow-sm);
  transition:box-shadow .16s ease,transform .18s cubic-bezier(.34,1.56,.64,1)}
.card:hover .tile{transform:translateY(-3px);box-shadow:var(--shadow-md)}
```

## 4. 主题握手细节

- 宿主在**每个页面** document 创建时注入 `Theme.FirstPaintScript()`：先写 `window.__pdTheme`（`'dark'|'light'`），再 `documentElement.setAttribute('data-theme', t)`。页面首帧脚本据此渲染，**不要**自己去问 `prefers-color-scheme`（WebView2 色彩偏好异步生效且会记住上次运行，首帧可能是旧值 → 闪色）。
- “跟随页坞”：页面**移除** `data-theme`，落到 `color-scheme:light dark` + 媒体查询；宿主通过 WebView2 `PreferredColorScheme`（跟随系统用 `Auto`）驱动媒体查询结果。
- 手动浅色/深色：`setAttribute('data-theme','light'|'dark')`，覆盖媒体查询。
- 原生侧：`Theme.ResolveFromConfig()` 必须在创建任何窗体前调用（读 `apps.json` 的 `settings.theme`），否则控件构造时读到的 `Theme.Dark` 未定，会先画一帧错色。

## 5. 数据与命名约定

- `localStorage` 键：`pagedock-<工具>-settings`（配置对象 JSON）、`pagedock-<工具>-hint`（一次性提示）等。按工具前缀隔离，避免相互污染。
- 内置页配置用 `Object.assign({}, DEFAULTS, JSON.parse(raw))` 合并，向前兼容新增字段。
- 小组件模式通过 URL 参数区分（如 `?widget=1`），页面据此切换精简布局。

## 6. 尺寸/间距经验值

- 首页正文 `#main` padding `14px 16px 8px`；`scrollbar-gutter:stable`（防拖窗宽时磁贴抖动）。
- 页脚高 `30px`，字号 `11px`；chip 药丸 `padding:1px 8px`。
- 弹窗 body padding `20px`；分组标题 `.set-sec` 字号 `10.5px`、`letter-spacing:1px`。
