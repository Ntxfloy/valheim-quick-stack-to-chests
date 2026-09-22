# Сборка мода. Пример:
#   .\build.ps1
#   .\build.ps1 -ValheimPath "D:\SteamLibrary\steamapps\common\Valheim" -Deploy
param(
    [string]$ValheimPath = "",
    [switch]$Deploy
)

$buildArgs = @("build", "-c", "Release", "$PSScriptRoot\src\QuickStackToChests\QuickStackToChests.csproj")
if ($ValheimPath -ne "") { $buildArgs += "-p:ValheimPath=$ValheimPath" }
if ($Deploy) { $buildArgs += "-p:DeployToGame=true" }

& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Готово: src\QuickStackToChests\bin\Release\QuickStackToChests.dll" -ForegroundColor Green
