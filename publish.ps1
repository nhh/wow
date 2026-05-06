$ErrorActionPreference = "Stop"
$out = "$PSScriptRoot\publish"

Write-Host "==> Server (NativeAOT, win-x64)"
dotnet publish "$PSScriptRoot\Server\Server.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishAot=true `
    /p:StripSymbols=true `
    -o "$out\Server"
if (-not $?) { exit 1 }

Write-Host "==> Client (self-contained trimmed, win-x64)"
dotnet publish "$PSScriptRoot\Client\Client.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishTrimmed=true `
    /p:PublishSingleFile=true `
    -o "$out\Client"
if (-not $?) { exit 1 }

Write-Host ""
Write-Host "Done. Output: $out"
