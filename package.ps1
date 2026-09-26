param(
    [string]$ValheimPath = "",
    [string]$OutputDirectory = "$PSScriptRoot\dist"
)

$ErrorActionPreference = "Stop"

$dotnetCmd = "dotnet"
$localDotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
if (Test-Path $localDotnet) {
    $dotnetCmd = $localDotnet
}

$project = Join-Path $PSScriptRoot "src\QuickStackToChests\QuickStackToChests.csproj"
$buildArgs = @("build", $project, "-c", "Release", "-p:DeployToGame=false")
if ($ValheimPath) { $buildArgs += "-p:ValheimPath=$ValheimPath" }
& $dotnetCmd @buildArgs

$staging = Join-Path $OutputDirectory "thunderstore_package"
$zipPath = Join-Path $OutputDirectory "Ntxfloy-QuickStackToChests-1.2.4.zip"
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$staging\BepInEx\plugins" | Out-Null
Copy-Item "$PSScriptRoot\manifest.json", "$PSScriptRoot\README.md", "$PSScriptRoot\icon.png" -Destination $staging
Copy-Item "$PSScriptRoot\src\QuickStackToChests\bin\Release\QuickStackToChests.dll" -Destination "$staging\BepInEx\plugins"

if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path "$staging\*" -DestinationPath $zipPath
Write-Host "Thunderstore package ready: $zipPath" -ForegroundColor Green
