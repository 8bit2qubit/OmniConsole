# Verifying Your OmniConsole Installation

> 🌐 **English** | [繁體中文](AUTHENTICITY.zh-TW.md)

This document explains how to confirm your installed copy of OmniConsole is the official build from this repository.

## Official Source

The only official sources for OmniConsole are:

    https://github.com/8bit2qubit/OmniConsole
    https://8bit2qubit.github.io/omniconsole-site/download

Builds from any other source (mirror, fork, third-party site) are not endorsed by this project and may have been modified.

## Verifying the Certificate

Open OmniConsole Settings → ☰ → About → **Release Info** → **Details**, then compare the **Certificate** SHA-256 thumbprint shown in the **Certificate Details** dialog with the value below.

**Official SHA-256 thumbprint:**

    DA:39:35:21:02:3B:87:EF:BF:52:95:CC:2D:AC:3D:DC:3A:75:7F:84:30:34:27:F8:9D:DB:59:EE:27:2A:5C:9A

If the values do not match, your installed build is not from this repository.

## Inspect the Certificate Yourself

You can independently verify the certificate without relying on the About page, using PowerShell:

```powershell
Get-AppxPackage -Name b5fbce6b-2d7d-4da0-b419-4beb30e2b808 |
  ForEach-Object {
    $sig = Join-Path $_.InstallLocation 'AppxSignature.p7x'
    $hash = (Get-AuthenticodeSignature -FilePath $sig).
              SignerCertificate.GetCertHashString('SHA256')
    ($hash -split '(.{2})' -ne '') -join ':'
  }
```

The output is the SHA-256 thumbprint in colon-separated form, matching the format shown on the About page. Compare it (case-insensitive) against the official value above.
