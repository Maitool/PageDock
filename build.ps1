# 页坞 PageDock 构建脚本（使用系统自带 csc.exe，无需安装任何 SDK）
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '未找到 csc.exe（需要 .NET Framework 4.x，Windows 10/11 自带）' }

$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null

# 复制 UI、示例与全部 DLL（含原生加载器）——先清空目录避免 Copy-Item 嵌套复制
Remove-Item (Join-Path $bin 'ui'), (Join-Path $bin 'samples') -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root 'ui') (Join-Path $bin 'ui') -Recurse -Force
if (Test-Path (Join-Path $root 'samples')) { Copy-Item (Join-Path $root 'samples') (Join-Path $bin 'samples') -Recurse -Force }
Get-ChildItem (Join-Path $root 'lib') -Filter '*.dll' | ForEach-Object { Copy-Item $_.FullName $bin -Force }

# 引用：系统程序集 + lib 下解包的 WebView2 托管互操作 DLL（原生 WebView2Loader.dll 只复制不引用）
$refs = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll',
          '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll',
          '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll')
Get-ChildItem (Join-Path $root 'lib') -Filter 'Microsoft.Web.WebView2.*.dll' | ForEach-Object {
  if ($_.Name -notmatch 'Wpf') { $refs += ('/r:"' + $_.FullName + '"') }
}
$sources = Get-ChildItem (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }

& $csc /nologo /target:winexe /platform:x64 /optimize+ `
  /win32icon:"$root\assets\app.ico" /win32manifest:"$root\assets\app.manifest" `
  /resource:"$root\assets\app.ico",PageDock.app.ico `
  /resource:"$root\assets\app.png",PageDock.app.png `
  /out:"$bin\PageDock.exe" $refs $sources
if ($LASTEXITCODE -ne 0) { throw "编译失败：csc 退出码 $LASTEXITCODE" }
Write-Host "构建完成：$bin\PageDock.exe"
