param([string]$OutputDirectory = '', [string]$EngineExecutable = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'bin\EngineTrial' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$wpf = Join-Path $framework 'WPF'
if (-not (Test-Path -LiteralPath $compiler)) { throw '需要 Windows .NET Framework 4.x，或使用 .NET SDK 构建 csproj。' }
$arguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/codepage:65001',
  "/out:$output\MentoHUST.Desktop.Preview.exe",
  "/win32icon:$root\Assets\AppIcon.ico",
  "/reference:$wpf\PresentationFramework.dll", "/reference:$wpf\PresentationCore.dll",
  "/reference:$wpf\WindowsBase.dll", "/reference:$framework\System.Xaml.dll",
  '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
  "/resource:$root\MainWindow.xaml,MentoHUST.Desktop.MainWindow.xaml",
  "/resource:$root\Theme.xaml,MentoHUST.Desktop.Theme.xaml",
  "/resource:$root\..\VS2012\MentoHUST\res\jnu_emblem_official.png,MentoHUST.Desktop.Emblem.png",
  "/resource:$root\Assets\HeaderHD.png,MentoHUST.Desktop.HeaderHD.png",
  "/resource:$root\Assets\AppIcon.ico,MentoHUST.Desktop.AppIcon.ico",
  "/resource:$root\StartHotspot.ps1,MentoHUST.Desktop.StartHotspot.ps1",
    "$root\Program.cs", "$root\EmbeddedEngine.cs", "$root\SessionDraft.cs", "$root\EngineContract.cs", "$root\StartupAuthentication.cs",
  "$root\LegacyCodec.cs", "$root\LegacyConfigurationStore.cs", "$root\AdapterCatalog.cs", "$root\ProcessEngineClient.cs", "$root\HotspotAfterAuthentication.cs", "$root\StartupRegistration.cs")
if ($EngineExecutable) {
  $enginePath = (Resolve-Path -LiteralPath $EngineExecutable).Path
  $arguments += "/resource:$enginePath,MentoHUST.Desktop.Engine.exe"
}
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "WPF 编译失败：$LASTEXITCODE" }
Write-Output "WPF_PREVIEW_BUILD_PASS: $output/MentoHUST.Desktop.Preview.exe"
