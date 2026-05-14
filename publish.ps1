$ErrorActionPreference = "Stop"
$out = "$PSScriptRoot\publish"

Write-Host "==> Server (self-contained, win-x64)"
dotnet publish "$PSScriptRoot\Server\Server.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:StripSymbols=true `
    -o "$out\Server"
if (-not $?) { exit 1 }

Write-Host "==> Client (self-contained, win-x64)"
dotnet publish "$PSScriptRoot\Client\Client.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    -o "$out\Client"
if (-not $?) { exit 1 }

Write-Host "==> ScriptCompiler (framework-dependent)"
dotnet publish "$PSScriptRoot\ScriptCompiler\ScriptCompiler.csproj" `
    -c Release `
    -o "$out\ScriptCompiler"
if (-not $?) { exit 1 }

Write-Host "==> Copying world.db"
$srcDb = "$PSScriptRoot\world.db"
$dstDb = "$out\Server\world.db"
if (Test-Path $srcDb) {
    Copy-Item $srcDb $dstDb -Force
} else {
    Write-Warning "world.db not found at $srcDb — skipping copy"
}

if (Test-Path $dstDb) {
    Write-Host "==> Compiling scripts"
    & "$out\ScriptCompiler\ScriptCompiler.exe" $dstDb "$out\Server\compiled-scripts"
    if (-not $?) { exit 1 }
} else {
    Write-Warning "No world.db in server output — skipping script compilation"
}

Write-Host ""
Write-Host "Done. Output: $out"
