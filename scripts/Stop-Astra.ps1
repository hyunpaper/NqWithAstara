[CmdletBinding()]
param([string]$Root = '')
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) }
$pidFile = Join-Path $Root '.runtime/server.json'
if (-not (Test-Path $pidFile)) { Write-Host 'Astra 실행 기록이 없습니다.'; exit 0 }
$record = Get-Content -LiteralPath $pidFile -Raw | ConvertFrom-Json
$proc = Get-CimInstance Win32_Process -Filter "ProcessId = $($record.pid)" -ErrorAction SilentlyContinue
if (-not $proc) { Remove-Item -LiteralPath $pidFile -Force; Write-Host 'Astra 프로세스가 이미 종료되었습니다.'; exit 0 }
$cmd = [string]$proc.CommandLine
$app = [IO.Path]::GetFullPath([string]$record.app)
$executable = [IO.Path]::GetFullPath([string]$record.executable)
$runningExecutable = if ($proc.ExecutablePath) { [IO.Path]::GetFullPath([string]$proc.ExecutablePath) } else { '' }
$expectedStart = [DateTimeOffset]::Parse([string]$record.processStartedAt)
$actualStart = [DateTimeOffset]$proc.CreationDate.ToUniversalTime()
if (($cmd -notlike "*$app*") -or ($runningExecutable -and $runningExecutable -ne $executable) -or ([Math]::Abs(($actualStart - $expectedStart).TotalSeconds) -gt 3)) { throw '기록된 PID가 Astra 애플리케이션과 일치하지 않아 중지하지 않았습니다.' }
Stop-Process -Id ([int]$record.pid) -Force
Remove-Item -LiteralPath $pidFile -Force
Write-Host 'Astra를 중지했습니다.'
