# Run this in PowerShell as Administrator on the Windows host
# Forwards LAN-accessible ports to WSL2 services

$wslIp = (wsl hostname -I).Trim().Split(" ")[0]
$ports = @(8000, 8083)

Write-Host "WSL2 IP: $wslIp"

foreach ($port in $ports) {
    # Remove any existing rule for this port first
    netsh interface portproxy delete v4tov4 listenport=$port listenaddress=0.0.0.0 2>$null

    # Add new forwarding rule
    netsh interface portproxy add v4tov4 listenport=$port listenaddress=0.0.0.0 connectport=$port connectaddress=$wslIp
    Write-Host "Forwarding port $port -> WSL2 $wslIp`:$port"
}

# Add firewall rules (only needed once, skip if already exists)
foreach ($port in $ports) {
    $ruleName = "WSL2 Port $port"
    if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow
        Write-Host "Firewall rule added for port $port"
    } else {
        Write-Host "Firewall rule for port $port already exists"
    }
}

Write-Host ""
Write-Host "Done! Services are now LAN-accessible on this machine's IP."
Write-Host "Run 'ipconfig' to find your Windows LAN IP (look for IPv4 under your network adapter)."
