# Limitations

- **The SCOM SDK documents no completeness, truncation, cancellation or backend-work
  contract.** This project cannot prove from the SDK alone that a collection returned every
  object, that a limit did not truncate results, that a cancelled call stopped server-side work,
  or how much work SCOM did on the backend.
- **Completeness is established by per-edge validation plus console reconciliation.** The
  collector validates each relationship edge it traverses, and the operator reconciles the
  inventory against the Operations console (the acceptance checks in
  [install-cli.md](install-cli.md)). Verification flags in the worker configuration are
  operator attestations.
- **Lifecycle is not licensing or entitlement.** `list_os_support_assessments` reports
  vendor lifecycle dates from a bundled, dated catalog for exact OS captions. It says nothing
  about licenses, activation, Software Assurance or ESU entitlement. See
  [os-lifecycle-assessment.md](os-lifecycle-assessment.md).
- **This is not a monitoring replacement.** It serves a retained snapshot for conversational
  inventory. It does not provide live health, alerting, metrics or incident response.

## Stale and partial semantics

Every answer carries `collected_at`, coverage and freshness. After the **fresh** window the
data is reported stale; after the **unavailable** window the tools return `SOURCE_UNAVAILABLE`
(`READ_MODEL_EXPIRED`) rather than data. Partial or incomplete coverage is reported, never
hidden. Details: [mcp-server-spec.md](mcp-server-spec.md).

## Other limits

- Public-cloud Entra only; sovereign clouds are not implemented.
- Windows Server inventory (and lifecycle for the catalog's listed versions); network devices,
  UNIX/Linux and application metrics are out of scope.
- Class, property and relationship names are management-pack dependent; the examples are candidates, not guarantees.
- Disk-size and memory units depend on the property metadata and must be verified ([examples/worker.example.md](../examples/worker.example.md)).
- Lab validation covers a three-server Windows Server 2022 environment only; the hourly cadence, larger sizes, certificate-rotation and clean-stop-then-reboot paths are not lab-verified.
- Unsigned community release; no warranty ([../LICENSE](../LICENSE)).
