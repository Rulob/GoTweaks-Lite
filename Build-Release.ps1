<#
    Build-Release.ps1 - produce a distributable GoTweaks Lite release (option "C").

    Builds the signed MSIX bundle, then assembles a clean `dist/` folder:

        dist/
          GoTweaks-Setup.exe       (THE end-user download - self-contained, embeds the two
                                     files below, nothing else needed next to it)
          GoTweaks_<version>.msixbundle   (also published loose - required for the app's own
          GoTweaks_<version>.cer          in-app auto-update, which fetches these directly
                                           from GitHub Release assets, not via Setup.exe)

    Upload all three loose files as separate GitHub Release assets - no zip needed. (A zip made
    sense when users had to keep 3 files together themselves; now GoTweaks-Setup.exe is the only
    thing end users touch, and nothing downloads a zip programmatically. Pass -Zip to still
    produce GoTweaks_<version>.zip if you want one for archival.)

    GoTweaks-Setup.exe is compiled from Installer/GoTweaksSetupGUI.ps1 via the `ps2exe` module
    (double-clickable, no console window, auto-elevates via its own manifest, EMBEDS the
    .msixbundle + .cer via -embedFiles so a user only ever downloads this one file for a first
    install). It is the ONLY installer shipped, so the `ps2exe` module is a HARD build
    requirement - one-time setup on a fresh dev box:
        Install-Module ps2exe -Scope CurrentUser -Force
    Upload BOTH GoTweaks-Setup.exe AND the loose .msixbundle to the GitHub Release - the exe is
    for humans, the loose bundle is for the in-app updater (see GoTweaksUpdateService.cs, which
    scans release assets for a *.msixbundle file - it doesn't know Setup.exe exists). The loose
    .cer isn't consumed by anything automated (the updater only ever touches the .msixbundle, the
    cert only needs trusting once at first install) - it's kept only for the documented
    Install-GoTweaks.ps1 source fallback.
    The console `Install GoTweaks.ps1`/`.bat` scripts still live under `Installer/` in the repo
    (useful for local dev-box installs / troubleshooting) but are no longer copied into dist/.

    Usage (from the repo root, in a Developer PowerShell / VS-enabled shell):
        ./Build-Release.ps1
        ./Build-Release.ps1 -Thumbprint <SHA1>     # override signing cert
        ./Build-Release.ps1 -SkipBuild             # just re-assemble dist from existing AppPackages

    Signing: by default the script finds a self-signed cert with subject "CN=dg"
    in Cert:\CurrentUser\My (matches the manifest Publisher). Create one once with:
        New-SelfSignedCertificate -Type Custom -Subject "CN=dg" -KeyUsage DigitalSignature `
            -FriendlyName "GoTweaks dev" -CertStoreLocation "Cert:\CurrentUser\My" `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3","2.5.29.19={text}")
#>

[CmdletBinding()]
param(
    [string]$Thumbprint,
    [string]$Configuration = 'Release',
    [switch]$SkipBuild,
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot

function Write-Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Fail($m) { Write-Host "ERROR: $m" -ForegroundColor Red; exit 1 }

# --- Resolve signing certificate ------------------------------------
if (-not $Thumbprint) {
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=dg' } |
            Sort-Object NotAfter | Select-Object -Last 1
    if (-not $cert) {
        Fail "No 'CN=dg' certificate found in Cert:\CurrentUser\My. Create one (see the header of this script) or pass -Thumbprint."
    }
    $Thumbprint = $cert.Thumbprint
}
Write-Host "Signing thumbprint: $Thumbprint"

# --- Build the signed bundle ----------------------------------------
$buildStart = Get-Date
if (-not $SkipBuild) {
    # vswhere.exe always installs under the x86 Program Files; probe both, tolerate absence.
    $msbuild = $null
    $vswhere = @("${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe",
                 "${env:ProgramFiles}\Microsoft Visual Studio\Installer\vswhere.exe") |
               Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($vswhere) {
        $msbuild = & $vswhere -latest -prerelease -find MSBuild\**\Bin\MSBuild.exe 2>$null |
                   Select-Object -First 1
    }
    if (-not $msbuild) {
        # Fall back to a direct VS install probe, then PATH.
        $msbuild = Get-ChildItem "${env:ProgramFiles}\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe" `
                       -ErrorAction SilentlyContinue | Sort-Object FullName | Select-Object -Last 1 -ExpandProperty FullName
    }
    if (-not $msbuild) { $msbuild = 'MSBuild.exe' }   # final fall back to PATH
    Write-Host "MSBuild: $msbuild"

    Write-Step "Building signed MSIX bundle ($Configuration, x64)"
    & $msbuild "$repo\XboxGamingBarPackage\XboxGamingBarPackage.wapproj" `
        /t:Build /p:Configuration=$Configuration /p:Platform=x64 `
        /p:AppxBundlePlatforms=x64 /p:UapAppxPackageBuildMode=SideloadOnly /p:AppxBundle=Always `
        /p:PackageCertificateThumbprint=$Thumbprint /p:PackageCertificateKeyFile= `
        /nologo /v:minimal
    # NOTE: the wapproj build often exits 1 at mspdbcmf.exe (PDB merge, code 1106) while
    # still producing the .msixbundle + .cer. We verify the artifacts below instead of
    # trusting the exit code.
}

# --- Locate the freshest bundle + cer -------------------------------
$appPkgs = "$repo\XboxGamingBarPackage\AppPackages"
if (-not (Test-Path $appPkgs)) { Fail "AppPackages folder not found - did the build run?" }

$bundle = Get-ChildItem $appPkgs -Recurse -Filter *.msixbundle |
          Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $bundle) { Fail "No .msixbundle produced under AppPackages." }
# The wapproj build exits 1 even on success (mspdbcmf, see above), so a real failure (e.g.
# MakeAppx) used to go unnoticed and the previous release's bundle got shipped. Require a
# bundle written by this run.
if (-not $SkipBuild -and $bundle.LastWriteTime -lt $buildStart) {
    Fail "The newest bundle ($($bundle.Name), $($bundle.LastWriteTime)) is older than this build - packaging failed. Check the MSBuild output above (MakeAppx errors)."
}
$cer = Get-ChildItem $bundle.DirectoryName -Filter *.cer | Select-Object -First 1
if (-not $cer) { Fail "No .cer next to the bundle ($($bundle.DirectoryName))." }

# Derive a friendly version string from the bundle name (..._<ver>_x64.msixbundle).
$version = if ($bundle.Name -match '_(\d+\.\d+\.\d+\.\d+)_') { $Matches[1] } else { 'latest' }
Write-Host "Bundle:  $($bundle.Name)  (version $version)"

# --- Assemble dist/ -------------------------------------------------
$dist = "$repo\dist"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null

$bundleOut = "GoTweaks_$version.msixbundle"
$cerOut    = "GoTweaks_$version.cer"
Copy-Item $bundle.FullName (Join-Path $dist $bundleOut)
Copy-Item $cer.FullName    (Join-Path $dist $cerOut)

# --- Compile the GUI installer (the only installer shipped - hard requirement) ---
Write-Step "Compiling GoTweaks-Setup.exe (GUI installer)"
if (-not (Get-Module -ListAvailable -Name ps2exe)) {
    Fail "ps2exe module not installed - GoTweaks-Setup.exe is the only installer shipped, so this is required. Run: Install-Module ps2exe -Scope CurrentUser"
}
try {
    Import-Module ps2exe -ErrorAction Stop
    # Embed the bundle + cert so GoTweaks-Setup.exe is a fully self-contained single-file
    # install - they're extracted to %TEMP%\GoTweaksSetup\ at startup (see
    # Installer/GoTweaksSetupGUI.ps1). Target paths must match that script's $embeddedDir.
    $embedFiles = @{
        '%TEMP%\GoTweaksSetup\GoTweaks.msixbundle' = (Join-Path $dist $bundleOut)
        '%TEMP%\GoTweaksSetup\GoTweaks.cer'         = (Join-Path $dist $cerOut)
    }
    Invoke-ps2exe -inputFile "$repo\Installer\GoTweaksSetupGUI.ps1" -outputFile (Join-Path $dist "GoTweaks-Setup.exe") `
        -iconFile "$repo\XboxGamingBarHelper\GoTweaks.ico" -noConsole -requireAdmin -STA -noConfigFile `
        -title "GoTweaks Lite Setup" -product "GoTweaks Lite" -company "GoTweaks Lite" `
        -version $version -description "GoTweaks Lite installer" -embedFiles $embedFiles | Out-Null
    Write-Host "   GoTweaks-Setup.exe compiled (embeds the bundle + cert)." -ForegroundColor Green
} catch {
    Fail "Failed to compile GoTweaks-Setup.exe: $($_.Exception.Message)"
}

Write-Step "Staged release in dist/"
Get-ChildItem $dist | Select-Object Name, @{n='Size';e={'{0:N0} KB' -f ($_.Length/1KB)}} | Format-Table -AutoSize

# --- Optional zip (archival only - not needed for a normal release upload) ----
if ($Zip) {
    $zipPath = Join-Path $dist "GoTweaks_$version.zip"
    Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zipPath -Force
    Write-Host "Zip: $zipPath" -ForegroundColor Green
}

Write-Host ""
Write-Host "Release assembled. Upload the 3 files in dist/ as separate GitHub Release assets." -ForegroundColor Green
