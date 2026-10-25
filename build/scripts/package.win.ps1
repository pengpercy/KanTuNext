# Zips a Windows publish folder. env: VERSION, RUNTIME (win-x64|win-arm64|win7-x64); input: build\KanTuNext\
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
Remove-Item -Path KanTuNext\*.pdb -Force -ErrorAction SilentlyContinue
Compress-Archive -Path KanTuNext -DestinationPath "KanTuNext_$($env:VERSION).$($env:RUNTIME).zip" -Force
Get-Item "KanTuNext_*.zip" | Format-Table Name, Length
