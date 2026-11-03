param([string]$GatewayUrl = 'http://127.0.0.1:18080')
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:AI_GATEWAY_ADMIN_KEY)) {
    throw 'Set AI_GATEWAY_ADMIN_KEY in this process from the secret store first.'
}
$gatewayAddress = [uri]$GatewayUrl
if ($gatewayAddress.Scheme -ne 'https' -and -not $gatewayAddress.IsLoopback) {
    throw 'Use HTTPS for remote administration.'
}
Invoke-RestMethod -Uri ($GatewayUrl.TrimEnd('/') + '/admin/control') -Method Post `
    -Headers @{ Authorization = 'Bearer ' + $env:AI_GATEWAY_ADMIN_KEY } `
    -ContentType 'application/json' -Body '{"stopped":true}' | Out-Null
Write-Output 'AI spending stopped. Previously admitted calls retain their reservations.'
