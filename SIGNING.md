# Code signing

Release executables are signed with Authenticode (SHA-256, RFC 3161 timestamp) using the author's personal certificate.

| | |
|---|---|
| Subject | CN=Paolo Rubagotti |
| Certificate thumbprint (SHA-1) | `F73FDA46DD3A0A7DE82C0E479AE1F1E5091416F8` |
| SHA-256 fingerprint of `Paolo-Rubagotti-CodeSigning.cer` | `A8F7445D921A1895901DFB8E9BACD23D25F96F425EE63D82D2269B4ECA004321` |
| Valid until | 2036-10-25 |

The certificate is **self-signed**, not issued by a public certificate authority. The signature proves that the file was
not modified after signing and names the signer, but a PC that has not imported the certificate cannot verify that the
name really belongs to the author, and SmartScreen may still warn.

To trust it on your PC, compare the fingerprint above with the one in the release notes (a separate page from the
download), then run the `Import-Certificate.ps1` script shipped in the release zip next to the `.cer`. Only trust the
certificate after the fingerprints match.

Verify a downloaded file with `Get-AuthenticodeSignature .\TestLoom.exe` (the signer thumbprint must match) and with the
`SHA256SUMS.txt` published in the release.
