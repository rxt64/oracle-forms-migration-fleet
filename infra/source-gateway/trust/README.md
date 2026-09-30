# Source Gateway Public Trust

This directory deliberately contains no certificate yet. After an approved host bootstrap creates the
private development root, copy only its public DER certificate here as
`ofm-source-gateway-dev-root-2026.cer`, record its SHA-256, and submit the public bytes for review.

The private key never leaves the Forms VM. The trusted `source-gateway-image.yml` workflow verifies the
public certificate SHA-256, converts the reviewed DER bytes to PEM, verifies CA and self-chain properties,
and binds the base digest to the healthy latest-ready public workbench before building the trust overlay.
It runs `update-ca-certificates` and retains a commit/digest/revision manifest; no custom certificate
callback or validation bypass is permitted. Installer reruns reuse the persisted root/leaf thumbprint
binding and refuse unmanaged matching certificates instead of silently rotating trust.