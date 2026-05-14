$ErrorActionPreference = "Stop"
$out = "$PSScriptRoot\publish"

Write-Host "==> Server (self-contained, win-x64)"
dotnet publish "$PSScriptRoot\Server\Server.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:StripSymbols=true `
    -o "$out\Server"
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "==> Client (self-contained, win-x64)"
dotnet publish "$PSScriptRoot\Client\Client.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    -o "$out\Client"
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "==> Copying world.db"
$srcDb = "$PSScriptRoot\world.db"
$dstDb = "$out\Server\world.db"
Copy-Item $srcDb $dstDb -Force

Write-Host "==> Compiling scripts"
$scriptsOut = "$out\Server\compiled-scripts"
dotnet run --project "$PSScriptRoot\ScriptCompiler\ScriptCompiler.csproj" `
    -c Release -- $dstDb $scriptsOut
if ($LASTEXITCODE -ne 0) { exit 1 }

$particleDll = "$scriptsOut\scripts\particle.dll"
if (-not (Test-Path $particleDll)) {
    Write-Error "Script compilation produced no output: $particleDll missing"
    exit 1
}

Write-Host ""
Write-Host "Done. Output: $out"
