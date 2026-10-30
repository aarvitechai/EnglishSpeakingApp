# Serves the published app at https://interview.neurotara.com from this PC, through a Cloudflare Tunnel.
# Safe to run repeatedly: it starts only what is not already running.
$repo = Split-Path $PSScriptRoot -Parent
$pub = Join-Path $repo "backend\bin\publish"

$listening = Get-NetTCPConnection -LocalPort 5090 -State Listen -ErrorAction SilentlyContinue
if (-not $listening) {
    $env:ASPNETCORE_ENVIRONMENT = "Production"
    # Bound to localhost only: the tunnel is the sole way in, which is what makes forwarded headers trustworthy.
    $env:ASPNETCORE_URLS = "http://localhost:5090"
    $env:ConnectionStrings__Default = "Server=DESKTOP-2ERCDGB\MSSQLSERVER01;Database=InterviewAI;Trusted_Connection=True;TrustServerCertificate=True;"
    Start-Process dotnet -ArgumentList "Api.dll" -WorkingDirectory $pub -WindowStyle Hidden -RedirectStandardOutput "$pub\app.log" -RedirectStandardError "$pub\app.err.log"
}

if (-not (Get-Process cloudflared -ErrorAction SilentlyContinue)) {
    Start-Process cloudflared -ArgumentList "tunnel", "run", "neurotara-interview" -WindowStyle Hidden -RedirectStandardOutput "$pub\tunnel.log" -RedirectStandardError "$pub\tunnel.err.log"
}
