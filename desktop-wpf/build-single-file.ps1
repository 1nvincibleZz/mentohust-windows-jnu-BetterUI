param(
  [string]$OutputDirectory = '',
  [string]$EngineExecutable = '',
  [string]$VisualStudioRoot = 'D:\MicrosoftVisualStudio',
  [string]$Version = 'v1.0.2-wpf'
)
$ErrorActionPreference = 'Stop'
$taskOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $PSScriptRoot 'bin\SingleFile' }
if ($Version -notmatch '^[a-zA-Z0-9._-]+$') { throw '版本号只能包含字母、数字、点、下划线及连字符。' }
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
if (-not $EngineExecutable) {
  $taskNativeOutput = Join-Path $taskOutput 'native'
  & (Join-Path $PSScriptRoot 'build-engine.ps1') -OutputDirectory $taskNativeOutput -VisualStudioRoot $VisualStudioRoot
  $EngineExecutable = Join-Path $taskNativeOutput 'MentoHUST.Engine.exe'
}
$taskPackage = Join-Path $taskOutput 'MentoHUST-BetterUI'
New-Item -ItemType Directory -Path $taskPackage -Force | Out-Null
# Prevent stale files in a reused directory from leaking into the public package.
$taskUnexpected = @(Get-ChildItem -LiteralPath $taskPackage -Force | Where-Object Name -NotIn @('MentoHUST.BetterUI.exe','README.txt'))
if ($taskUnexpected.Count) { throw '发布目录包含其他文件，请指定新的输出目录。' }
& (Join-Path $PSScriptRoot 'build-preview.ps1') -OutputDirectory (Join-Path $taskOutput 'compiled') -EngineExecutable $EngineExecutable
Copy-Item -LiteralPath (Join-Path $taskOutput 'compiled\MentoHUST.Desktop.Preview.exe') -Destination (Join-Path $taskPackage 'MentoHUST.BetterUI.exe') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README-user.txt') -Destination (Join-Path $taskPackage 'README.txt') -Force
$taskArchive = Join-Path $taskOutput "MentoHUST-BetterUI-$Version.zip"
Compress-Archive -LiteralPath (Join-Path $taskPackage 'MentoHUST.BetterUI.exe'),(Join-Path $taskPackage 'README.txt') -DestinationPath $taskArchive -Force
$taskHash = Get-FileHash -Algorithm SHA256 -LiteralPath $taskArchive
# Keep this maintainer record outside the two-file public ZIP.
[IO.File]::WriteAllText((Join-Path $taskOutput 'SHA256.txt'), "$($taskHash.Hash)  $([IO.Path]::GetFileName($taskArchive))`r`n", [Text.Encoding]::ASCII)
Write-Output "SINGLE_FILE_PACKAGE_PASS: $taskArchive"
