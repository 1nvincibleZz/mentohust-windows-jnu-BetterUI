param([Parameter(Mandatory=$true)][string]$OutputDirectory, [string]$VisualStudioRoot = 'D:\MicrosoftVisualStudio')
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot
$repo = Split-Path $project
$evidence = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('oracle-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$original = Get-Content -LiteralPath (Join-Path $repo 'VS2012\MentoHUST\Source\MentoHUST.cpp') -Raw
$key = [regex]::Matches($original, '(?m)^static const (?:unsigned char base64Tab|char xorRuijie)\[\].*$') | ForEach-Object Value
if ($key.Count -ne 2) { throw 'Could not locate original credential codec constants' }
$methods = foreach ($name in @('EncodeRuijie', 'DecodeRuijie')) {
  $match = [regex]::Match($original, "int $name\([^)]*\)\s*\{")
  if (-not $match.Success) { throw "Could not locate $name" }
  $position = $match.Index + $match.Length
  $depth = 1
  while ($depth -gt 0 -and $position -lt $original.Length) {
    if ($original[$position] -eq '{') { $depth++ }
    if ($original[$position] -eq '}') { $depth-- }
    $position++
  }
  $original.Substring($match.Index, $position - $match.Index).Replace("int $name", "extern `"C`" __declspec(dllexport) int $name")
}
$oracleSource = Join-Path $evidence 'legacy_codec_oracle.cpp'
Set-Content -LiteralPath $oracleSource -Value (@('#include <cstring>', 'typedef unsigned int UINT32;') + $key + $methods) -Encoding UTF8
& (Join-Path $VisualStudioRoot 'Common7\Tools\Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Null
$object = Join-Path $evidence 'codec.obj'
& cl.exe /nologo /LD /O2 /MD $oracleSource "/Fo$object" /link "/OUT:$evidence\legacy_codec_oracle.dll" "/IMPLIB:$evidence\legacy_codec_oracle.lib"
if ($LASTEXITCODE -ne 0) { throw 'Native codec oracle build failed' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$evidence\ConfigurationChecks.exe" "/reference:System.Core.dll" `
  (Join-Path $project 'SessionDraft.cs') (Join-Path $project 'LegacyCodec.cs') (Join-Path $project 'LegacyConfigurationStore.cs') (Join-Path $PSScriptRoot 'ConfigurationChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Configuration check build failed' }
& (Join-Path $evidence 'ConfigurationChecks.exe') $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Configuration checks failed' }
Write-Output "CONFIG_CHECK_EVIDENCE=$evidence"
