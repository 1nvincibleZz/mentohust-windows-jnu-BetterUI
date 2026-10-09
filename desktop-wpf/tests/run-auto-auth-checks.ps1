param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskExecutable = Join-Path $taskOutput 'AutomaticAuthenticationChecks.exe'
& $taskCompiler /nologo /target:exe /codepage:65001 "/out:$taskExecutable" (Join-Path (Split-Path $PSScriptRoot) 'StartupAuthentication.cs') (Join-Path $PSScriptRoot 'AutomaticAuthenticationChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Automatic authentication checks build failed' }
& $taskExecutable | Tee-Object -FilePath (Join-Path $taskOutput 'auto-auth-checks.txt')
if ($LASTEXITCODE -ne 0) { throw 'Automatic authentication checks failed' }
