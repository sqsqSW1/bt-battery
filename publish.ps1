[CmdletBinding(DefaultParameterSetName = "Default")]
param(
    [Parameter(ParameterSetName = "FrameworkDependent")]
    [switch]$FrameworkDependent,
    [Parameter(ParameterSetName = "Compact")]
    [switch]$Compact
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if ($Compact) {
    $projectDir = Join-Path $root "src\BtBattery.Compact"
    dotnet build (Join-Path $projectDir "BtBattery.Compact.csproj") -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $binary = Get-Item -LiteralPath (Join-Path $projectDir "bin\Release\net48\蓝牙电量.exe")
    if ($binary.Length -ge 1000000) { throw "轻量版超过了 1 MB 的发布上限。" }

    $outDir = Join-Path $root "artifacts\compact-win-x64"
    [void][System.IO.Directory]::CreateDirectory($outDir)
    $destination = Join-Path $outDir $binary.Name
    Copy-Item -LiteralPath $binary.FullName -Destination $destination -Force
    Write-Host "轻量版已发布到 $destination（$($binary.Length) 字节）"
    return
}

$project = Join-Path $root "src\BtBattery.App\BtBattery.App.csproj"

$publishArgs = @(
    "publish", $project,
    "-c", "Release",
    "-r", "win-x64"
)

if ($FrameworkDependent) {
    $publishArgs += "--self-contained", "false"
} else {
    $publishArgs += @(
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true"
    )
}

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "发布完成。可执行文件在 src\BtBattery.App\bin\Release\ 下面的 win-x64\publish\蓝牙电量.exe"
