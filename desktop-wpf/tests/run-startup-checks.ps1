param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskProject = Split-Path $PSScriptRoot
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskExecutable = Join-Path $taskOutput 'StartupChecks.exe'
& $taskCompiler /nologo /target:exe /codepage:65001 "/out:$taskExecutable" `
    (Join-Path $taskProject 'StartupRegistration.cs') (Join-Path $taskProject 'SessionDraft.cs') `
    (Join-Path $taskProject 'LegacyCodec.cs') (Join-Path $taskProject 'LegacyConfigurationStore.cs') `
    (Join-Path $PSScriptRoot 'StartupChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Startup tests build failed' }
& $taskExecutable $taskOutput | Tee-Object -FilePath (Join-Path $taskOutput 'startup-checks.txt')
if ($LASTEXITCODE -ne 0) { throw 'Startup tests failed' }
