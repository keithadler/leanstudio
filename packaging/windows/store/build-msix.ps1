<#
  Builds the Microsoft Store package for Lean Studio. Run on Windows from the repository:

      pwsh packaging/windows/store/build-msix.ps1
      pwsh packaging/windows/store/build-msix.ps1 -TestSign     # also sign it so it installs on this PC

  Needs the .NET 10 SDK, the tenet submodule and the Windows SDK (makeappx, makepri, signtool).
  Writes to artifacts/msix:
      LeanStudio_X.Y.Z.0_x64.msix, LeanStudio_X.Y.Z.0_arm64.msix
      LeanStudio_X.Y.Z.0.msixbundle      the file to upload to Partner Center

  The version is VersionPrefix from Directory.Build.props, with a 0 revision as the Store requires.
  The Store signs the bundle itself, so an upload does not need -TestSign. -TestSign makes a
  certificate on this PC whose subject is the publisher in identity.json, signs the packages with it
  and saves its public half next to them: install that into "Trusted People" (Local Machine) on a test
  PC, then double-click the bundle.
#>
param(
  [switch]$TestSign,
  [string[]]$Architectures = @('x64', 'arm64')
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here '..\..\..')
$out = Join-Path $root 'artifacts\msix'
$identity = Get-Content (Join-Path $here 'identity.json') -Raw | ConvertFrom-Json
$props = [xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
$prefix = ($props.Project.PropertyGroup | ForEach-Object { $_.VersionPrefix } | Where-Object { $_ } | Select-Object -First 1)
if ($prefix -notmatch '^\d+\.\d+\.\d+$') { throw "VersionPrefix '$prefix' is not X.Y.Z" }
$version = "$prefix.0"

function Find-SdkTool([string]$name) {
  $onPath = Get-Command $name -ErrorAction SilentlyContinue
  if ($onPath) { return $onPath.Source }
  $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
  $hostArch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
  $found = Get-ChildItem $kits -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^10\.' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName "$hostArch\$name" } | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $found) { throw "$name was not found. Install the Windows SDK." }
  return $found
}
$makeappx = Find-SdkTool 'makeappx.exe'
$makepri = Find-SdkTool 'makepri.exe'

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
$packages = Join-Path $out 'packages'
New-Item -ItemType Directory -Force $packages | Out-Null

foreach ($arch in $Architectures) {
  $layout = Join-Path $out "layout-$arch"
  Write-Host "Publishing win-$arch..."
  dotnet publish (Join-Path $root 'src\LeanStudio.App\LeanStudio.App.csproj') -c Release -r "win-$arch" --self-contained `
    -p:PublishSingleFile=false -p:DebugType=none -o $layout --nologo -v q
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $arch" }
  Get-ChildItem $layout -Filter *.pdb -Recurse | Remove-Item -Force
  Copy-Item (Join-Path $here 'Assets') (Join-Path $layout 'Assets') -Recurse
  $manifest = (Get-Content (Join-Path $here 'AppxManifest.template.xml') -Raw).
    Replace('{{IdentityName}}', $identity.IdentityName).Replace('{{Publisher}}', $identity.Publisher).
    Replace('{{PublisherDisplayName}}', $identity.PublisherDisplayName).Replace('{{Version}}', $version).Replace('{{Architecture}}', $arch)
  Set-Content (Join-Path $layout 'AppxManifest.xml') $manifest -Encoding utf8
  # The index that lets Windows pick the right size of each logo (scale-200, targetsize-32...).
  $priConfig = Join-Path $out "priconfig-$arch.xml"
  & $makepri createconfig /cf $priConfig /dq en-US /o | Out-Null
  & $makepri new /pr $layout /cf $priConfig /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "makepri failed for $arch" }
  Remove-Item $priConfig -Force
  & $makeappx pack /o /d $layout /p (Join-Path $packages "LeanStudio_${version}_$arch.msix") | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed for $arch" }
  Remove-Item $layout -Recurse -Force
}

$bundle = Join-Path $out "LeanStudio_$version.msixbundle"
& $makeappx bundle /o /bv $version /d $packages /p $bundle | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makeappx bundle failed' }
Get-ChildItem $packages | Move-Item -Destination $out
Remove-Item $packages -Recurse -Force

if ($TestSign) {
  $signtool = Find-SdkTool 'signtool.exe'
  $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $identity.Publisher -and $_.HasPrivateKey } | Select-Object -First 1
  if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $identity.Publisher -KeyUsage DigitalSignature -FriendlyName 'Lean Studio test signing' `
      -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
  }
  Export-Certificate -Cert $cert -FilePath (Join-Path $out 'LeanStudio-test.cer') | Out-Null
  Get-ChildItem $out -Include *.msix, *.msixbundle -Recurse | ForEach-Object {
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $_.FullName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $($_.Name)" }
  }
  Write-Host 'Signed for testing. Install LeanStudio-test.cer into "Trusted People" (Local Machine) on the test PC first.'
}

Get-ChildItem $out | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
