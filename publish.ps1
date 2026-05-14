$ErrorActionPreference = "Stop"
$out = "$PSScriptRoot\publish"

function Fail($msg) {
    Write-Host ""
    Write-Host "FEHLER: $msg" -ForegroundColor Red
    Read-Host "Enter zum Schliessen"
    exit 1
}

Write-Host "==> Server (self-contained, win-x64)"
dotnet publish "$PSScriptRoot\Server\Server.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:StripSymbols=true `
    -o "$out\Server"
if ($LASTEXITCODE -ne 0) { Fail "Server publish fehlgeschlagen (exit $LASTEXITCODE)" }

Write-Host "==> Client (self-contained, win-x64)"
dotnet publish "$PSScriptRoot\Client\Client.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    -o "$out\Client"
if ($LASTEXITCODE -ne 0) { Fail "Client publish fehlgeschlagen (exit $LASTEXITCODE)" }

Write-Host "==> Seede root world.db"
dotnet run --project "$PSScriptRoot\Server\Server.csproj" -- --seed
if ($LASTEXITCODE -ne 0) { Fail "Server --seed fehlgeschlagen (exit $LASTEXITCODE)" }

Write-Host "==> Kopiere world.db"
$srcDb = "$PSScriptRoot\world.db"
$dstDb = "$out\Server\world.db"
if (-not (Test-Path $srcDb)) { Fail "world.db nicht gefunden: $srcDb" }
Copy-Item $srcDb $dstDb -Force

Write-Host "==> Kompiliere Scripts"
$scriptsOut = "$out\Server\compiled-scripts"
dotnet run --project "$PSScriptRoot\ScriptCompiler\ScriptCompiler.csproj" `
    -c Release -- $dstDb $scriptsOut
if ($LASTEXITCODE -ne 0) { Fail "ScriptCompiler fehlgeschlagen (exit $LASTEXITCODE)" }

$particleDll = "$scriptsOut\scripts\particle.dll"
if (-not (Test-Path $particleDll)) { Fail "Script-Output fehlt: $particleDll" }

Write-Host ""
Write-Host "Fertig. Output: $out" -ForegroundColor Green
Read-Host "Enter zum Schliessen"
