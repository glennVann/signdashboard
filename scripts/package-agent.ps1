param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$agentProject = Join-Path $root "SignDashboard.Agent\SignDashboard.Agent.csproj"
$publishRoot = Join-Path $root "SignDashboard.Agent\bin\$Configuration\net9.0\$Runtime\publish"
$downloadsRoot = Join-Path $root "SignDashboard.Api\AgentDownloads"
$zipPath = Join-Path $downloadsRoot "SignDashboard.Agent.$Runtime.zip"
$readmePath = Join-Path $publishRoot "README-Agent.txt"

dotnet publish $agentProject -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true

New-Item -ItemType Directory -Force -Path $downloadsRoot | Out-Null

@"
Sign Dashboard Local Agent

1. Extract this ZIP on the Windows computer or server that can access the shop drive, NAS, or fileserver.
2. Copy .env.example to .env.
3. Set SIGN_DASHBOARD_API_BASE_URL, SIGN_DASHBOARD_AGENT_TOKEN, and SIGN_DASHBOARD_CUSTOMER_FILES_ROOT.
4. Run SignDashboard.Agent.exe.

For production, install the executable as a Windows service so it starts automatically.
"@ | Set-Content -Path $readmePath -Encoding UTF8

Copy-Item -Path (Join-Path $root "SignDashboard.Agent\.env.example") -Destination (Join-Path $publishRoot ".env.example") -Force

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $zipPath -Force
Write-Host "Created $zipPath"
