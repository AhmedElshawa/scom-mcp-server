# Imported package provenance

The wrapper sources, native helper, synthetic harness, and source review
evidence in this directory were imported from the explicitly approved
`SCOM-MCP-Supervised-Wrappers-native-job-review-v2.zip` archive.

- SHA-256: `AACA959E0ADE3B90576928E552623DD62687FB76227FBA31197963984A49197D`
- Archive entries were validated as flat file names before extraction.
- The package's `REPAIR-RESULTS.txt`, `README-REPAIRED.txt`, and
  `verified-ps7-results.json` are retained as supplied. They describe the
  external review host and its historical runs only; they are not claims that
  either engine has passed this repository's hosted CI.
- The package recorded a prior PowerShell 7 run as 19/19 passing and the full
  Windows PowerShell 5.1 suite as blocked by unsigned-script policy. Those
  statements remain historical evidence, not current dual-engine acceptance.
- Current dual-engine results are produced by
  `.github/workflows/supervised-indexed-run.yml`; each result records the exact
  reviewed commit and is retained as a CI artifact.

No signing, execution-policy change, real SCOM/config/source call, live
deployment, or production acceptance is included.
