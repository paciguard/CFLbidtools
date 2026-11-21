$ErrorActionPreference = 'Stop'
$port = 9443
$backend = 'http://127.0.0.1:5210'

function Read-TailscaleJson {
    $result = & $cli @args
    if ($LASTEXITCODE -ne 0) { throw 'Tailscale did not respond. Open Tailscale on this PC and try again.' }
    $result | Out-String | ConvertFrom-Json
}

function Get-PortState($config) {
    $suffix = ":${port}$"
    $listeners = @($config.Web.PSObject.Properties | Where-Object { $_.Name -match $suffix })
    $publicRoutes = @($config.AllowFunnel.PSObject.Properties | Where-Object { $_.Name -match $suffix -and $_.Value })
    $tcp = $config.TCP."$port"
    if ($publicRoutes.Count -gt 0) { return 'occupied' }
    if ($listeners.Count -eq 0 -and !$tcp) { return 'available' }
    if ($listeners.Count -ne 1 -or $listeners[0].Name -ne $hostPort -or !$tcp.HTTPS) { return 'occupied' }
    $handlers = @($listeners[0].Value.Handlers.PSObject.Properties | Where-Object Name)
    if ($handlers.Count -eq 1 -and $handlers[0].Name -eq '/' -and
        ([string]$handlers[0].Value.Proxy).TrimEnd('/') -eq $backend) { return 'workbench' }
    'occupied'
}

function Copy-PhoneLink {
    Write-Host "`nPhone address: $phoneUrl"
    try {
        Set-Clipboard -Value $phoneUrl
        Write-Host 'Copied. Send this link to your phone and open it there.'
    }
    catch { Write-Host 'Copy this link and send it to your phone.' }
    Write-Host 'The PC workbench must be running, and the phone must be connected to the same private Tailscale network.'
}

try {
    Write-Host "CFL Workbench | Phone Connection`n"
    $cli = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) |
        Where-Object { $_ } | ForEach-Object { Join-Path $_ 'Tailscale\tailscale.exe' } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (!$cli) {
        throw 'Tailscale is not installed on this PC. Install it from https://tailscale.com/download/windows, then sign in on the PC and phone.'
    }
    $device = Read-TailscaleJson status --json
    $dns = ([string]$device.Self.DNSName).TrimEnd('.')
    if ($device.BackendState -ne 'Running' -or $dns -notmatch '^[a-z0-9][a-z0-9.-]*\.ts\.net$') {
        throw 'Connect this PC to Tailscale and make sure MagicDNS is enabled for your private network.'
    }
    $hostPort = "${dns}:${port}"
    $phoneUrl = "https://$hostPort/"
    switch (Get-PortState (Read-TailscaleJson serve status --json)) {
        'occupied' { throw "Tailscale port $port is already configured for another service or public access. No settings were changed." }
        'available' {
            Write-Host 'Phone access is off. Only users allowed by your Tailscale access rules can connect once enabled.'
            if ((Read-Host 'Type 1 to enable private phone access, or press Enter to leave it off') -eq '1') {
                try {
                    $local = Invoke-WebRequest -Uri "$backend/api/connection" -UseBasicParsing -TimeoutSec 3
                    if ($local.StatusCode -ne 204 -or $local.Headers['X-CFL-Installation'] -ne 'windows') { throw 'Not installed workbench' }
                }
                catch { throw 'Start the installed CFL Engineering Workbench on this PC, then try again.' }
                & $cli serve --bg "--https=$port" $backend
                if ($LASTEXITCODE -ne 0 -or (Get-PortState (Read-TailscaleJson serve status --json)) -ne 'workbench') {
                    throw 'Private phone access was not confirmed. Follow any Tailscale HTTPS setup prompt and try again.'
                }
                Copy-PhoneLink
            }
        }
        'workbench' {
            Write-Host "Phone access is on: $phoneUrl"
            switch (Read-Host 'Type 1 to copy the link, 2 to turn off phone access, or press Enter to close') {
                '1' { Copy-PhoneLink }
                '2' {
                    & $cli serve --bg "--https=$port" $backend off
                    if ($LASTEXITCODE -ne 0 -or (Get-PortState (Read-TailscaleJson serve status --json)) -eq 'workbench') {
                        throw 'Phone access could not be turned off. Check Tailscale on this PC.'
                    }
                    Write-Host 'Phone access is off.'
                }
            }
        }
    }
}
catch { Write-Host $_.Exception.Message -ForegroundColor Yellow }
Read-Host 'Press Enter to close' | Out-Null
