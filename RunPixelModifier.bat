@echo off
setlocal
cd /d "%~dp0"
dotnet run --project src\PixelModifier.App\PixelModifier.App.csproj -c Release -- %*
if errorlevel 1 pause
