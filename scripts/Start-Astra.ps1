[CmdletBinding()]
param(
    [string]$Root = '',
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) }
$port = 5188
$runtime = Join-Path $Root '.runtime'
$pidFile = Join-Path $runtime 'server.json'
$logFile = Join-Path $runtime 'server.log'
$errorLogFile = Join-Path $runtime 'server-error.log'
$clientDir = Join-Path $Root 'client'

function Fail([string]$message) {
    Write-Error "Astra 시작 실패: $message"
    exit 1
}

function Require-Command([string]$name, [string]$installHint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) { Fail "$name 명령을 찾을 수 없습니다. $installHint" }
}

function Find-Project {
    $candidates = @(
        (Join-Path $Root 'server'), (Join-Path $Root 'backend'),
        (Join-Path $Root 'src/server'), (Join-Path $Root 'src/backend')
    ) | Where-Object { Test-Path $_ }
    $projects = foreach ($dir in $candidates) { Get-ChildItem -LiteralPath $dir -Filter '*.csproj' -File -Recurse -ErrorAction SilentlyContinue }
    if (-not $projects) { Fail 'server 또는 backend 폴더에서 .csproj를 찾지 못했습니다.' }
    if ($projects.Count -gt 1) {
        $preferred = $projects | Where-Object { $_.Name -match 'Api|Server|Backend' } | Select-Object -First 1
        if ($preferred) { return $preferred }
    }
    return ($projects | Select-Object -First 1)
}

function Test-AstraHealth {
    try {
        $response = Invoke-WebRequest "http://127.0.0.1:$port/api/health" -UseBasicParsing -TimeoutSec 2
        if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) { return $false }
        $body = $response.Content | ConvertFrom-Json
        $name = "$($body.app) $($body.service) $($body.name)"
        if (($body.status -notin @('ok','healthy','ready'))) { return $false }
        if ($name -match '(?i)astra') { return $true }
        # Older Astra builds expose no app name; state shape is still an app-specific check.
        $stateResponse = Invoke-WebRequest "http://127.0.0.1:$port/api/state" -UseBasicParsing -TimeoutSec 2
        $state = $stateResponse.Content | ConvertFrom-Json
        return ($null -ne $state.watchlist -and $null -ne $state.connection -and $null -ne $state.market)
    } catch { return $false }
}

Require-Command 'dotnet' '.NET 9 SDK를 설치하세요: https://dotnet.microsoft.com/download/dotnet/9.0'
Require-Command 'npm' 'Node.js LTS를 설치하세요: https://nodejs.org/'
if (-not (Test-Path (Join-Path $clientDir 'package.json'))) { Fail 'client/package.json을 찾지 못했습니다.' }
$project = Find-Project
New-Item -ItemType Directory -Force -Path $runtime | Out-Null

# An already healthy Astra instance is reused. This avoids duplicate pollers.
try {
    if (Test-AstraHealth) {
        if (-not $NoBrowser) { Start-Process "http://127.0.0.1:$port" }
        Write-Host "Astra가 이미 실행 중입니다: http://127.0.0.1:$port"
        exit 0
    }
} catch { }

Push-Location $clientDir
try {
    if (-not (Test-Path (Join-Path $clientDir 'node_modules'))) { npm install --no-audit --no-fund; if ($LASTEXITCODE) { Fail 'npm install이 실패했습니다.' } }
    npm run build; if ($LASTEXITCODE) { Fail 'React 빌드가 실패했습니다.' }
} finally { Pop-Location }

dotnet build $project.FullName --configuration Release
if ($LASTEXITCODE) { Fail '.NET 빌드가 실패했습니다.' }
$dll = Join-Path $project.Directory.FullName ("bin\Release\net9.0\" + [IO.Path]::GetFileNameWithoutExtension($project.Name) + '.dll')
if (-not (Test-Path $dll)) { Fail "빌드 결과 DLL을 찾지 못했습니다: $dll" }

$absoluteProject = [IO.Path]::GetFullPath($project.FullName)
$env:ASPNETCORE_URLS = "http://127.0.0.1:$port"
$env:ASTRA_CLIENT_ROOT = $clientDir
$desktop = [Environment]::GetFolderPath('Desktop')
$credentialFile = Join-Path $desktop 'tossapi.txt'
$env:TOSS_CREDENTIALS_PATH = $credentialFile
$absoluteDll = [IO.Path]::GetFullPath($dll)
$dotnetExe = (Get-Command dotnet).Source
$proc = Start-Process -FilePath $dotnetExe -ArgumentList @("`"$absoluteDll`"") -WorkingDirectory $Root -WindowStyle Hidden -RedirectStandardOutput $logFile -RedirectStandardError $errorLogFile -PassThru
$record = [ordered]@{ pid = $proc.Id; project = $absoluteProject; app = $absoluteDll; executable = $dotnetExe; url = "http://127.0.0.1:$port"; startedAt = (Get-Date).ToUniversalTime().ToString('o'); processStartedAt = $proc.StartTime.ToUniversalTime().ToString('o') }
$record | ConvertTo-Json | Set-Content -LiteralPath $pidFile -Encoding UTF8

$ready = $false
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    if (Test-AstraHealth) { $ready = $true; break }
    if ($proc.HasExited) { break }
}
if (-not $ready) {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Fail "서버가 준비되지 않았습니다. 로그를 확인하세요: $logFile"
}
if (-not $NoBrowser) { Start-Process "http://127.0.0.1:$port" }
Write-Host "Astra 준비 완료: http://127.0.0.1:$port"
