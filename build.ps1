param(
    [string]$ValheimPath = "",
    [switch]$Deploy
)

$dotnetCmd = "dotnet"
$localDotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
if (Test-Path $localDotnet) {
    $dotnetCmd = $localDotnet
}

$buildArgs = @("build", "-c", "Release", "$PSScriptRoot\src\QuickStackToChests\QuickStackToChests.csproj")
if ($ValheimPath -ne "") { $buildArgs += "-p:ValheimPath=$ValheimPath" }
if ($Deploy) { $buildArgs += "-p:DeployToGame=true" }

& $dotnetCmd @buildArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Success: src\QuickStackToChests\bin\Release\QuickStackToChests.dll" -ForegroundColor Green
