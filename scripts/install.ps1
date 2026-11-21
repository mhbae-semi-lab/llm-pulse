<#
.SYNOPSIS
  LLM Pulse 설치/제거 (관리자 권한 불필요, 현재 사용자 전용).
.DESCRIPTION
  설치: LLMPulse.App.exe 를 %LOCALAPPDATA%\Programs\LLMPulse 로 복사하고 시작 메뉴 바로가기를 만든다.
  제거: -Uninstall. 시작 메뉴 바로가기, 자동 실행(HKCU Run), 예약 작업, 설치 폴더를 지운다.
        내 데이터(%LOCALAPPDATA%\LLMPulse)는 -RemoveData 를 줄 때만 지운다.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install.ps1
  powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
#>
param(
    [switch]$Uninstall,
    [switch]$RemoveData,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\LLMPulse'),
    [string]$StartMenuDir = (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs')
)
$ErrorActionPreference = 'Stop'
$exeName  = 'LLMPulse.App.exe'
$shortcut = Join-Path $StartMenuDir 'LLM Pulse.lnk'
$target   = Join-Path $InstallDir $exeName

function Stop-Running {
    Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exeName)) -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $target } | ForEach-Object {
            Write-Host "실행 중인 LLM Pulse 를 종료합니다 (PID $($_.Id))."
            $_.CloseMainWindow() | Out-Null
            if (-not $_.WaitForExit(5000)) { $_.Kill() }
        }
}

if ($Uninstall) {
    Stop-Running
    Remove-Item $shortcut -Force -ErrorAction SilentlyContinue
    # 자동 실행/예약 작업은 "이 설치본"을 가리킬 때만 지운다 (다른 경로의 exe 설정은 건드리지 않는다)
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if ((Get-ItemProperty $runKey -Name 'LLMPulse' -ErrorAction SilentlyContinue).LLMPulse -like "*$target*") {
        Remove-ItemProperty -Path $runKey -Name 'LLMPulse'
    }
    $ErrorActionPreference = 'Continue' # schtasks 는 작업이 없으면 stderr 로 오류를 낸다 (정상 상황)
    $taskXml = schtasks.exe /Query /TN 'LLMPulse Daily Refresh' /XML 2>&1 | Out-String
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -eq 0 -and $taskXml -like "*$target*") { schtasks.exe /Delete /TN 'LLMPulse Daily Refresh' /F | Out-Null }
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    if ($RemoveData) {
        Remove-Item (Join-Path $env:LOCALAPPDATA 'LLMPulse') -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host '내 데이터까지 삭제했습니다.'
    }
    Write-Host '제거했습니다.'
    return
}

$source = Join-Path $PSScriptRoot $exeName
if (-not (Test-Path $source)) { throw "$exeName 을(를) $PSScriptRoot 에서 찾을 수 없습니다. 압축을 푼 폴더에서 실행하세요." }

Stop-Running
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item $source $target -Force
New-Item -ItemType Directory -Path $StartMenuDir -Force | Out-Null
$sh = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
$sh.TargetPath = $target
$sh.WorkingDirectory = $InstallDir
$sh.Description = 'LLM Pulse'
$sh.Save()
Write-Host "설치했습니다: $target"
Write-Host "시작 메뉴에서 'LLM Pulse' 를 실행하세요. 제거: install.ps1 -Uninstall"