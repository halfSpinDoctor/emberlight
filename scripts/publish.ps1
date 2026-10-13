<#
.SYNOPSIS
  Builds distributable copies of Emberlight into .\artifacts, optionally code-signing them.

.DESCRIPTION
  Produces two single-file flavours (x64):
    artifacts\selfcontained   Runs on any 64-bit Windows 10/11 with nothing installed (~70-150 MB).
    artifacts\small           Needs the ".NET 10 Desktop Runtime" installed on the PC (a few MB).

  Nothing is signed unless you pass ONE of the signing options below. Without a certificate trusted by
  Windows, Smart App Control (Windows 11) will refuse to run the exe; see "Windows security prompts" in README.md.
  (A self-signed certificate does not satisfy Smart App Control.)

  Run from the repository root:   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1

.PARAMETER CertThumbprint
  Sign with a certificate that is already installed in your certificate store (identified by its thumbprint).

.PARAMETER PfxPath
  Sign with a certificate file (.pfx). Use together with -PfxPassword.

.PARAMETER PfxPassword
  Password of the .pfx, as a SecureString:  -PfxPassword (Read-Host -AsSecureString)

.PARAMETER ArtifactSigningDlib
  Sign with Azure Artifact Signing (formerly Trusted Signing). Path to Azure.CodeSigning.Dlib.dll from the
  Artifact Signing client tools. Use together with -ArtifactSigningMetadata.

.PARAMETER ArtifactSigningMetadata
  Path to the metadata.json that names your Artifact Signing endpoint, account and certificate profile.

.PARAMETER TimestampUrl
  RFC 3161 timestamp server. The timestamp keeps a signature valid after the certificate expires.
  Defaults to DigiCert's server, or Microsoft's for Artifact Signing.

.EXAMPLE
  scripts\publish.ps1 -CertThumbprint 0123456789ABCDEF0123456789ABCDEF01234567

.EXAMPLE
  scripts\publish.ps1 -PfxPath C:\keys\mycert.pfx -PfxPassword (Read-Host -AsSecureString)

.EXAMPLE
  scripts\publish.ps1 -ArtifactSigningDlib "C:\tools\Azure.CodeSigning.Dlib.dll" -ArtifactSigningMetadata .\metadata.json
#>
param(
    [string]$Configuration = "Release",

    # --- optional signing: give at most one of the three groups ---
    [string]$CertThumbprint,
    [string]$PfxPath,
    [securestring]$PfxPassword,
    [string]$ArtifactSigningDlib,
    [string]$ArtifactSigningMetadata,
    [string]$TimestampUrl
)
$ErrorActionPreference = "Stop"
$project = "src/Emberlight.App/Emberlight.App.csproj"

# ---------------------------------------------------------------------------------------------
# Work out the signing mode (if any) before building anything, so mistakes fail fast.
# ---------------------------------------------------------------------------------------------
$modes = @()
if ($CertThumbprint) { $modes += "store" }
if ($PfxPath -or $PfxPassword) { $modes += "pfx" }
if ($ArtifactSigningDlib -or $ArtifactSigningMetadata) { $modes += "artifact" }
if ($modes.Count -gt 1) { throw "Choose only one signing method (store certificate, .pfx, or Artifact Signing)." }
$signMode = if ($modes.Count -eq 1) { $modes[0] } else { $null }

if ($signMode -eq "pfx" -and (-not $PfxPath -or -not $PfxPassword -or -not (Test-Path $PfxPath))) {
    throw "-PfxPath (an existing file) and -PfxPassword are both required for .pfx signing."
}
if ($signMode -eq "artifact" -and (-not $ArtifactSigningDlib -or -not $ArtifactSigningMetadata -or
        -not (Test-Path $ArtifactSigningDlib) -or -not (Test-Path $ArtifactSigningMetadata))) {
    throw "-ArtifactSigningDlib and -ArtifactSigningMetadata must both be given and point to existing files."
}

$signtool = $null
if ($signMode) {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { $signtool = $cmd.Source }
    else {
        # installed with the Windows SDK / Visual Studio
        $found = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($found) { $signtool = $found.FullName }
    }
    if (-not $signtool) { throw "signtool.exe not found. Install the Windows SDK (or Visual Studio) and try again." }
    if (-not $TimestampUrl) {
        $TimestampUrl = if ($signMode -eq "artifact") { "http://timestamp.acs.microsoft.com" } else { "http://timestamp.digicert.com" }
    }
}

function Invoke-Sign([string]$File) {
    Write-Host "Signing $File"
    $signArgs = @("sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256", "/d", "Emberlight")
    switch ($signMode) {
        "store"    { $signArgs += @("/sha1", $CertThumbprint) }
        "pfx"      {
            $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($PfxPassword)
            try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
            finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
            $signArgs += @("/f", $PfxPath, "/p", $plain)
        }
        "artifact" { $signArgs += @("/dlib", $ArtifactSigningDlib, "/dmdf", $ArtifactSigningMetadata) }
    }
    & $signtool @signArgs $File
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $File" }

    # Confirm Windows would accept the signature (this fails for certificates Windows doesn't trust).
    & $signtool verify /pa $File
    if ($LASTEXITCODE -ne 0) { Write-Warning "Signed, but Windows does not consider the signature trusted: $File" }
}

# ---------------------------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------------------------
# no .pdb files in the output, and WPF's native libraries bundled into the single file
$common = @("-c", $Configuration, "-r", "win-x64", "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:DebugType=None", "-p:DebugSymbols=false")

Write-Host "== Self-contained single file =="
dotnet publish $project @common --self-contained true -p:EnableCompressionInSingleFile=true -o artifacts/selfcontained
if ($LASTEXITCODE -ne 0) { throw "publish (self-contained) failed" }

Write-Host "== Framework-dependent single file (small) =="
dotnet publish $project @common --self-contained false -o artifacts/small
if ($LASTEXITCODE -ne 0) { throw "publish (framework-dependent) failed" }

# ---------------------------------------------------------------------------------------------
# Sign (optional)
# ---------------------------------------------------------------------------------------------
if ($signMode) {
    Invoke-Sign "artifacts/selfcontained/Emberlight.exe"
    Invoke-Sign "artifacts/small/Emberlight.exe"
    Write-Host "Done. Both executables are signed."
    Write-Host "If you build the installer, sign the installer .exe too (see the SignTool setting in Inno Setup)."
}
else {
    Write-Host "Done. The executables are UNSIGNED (no signing option given)."
    Write-Host "Windows 11 Smart App Control will block unsigned builds; see 'Windows security prompts' in README.md."
}
