param([string]$VisualStudioRoot = 'D:\MicrosoftVisualStudio', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$repo = Split-Path $root
$legacy = Join-Path $repo 'VS2012\MentoHUST'
$source = Join-Path $legacy 'Source'
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'bin\EngineTrial' }
$generated = Join-Path $root 'obj\Engine'
New-Item -ItemType Directory -Path $output,$generated -Force | Out-Null
# Only includes change in a generated compilation unit. Protocol functions stay byte-for-byte.
$byteEncoding = [Text.Encoding]::GetEncoding(28591)
$process = $byteEncoding.GetString([IO.File]::ReadAllBytes((Join-Path $source 'Process.cpp')))
foreach ($header in @('MentoHUST.h','MentoHUSTDlg.h')) {
  if (-not $process.Contains("#include `"$header`"")) { throw "Missing expected include $header" }
  $process = $process.Replace("#include `"$header`"", '#include "EngineHost.h"')
}
[IO.File]::WriteAllBytes((Join-Path $generated 'Process.bridge.cpp'), $byteEncoding.GetBytes($process))
$original = [IO.File]::ReadAllText((Join-Path $source 'MentoHUST.cpp'))
$keys = [regex]::Matches($original, '(?m)^static const (?:unsigned char base64Tab|char xorRuijie)\[\].*$') | ForEach-Object Value
if ($keys.Count -ne 2) { throw 'Original codec constants missing' }
$method = [regex]::Match($original, 'int DecodeRuijie\([^)]*\)\s*\{')
if (-not $method.Success) { throw 'Original decoder missing' }
$position = $method.Index + $method.Length; $depth = 1
while ($depth -gt 0 -and $position -lt $original.Length) {
  if ($original[$position] -eq '{') { $depth++ }
  if ($original[$position] -eq '}') { $depth-- }
  $position++
}
$decoder = @('#include "stdafx.h"') + $keys + $original.Substring($method.Index, $position-$method.Index)
[IO.File]::WriteAllText((Join-Path $generated 'LegacyDecoder.cpp'), ($decoder -join "`r`n"), [Text.UTF8Encoding]::new($true))
$tables = [regex]::Matches([IO.File]::ReadAllText((Join-Path $legacy 'MentoHUST.rc')), '(?ms)^STRINGTABLE[^\r\n]*\r?\nBEGIN\r?\n.*?^END') | ForEach-Object Value
if ($tables.Count -lt 3) { throw 'Original string resources missing' }
$resource = '#include "resource.h"' + "`r`nLANGUAGE 4, 2`r`n" + ($tables -join "`r`n")
[IO.File]::WriteAllText((Join-Path $generated 'EngineStrings.rc'), $resource, [Text.UTF8Encoding]::new($true))
& (Join-Path $VisualStudioRoot 'Common7\Tools\Launch-VsDevShell.ps1') -Arch x86 -HostArch amd64 -SkipAutomaticLocation | Out-Null
Push-Location $generated
try {
  & rc.exe /nologo /c65001 "/I$legacy" /foEngineStrings.res EngineStrings.rc
  if ($LASTEXITCODE -ne 0) { throw 'Engine resource build failed' }
  & cl.exe /nologo /O2 /MD /EHsc /std:c++17 /utf-8 /wd4828 /D_AFXDLL /DWIN32 /D_WINDOWS /DUNICODE /D_UNICODE /DNDEBUG /D_CRT_SECURE_NO_WARNINGS `
    "/I$root\native" "/I$source" "/I$source\Other" "/I$legacy" `
    "$root\native\EngineHost.cpp" Process.bridge.cpp LegacyDecoder.cpp "$source\Other\md5.c" EngineStrings.res `
    /link /SUBSYSTEM:WINDOWS /ENTRY:wWinMainCRTStartup /DELAYLOAD:wpcap.dll "/LIBPATH:$source\Other" ole32.lib "/OUT:$output\MentoHUST.Engine.exe"
  if ($LASTEXITCODE -ne 0) { throw 'Engine build failed' }
} finally { Pop-Location }
Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $source 'Process.cpp'),(Join-Path $source 'Process.h') | Format-Table Path,Hash -AutoSize
Write-Output "ENGINE_BUILD_PASS: $output\MentoHUST.Engine.exe"
