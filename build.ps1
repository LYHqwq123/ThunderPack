[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspaceRoot = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $workspaceRoot '.build/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $workspaceRoot '.build/nuget-packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:MSBuildEnableWorkloadResolver = 'false'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '请先安装 Windows 版 .NET 10 SDK，然后重新运行 build.ps1。'
}

Push-Location -LiteralPath $workspaceRoot
try {
    if (-not $SkipTests) {
        dotnet run --project (Join-Path $workspaceRoot 'tests/ThunderPack.Smoke/ThunderPack.Smoke.csproj') --configuration Release -- $workspaceRoot
        if ($LASTEXITCODE -ne 0) { throw '核心功能验证失败，已停止生成发布文件。' }
    }
    dotnet publish (Join-Path $workspaceRoot 'src/ThunderPack.App/ThunderPack.App.csproj') --configuration Release --self-contained false --output (Join-Path $workspaceRoot 'dist/ThunderPack')
    if ($LASTEXITCODE -ne 0) { throw 'Windows 桌面程序编译失败。' }
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'README.md') -Destination (Join-Path $workspaceRoot 'dist/ThunderPack/README.md') -Force
    $previewImage = Join-Path $workspaceRoot 'docs/screenshot.png'
    if (Test-Path -LiteralPath $previewImage) {
        New-Item -ItemType Directory -Force -Path (Join-Path $workspaceRoot 'dist/ThunderPack/docs') | Out-Null
        Copy-Item -LiteralPath $previewImage -Destination (Join-Path $workspaceRoot 'dist/ThunderPack/docs/screenshot.png') -Force
    }
    Write-Host ''
    Write-Host ('已生成：' + (Join-Path $workspaceRoot 'dist/ThunderPack/ThunderPack.exe')) -ForegroundColor Green
    Write-Host '发布时请保留 ThunderPack 文件夹内全部文件。运行需要 .NET 10 Windows Desktop Runtime。'
}
finally {
    Pop-Location
}
