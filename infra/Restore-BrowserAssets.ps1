$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path $PSScriptRoot -Parent
$manifest = Get-Content -LiteralPath (Join-Path $root 'libman.json') -Raw | ConvertFrom-Json
$library = $manifest.libraries[0]
if ($library.library -ne '@azure/msal-browser@4.30.0') {
    throw 'Update the reviewed asset hashes when changing the pinned MSAL version.'
}
$hashes = @{
    'lib/msal-browser.min.js' = 'K1gBZQZKak7AQevUSDIpkXJoiiOkLDuB1lMUeZ26BOs='
    'LICENSE' = 'xUSt+PDUNFPZ/fbBHaGWBdEtiYBqeHBlMWm4zJf1u1M='
}
foreach ($file in $library.files) {
    if (-not $hashes.ContainsKey($file)) { throw "No reviewed SHA256 for $file" }
    $target = Join-Path (Join-Path $root $library.destination) $file
    $temporary = [IO.Path]::GetTempFileName()
    try {
        Invoke-WebRequest -UseBasicParsing `
            -Uri "https://cdn.jsdelivr.net/npm/$($library.library)/$file" -OutFile $temporary
        $expected = [BitConverter]::ToString([Convert]::FromBase64String($hashes[$file])).Replace('-', '')
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $expected) {
            throw "MSAL asset integrity check failed: $file"
        }
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $temporary -Destination $target -Force
        Write-Output "Restored and SHA256-verified $file"
    }
    finally {
        Remove-Item -LiteralPath $temporary -Force
    }
}
