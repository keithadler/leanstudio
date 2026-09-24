# Authenticode-sign Lean Studio's own binaries in a published Windows build, between
# `publish.sh <rid> build` and `publish.sh <rid> archive`. Only our files are signed: the .NET runtime's are
# already signed by Microsoft, and other libraries' signatures are left as their authors made them.
#   pwsh packaging/windows/sign.ps1 -Rid win-x64              signs with a code-signing certificate (.pfx)
#   pwsh packaging/windows/sign.ps1 -Rid win-x64 -ListOnly    prints the files to sign (for Azure Artifact Signing)
# The certificate comes from the environment (see docs/packaging/signing.md):
#   WINDOWS_CERTIFICATE           the .pfx, base64-encoded
#   WINDOWS_CERTIFICATE_PASSWORD  its password
#   WINDOWS_TIMESTAMP_URL         optional: an RFC 3161 timestamp server (default: DigiCert's)
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$Rid,
    [switch]$ListOnly
)
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$folder = Join-Path $root "artifacts/$Rid/LeanStudio"
$names = 'LeanStudio.exe', 'LeanStudio.dll', 'LeanStudio.Core.dll', 'LeanStudio.Lsp.dll', 'LeanStudio.Mcp.dll',
         'Tenet.Kernel.dll', 'Tenet.Olean.dll'
$files = foreach ($n in $names) {
    $f = Join-Path $folder $n
    if (-not (Test-Path $f)) { throw "missing ${f}: run packaging/publish.sh $Rid build first" }
    $f
}
if ($ListOnly) { $files; return }

if (-not $env:WINDOWS_CERTIFICATE -or -not $env:WINDOWS_CERTIFICATE_PASSWORD) {
    throw 'set WINDOWS_CERTIFICATE (a base64 .pfx) and WINDOWS_CERTIFICATE_PASSWORD'
}
$timestamp = if ($env:WINDOWS_TIMESTAMP_URL) { $env:WINDOWS_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }

# signtool comes with the Windows SDK; take the newest installed.
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" |
    Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1
if (-not $signtool) { throw 'signtool.exe not found: install the Windows SDK' }

$pfx = Join-Path ([IO.Path]::GetTempPath()) "leanstudio-$([guid]::NewGuid()).pfx"
try {
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:WINDOWS_CERTIFICATE))
    & $signtool.FullName sign /f $pfx /p $env:WINDOWS_CERTIFICATE_PASSWORD /fd SHA256 /tr $timestamp /td SHA256 `
        /d 'Lean Studio' /du 'https://github.com/keithadler/leanstudio' @files
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed ($LASTEXITCODE)" }
}
finally {
    Remove-Item $pfx -Force -ErrorAction SilentlyContinue
}
& $signtool.FullName verify /pa /q @files
if ($LASTEXITCODE -ne 0) { throw "signtool verify failed ($LASTEXITCODE)" }
Write-Host "signed $($files.Count) files in $folder"
