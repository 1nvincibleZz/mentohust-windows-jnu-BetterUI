param([Parameter(Mandatory=$true)][string]$OutputDirectory, [Parameter(Mandatory=$true)][string]$EngineExecutable)
$ErrorActionPreference = 'Stop'
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
$taskEngine = (Resolve-Path -LiteralPath $EngineExecutable).Path
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskExecutable = Join-Path $taskOutput 'EmbeddedEngineChecks.exe'
& $taskCompiler /nologo /target:exe /codepage:65001 "/out:$taskExecutable" "/resource:$taskEngine,MentoHUST.Desktop.Engine.exe" (Join-Path (Split-Path $PSScriptRoot) 'EmbeddedEngine.cs') (Join-Path $PSScriptRoot 'EmbeddedEngineChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Embedded engine checks build failed' }
& $taskExecutable $taskOutput $taskEngine | Tee-Object -FilePath (Join-Path $taskOutput 'embedded-engine-checks.txt')
if ($LASTEXITCODE -ne 0) { throw 'Embedded engine checks failed' }
