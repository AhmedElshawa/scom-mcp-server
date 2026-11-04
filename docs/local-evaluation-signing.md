# Personal-lab signed evaluation derivative

This helper uses an **existing**, currently valid Code Signing certificate and
its private key in `CurrentUser\My`. It never exports the key, creates a
certificate, adds trust, changes execution policy, removes zone markings,
answers security warnings or starts setup/SCOM. The default pinned signer is
`FD229CD0460526F7A1BDE7AF70C12670C9904B59`, the operator's separately approved
personal-lab certificate, not a production publisher.

The operator must already have approved that exact certificate in **their own**
CurrentUser Root and TrustedPublisher stores. This does not grant trust to
another worker account, IIS identity or LocalMachine. Later execution as
another account requires its own approved trust process.

## One local operation after review

Obtain `Setup\Sign-ScomEvaluationPackage.ps1` from this PR's reviewed exact
commit, review it, and verify its separately supplied SHA-256. The helper is
unsigned source: downloading it does not authorize running it. If needed, use
the normal visible `Set-AuthenticodeSignature` cmdlet with the existing approved
certificate to sign **that reviewed helper**, verify `Get-AuthenticodeSignature`
returns `Valid` with the exact signer, then invoke its file normally. Do not
load it with `Invoke-Expression`, remove its zone stream, bypass warnings or
change policy.

For the already downloaded **1b6dc4c** package, the independent original manifest
pin is:

```text
sourceCommit: 1b6dc4cd33c7cb0028b8d7e1458cefa91f3bfd93
package-manifest.json SHA-256:
052C5D908F9B11E516297F882512F9E5A7D7435CB2C8D342F4564B4DBBD50988
```

Invoke the normally authorized helper under the key-owning Windows account:

```powershell
.\Sign-ScomEvaluationPackage.ps1 `
  -PackageDirectory 'C:\5\evaluation-1b6dc4cd33c7cb0028b8d7e1458cefa91f3bfd93 (1)\scom-evaluation\ci-evaluation' `
  -DestinationDirectory 'C:\5\scom-personal-lab-signed' `
  -ExpectedManifestSha256 '052C5D908F9B11E516297F882512F9E5A7D7435CB2C8D342F4564B4DBBD50988' `
  -ExpectedSourceCommit '1b6dc4cd33c7cb0028b8d7e1458cefa91f3bfd93'
```

Choose the actual original package location and a **new sibling destination**;
the helper refuses overlapping, existing or reparse-point outputs. Other
reviewed builds require their own independently verified source/manifest pins.
Never compute a replacement pin from an unverified package to silence rejection.

## Output and provenance

Before creating output the helper checks the original manifest pin, exact file
set and every SHA-256, absence of runtime configs/state, example-only worker
profile, certificate EKU/validity/private key and existing CurrentUser trust.
It signs only `ScomSetup.exe`, application-owned `server\ScomMcp.exe` if present,
and packaged `.ps1`/`.psm1` files in setup/collector/durable operations with
SHA-256. Vendor .NET binaries are untouched. It verifies every resulting
Authenticode signature and pinned signer, preserves unsigned-original hashes,
and regenerates a derivative manifest carrying the original source commit,
original manifest hash, exact signer and signed file list.

The new directory, `.zip` and `.zip.sha256` are published only after extracting
and revalidating the derivative archive and signatures. On failure it throws,
reports no success, removes any newly published ZIP/sidecar, and retains
partial output for review. Do not deploy partial outputs or reuse them as a
source. The original package is never edited. No timestamp service is used:
signatures are lab-only, not durable production release signing.

The setup backend creates `installed-files.json` **after** copying the signed
server/collector files. During Configure it computes
`Worker.profile.scriptSha256` from the actual installed signed profile. Therefore
the placeholder worker example remains unchanged; no live config, checkpoint,
receipt, installed manifest or source evidence is rewritten by this helper.
Do not sign an already installed/configured deployment with this tool.

Signing fixes neither missing prerequisites nor source/Entra readiness. Use the
new derivative only after its successful verification and customer review;
all wizard confirmations remain required. This is still `notCertified`.
