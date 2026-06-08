# Verifying Your OmniConsole Installation

> 🌐 **English** | [繁體中文](AUTHENTICITY.zh-TW.md)

This document explains how to confirm your installed copy of OmniConsole is the official build from this repository.

## Official Source

The only official sources for OmniConsole are:

    https://github.com/8bit2qubit/OmniConsole/releases
    https://8bit2qubit.github.io/omniconsole-site/download

Builds from any other source (mirror, fork, third-party site) are not endorsed by this project and may have been modified.

## Verifying the Certificate

Open OmniConsole Settings → ☰ → About → **Release Info** → **Details**, then compare the **Certificate** SHA-256 thumbprint shown in the **Certificate Details** dialog with the value below.

**Official SHA-256 thumbprint:**

    DA:39:35:21:02:3B:87:EF:BF:52:95:CC:2D:AC:3D:DC:3A:75:7F:84:30:34:27:F8:9D:DB:59:EE:27:2A:5C:9A

If the values do not match, your installed build is not from this repository.

## Inspect the Certificate Yourself

You can independently verify the certificate without relying on the About page, using PowerShell. A complete OmniConsole installation has two packages — **OmniConsole** itself and the **OmniCharm widget** — and both should be verified. The command below checks both at once:

```powershell
$packages = @(
  'b5fbce6b-2d7d-4da0-b419-4beb30e2b808'  # OmniConsole
  '4fa8e044-7ffa-4059-b034-e4111881d96e'  # OmniCharm widget
)
$packages | ForEach-Object { Get-AppxPackage -Name $_ } | ForEach-Object {
  $sig  = Join-Path $_.InstallLocation 'AppxSignature.p7x'
  $hash = (Get-AuthenticodeSignature -FilePath $sig).SignerCertificate.GetCertHashString('SHA256')
  '{0}: {1}' -f $_.Name, (($hash -split '(.{2})' -ne '') -join ':')
}
```

Both packages are signed by the same certificate; each line is prefixed with the package name, and every thumbprint should equal the official value above (case-insensitive). If any value does not match, that package is not from this repository. If only the OmniConsole line appears, the OmniCharm widget is not yet installed.
