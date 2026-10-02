# Source Gateway Public Trust

`ofm-source-gateway-dev-root-2026.cer` contains only the public DER development root recovered
from the allowlisted result of approved installation run `36945107616`. Its SHA-256 is
`f285dc714a836a0c3b34855e4eb7c17009c5ac48421b7ca4092252aee1f2a327`, matching the root
already bound on the Forms VM. The certificate is submitted for review with this record;
its presence does not establish a deployed trust overlay or gateway qualification.

The private key never leaves the Forms VM. The trusted `source-gateway-image.yml` workflow verifies the
public certificate SHA-256, converts the reviewed DER bytes to PEM, verifies CA and self-chain properties,
and binds the base digest to the healthy latest-ready public workbench before building the trust overlay.
It runs `update-ca-certificates` and retains a commit/digest/revision manifest; no custom certificate
callback or validation bypass is permitted. Installer reruns reuse the persisted root/leaf thumbprint
binding and refuse unmanaged matching certificates instead of silently rotating trust.