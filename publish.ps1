$ErrorActionPreference = "Stop"
$out   = "$PSScriptRoot\publish"
$srcDb = "$PSScriptRoot\world.db"

function Fail($msg) {
    Write-Host ""
    Write-Host "FEHLER: $msg" -ForegroundColor Red
    Read-Host "Enter zum Schliessen"
    exit 1
}

function CompileScripts($db, $scriptsOut) {
    dotnet run --project "$PSScriptRoot\ScriptCompiler\ScriptCompiler.csproj" `
        -c Release -- $db $scriptsOut
    if ($LASTEXITCODE -ne 0) { Fail "ScriptCompiler fehlgeschlagen fuer '$db' (exit $LASTEXITCODE)" }
    $particleDll = "$scriptsOut\scripts\particle.dll"
    if (-not (Test-Path $particleDll)) { Fail "Script-Output fehlt: $particleDll" }
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

Write-Host "==> Seede root world.db (frisch)"
Remove-Item $srcDb -ErrorAction SilentlyContinue
dotnet run --project "$PSScriptRoot\Server\Server.csproj" -- --seed
if ($LASTEXITCODE -ne 0) { Fail "Server --seed fehlgeschlagen (exit $LASTEXITCODE)" }
if (-not (Test-Path $srcDb)) { Fail "world.db wurde nicht erstellt: $srcDb" }

Write-Host "==> Kompiliere Scripts (dev)"
CompileScripts $srcDb "$PSScriptRoot\compiled-scripts"

Write-Host "==> Kopiere world.db und kompiliere Scripts (publish)"
$dstDb = "$out\Server\world.db"
Copy-Item $srcDb $dstDb -Force
CompileScripts $dstDb "$out\Server\compiled-scripts"

Write-Host ""
Write-Host "Fertig. Output: $out" -ForegroundColor Green
Read-Host "Enter zum Schliessen"
