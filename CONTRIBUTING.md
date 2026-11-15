# 贡献指南

欢迎 Issue、功能建议和 Pull Request。

## 本地构建

环境：Windows 10/11、.NET Framework 4.x（系统自带 `csc.exe`）、Microsoft Edge WebView2 Runtime。
项目不依赖 Visual Studio 或 .NET SDK。

```powershell
.\build.ps1
```

产物在 `bin\PageDock.exe`；`ui\`、`samples\`、`lib\` 一并复制到 `bin\`。

## 修改范围

- `src\`：WinForms 宿主（窗口、标签栏、全屏、WebView2 通信）
- `ui\`：工作台页面
- `samples\`：内置 HTML 单文件工具（便签、时钟）
- `README.md`：功能与变更记录

## 提交前

- 每次功能更新同步 6 处版本号（`src/Data.cs`、`ui/index.html`、`ui/app.js`、`assets/app.manifest`、`samples/便签.html`、`README.md`）；
- 运行 `node --check ui/app.js` 及 HTML 内脚本语法检查；
- 不要提交 `data/`、`bin/`、`staging/` 等运行期内容。

提交 PR 时请说明动机、改动范围和验证方式；UI 改动建议附截图。
