# ساخت و تست روی ویندوز — Build + test + publish on Windows
$ErrorActionPreference = "Stop"

dotnet build FlashRepairAgent.Windows.sln -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

& ".\src\FlashRepair.Tests\bin\Release\net8.0\flashrepair-tests.exe"
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

dotnet publish src\FlashRepair.Cli -c Release -r win-x64 --self-contained false -o publish\win-x64
dotnet publish src\FlashRepair.App -c Release -r win-x64 --self-contained false -o publish\app-win-x64

Write-Host "OK — outputs in publish\"
