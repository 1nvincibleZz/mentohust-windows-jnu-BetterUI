param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot
$evidence = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('checks-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /platform:anycpu /codepage:65001 "/out:$evidence\EngineChecks.exe" "/reference:System.Core.dll" `
  (Join-Path $project 'EngineContract.cs') (Join-Path $project 'ProcessEngineClient.cs') (Join-Path $project 'SessionDraft.cs') `
  (Join-Path $project 'LegacyCodec.cs') (Join-Path $project 'LegacyConfigurationStore.cs') (Join-Path $PSScriptRoot 'EngineChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Engine check compile failed' }
& (Join-Path $evidence 'EngineChecks.exe') (Join-Path $project 'bin\EngineTrial\MentoHUST.Engine.exe') $evidence
if ($LASTEXITCODE -ne 0) { throw 'Engine checks failed' }
Write-Output "ENGINE_CHECK_EVIDENCE=$evidence"
