#requires -Version 7
<#
.SYNOPSIS
    A tiny stand-in for CardService used to demonstrate GameService's resilience by hand.

.DESCRIPTION
    Listens on GET /cards/random and, after waiting -DelayMs, replies with -StatusCode and a
    card JSON in the contract's shape. No dependencies (System.Net.HttpListener). Point
    GameService at it with:  $env:CardService__BaseUrl = "http://localhost:<Port>"

    Examples:
      # slow success -> GameService times out each attempt and returns 504
      ./scripts/slow-card-service.ps1
      # fast 503 -> GameService retries and, after enough calls, the circuit breaker opens
      ./scripts/slow-card-service.ps1 -DelayMs 0 -StatusCode 503

    Stop with Ctrl+C.

.PARAMETER Port
    Port to listen on (default 5055).
.PARAMETER DelayMs
    Delay before responding, in milliseconds (default 2000).
.PARAMETER StatusCode
    HTTP status code to return (default 200).
#>
param(
    [int]$Port = 5055,
    [int]$DelayMs = 2000,
    [int]$StatusCode = 200
)

$ErrorActionPreference = 'Stop'
$card = '{"suit":"Hearts","rank":"Queen","isJoker":false}'

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "slow-card-service listening on http://localhost:$Port/ (DelayMs=$DelayMs, StatusCode=$StatusCode). Ctrl+C to stop."

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        try {
            $request = $context.Request
            $response = $context.Response
            if ($request.HttpMethod -eq 'GET' -and $request.Url.AbsolutePath -eq '/cards/random') {
                if ($DelayMs -gt 0) { Start-Sleep -Milliseconds $DelayMs }
                $response.StatusCode = $StatusCode
                $response.ContentType = 'application/json'
                $bytes = [System.Text.Encoding]::UTF8.GetBytes($card)
                $response.OutputStream.Write($bytes, 0, $bytes.Length)
            }
            else {
                $response.StatusCode = 404
            }
        }
        catch {
            # The client (GameService) may abort mid-delay when its attempt timeout fires; writing
            # to the dropped connection throws. Ignore it and keep serving the next request.
        }
        finally {
            try { $context.Response.OutputStream.Close() } catch { }
        }
    }
}
finally {
    $listener.Stop()
    $listener.Close()
}
