param([string]$Destination = (Join-Path $PSScriptRoot '../src/Aria2Fast.Web/bin/Debug/net10.0/aria2'))
$ErrorActionPreference = 'Stop'
$version = '1.37.0'
$url = "https://github.com/aria2/aria2/releases/download/release-$version/aria2-$version-win-64bit-build1.zip"
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('aria2fast-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $temporary, $Destination | Out-Null
try {
    $zip = Join-Path $temporary 'aria2.zip'
    Invoke-WebRequest -Uri $url -OutFile $zip
    # Pin the upstream release. Validate GitHub's digest when provided for this asset.
    $release = Invoke-RestMethod "https://api.github.com/repos/aria2/aria2/releases/tags/release-$version"
    $asset = $release.assets | Where-Object name -eq "aria2-$version-win-64bit-build1.zip"
    if (!$asset) { throw 'Official release asset missing' }
    $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($asset.digest -and $asset.digest -ne "sha256:$actual") { throw 'aria2 upstream SHA256 mismatch' }
    Set-Content -LiteralPath (Join-Path $Destination 'archive-sha256.txt') -Value "$actual  $url"
    Expand-Archive -LiteralPath $zip -DestinationPath $temporary
    $binary = Get-ChildItem -LiteralPath $temporary -Filter aria2c.exe -Recurse | Select-Object -First 1
    if (!$binary) { throw 'aria2c.exe missing from archive' }
    Copy-Item -Path (Join-Path $binary.Directory.FullName '*') -Destination $Destination -Recurse -Force
    & (Join-Path $Destination 'aria2c.exe') --version
    if ($LASTEXITCODE -ne 0) { throw 'aria2 smoke test failed' }
} finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf).StartsWith('aria2fast-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
