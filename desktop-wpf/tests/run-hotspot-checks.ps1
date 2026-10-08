param([string]$OutputDirectory = 'C:\Temp\mentohust-hotspot-checks')
$ErrorActionPreference = 'Stop'
$taskProject = Split-Path $PSScriptRoot
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskExe = Join-Path $taskOutput 'HotspotChecks.exe'
& $taskCompiler /nologo /target:exe /platform:anycpu /codepage:65001 "/out:$taskExe" "/resource:$taskProject\StartHotspot.ps1,MentoHUST.Desktop.StartHotspot.ps1" `
    (Join-Path $taskProject 'HotspotAfterAuthentication.cs') (Join-Path $taskProject 'EngineContract.cs') `
    (Join-Path $taskProject 'SessionDraft.cs') (Join-Path $taskProject 'LegacyCodec.cs') (Join-Path $taskProject 'LegacyConfigurationStore.cs') `
    (Join-Path $PSScriptRoot 'HotspotChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Hotspot checks build failed' }
& $taskExe | Tee-Object -FilePath (Join-Path $taskOutput 'flow-checks.txt')
if ($LASTEXITCODE -ne 0) { throw 'Hotspot flow checks failed' }
$taskErrors = $null; $taskTokens = $null
$taskAst = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $taskProject 'StartHotspot.ps1'), [ref]$taskTokens, [ref]$taskErrors)
if ($taskErrors.Count) { throw ($taskErrors | Out-String) }
$taskCommands = $taskAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true)
if (@($taskCommands | Where-Object { $_.GetCommandName() -eq 'Stop-Process' }).Count) { throw 'Do not use the broad process queries performed by Stop-Process' }
if (@($taskCommands | Where-Object { $_.GetCommandName() -in @('Stop-Service','Remove-Item','Set-Service','Restart-Service') }).Count) { throw 'Unexpected service or file mutation' }
$taskHelperText = [IO.File]::ReadAllText((Join-Path $taskProject 'StartHotspot.ps1'))
if ($taskHelperText -match '\.MainModule\b' -or $taskHelperText -notmatch 'QueryFullProcessImageName' -or $taskHelperText -notmatch 'OpenProcess\(0x101001') { throw 'Process validation must use limited-query, terminate and synchronize rights' }
if ([regex]::Matches($taskHelperText, '\.TerminateOnce\(\)').Count -ne 1 -or [regex]::Matches($taskHelperText, 'if \(!TerminateProcess\(').Count -ne 1) { throw 'Expected one guarded termination site' }
'HOTSPOT_HELPER_STATIC_PASS: syntax; one guarded native termination; no broad queries, service or file removal commands.' | Tee-Object -FilePath (Join-Path $taskOutput 'helper-checks.txt')
$taskTypeCommand = $taskCommands | Where-Object { $_.GetCommandName() -eq 'Add-Type' -and $_.Extent.Text -match 'MentoProcessAccess' } | Select-Object -First 1
$taskNativeSource = Join-Path $taskOutput 'NativeProcessAccess.cs'
[IO.File]::WriteAllText($taskNativeSource, $taskTypeCommand.CommandElements[-1].Value, [Text.UTF8Encoding]::new($false))
$taskFixtureDir = Join-Path $taskOutput ('fixture-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskFixtureDir -Force | Out-Null
$taskFixture = Join-Path $taskFixtureDir '8021x.exe'
& $taskCompiler /nologo /target:exe /codepage:65001 "/out:$taskFixture" (Join-Path $PSScriptRoot 'ProcessPermissionFixture.cs')
if ($LASTEXITCODE -ne 0) { throw 'Permission fixture build failed' }
$taskNativeExe = Join-Path $taskOutput 'NativeTerminationChecks.exe'
& $taskCompiler /nologo /target:exe /codepage:65001 "/out:$taskNativeExe" $taskNativeSource (Join-Path $PSScriptRoot 'NativeTerminationChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Native termination tests build failed' }
& $taskNativeExe $taskFixture | Tee-Object -FilePath (Join-Path $taskOutput 'native-termination-checks.txt')
if ($LASTEXITCODE -ne 0) { throw 'Native termination tests failed' }
