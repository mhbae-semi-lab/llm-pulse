<#
.SYNOPSIS
  배포 패키지를 만든다.
  - 기본: 자체 포함(.NET 런타임 내장) 빌드 -> Velopack 설치 파일(Setup.exe, 자동 업데이트) + 휴대용 zip
  - -Lite: 프레임워크 의존(.NET 8 Desktop Runtime 필요) 단일 exe zip (Velopack 없음)
.EXAMPLE
  pwsh scripts/publish.ps1                     # publish/velopack/LLMPulse.App-win-Setup.exe 등 + publish/LLMPulse-<ver>-win-x64.zip
  pwsh scripts/publish.ps1 -Version 1.0.1      # 버전 덮어쓰기 (로컬 업데이트 시험용)
  pwsh scripts/publish.ps1 -Lite               # publish/LLMPulse-<ver>-win-x64-lite.zip
  사전 준비(Velopack CLI): dotnet tool install -g vpk --version 1.2.161
#>
param([string]$Runtime = 'win-x64', [switch]$Lite, [string]$Version = '', [string]$OutDir = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'src/LLMPulse.App/LLMPulse.App.csproj'
$suffix = if ($Lite) { '-lite' } else { '' }
$out  = Join-Path $root "publish/$Runtime$suffix"

if (-not $Version) {
    $xml = [xml]::new(); $xml.Load($proj)
    $Version = ($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
}
$verArgs = @("-p:Version=$Version", "-p:AssemblyVersion=$Version.0", "-p:FileVersion=$Version.0")

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if ($Lite) {
    dotnet publish $proj -c Release -r $Runtime --self-contained false -p:PublishSingleFile=true @verArgs -o $out
} else {
    dotnet publish $proj -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true @verArgs -o $out
}
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

# 휴대용 zip (설치 없이 풀어서 실행, install.ps1 로 시작 메뉴 등록 가능)
$zip = Join-Path $root "publish/LLMPulse-$Version-$Runtime$suffix.zip"
Copy-Item (Join-Path $PSScriptRoot 'install.ps1') $out -Force
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -Force
Remove-Item (Join-Path $out 'install.ps1')
Write-Host ("Created $zip ({0} MB)" -f [math]::Round((Get-Item $zip).Length / 1MB, 1))

if (-not $Lite) {
    # Velopack: 설치 파일 + 업데이트 패키지. packId 는 데이터 폴더(%LOCALAPPDATA%\LLMPulse)와 겹치지 않게
    # LLMPulse.App 으로 둔다 (설치 폴더 = %LOCALAPPDATA%\LLMPulse.App, 제거해도 데이터는 남음).
    if (-not $OutDir) { $OutDir = Join-Path $root 'publish/velopack' }
    vpk pack --packId LLMPulse.App --packVersion $Version --packDir $out --mainExe LLMPulse.App.exe `
        --packTitle 'LLM Pulse' --packAuthors 'mhbae-semi-lab' --icon (Join-Path $root 'src/LLMPulse.App/Assets/app.ico') `
        --outputDir $OutDir
    if ($LASTEXITCODE) { throw 'vpk pack failed' }
    Write-Host "Velopack release in $OutDir"
}
