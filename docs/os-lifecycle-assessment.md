# Windows Server support-lifecycle assessment

## What this adds

`list_os_support_assessments` is an additive read-only MCP tool over the same
authorized, retained Windows computer view as the original six tools. It works
with either the legacy snapshot or the enabled indexed Windows projection.
It does not query SCOM or the internet when a question is asked, alter inventory,
write alerts, assess patch compliance or establish contractual support entitlement.
Generic object counts are never treated as server counts.

Example questions:

- "Which approved computers reach the end of extended support within a year?"
- "Which observed Windows Server products have ended standard extended support?"
- "Which computers cannot be assessed, and what inventory or evidence is missing?"
- "Show the Microsoft source and verification date behind this assessment."

## Declared coverage and exact matching

Catalog version **windows-server-fixed-2026-11-02.1** was checked against the
Microsoft sources below on **2026-11-02**. This is a curated project catalog,
not an official auto-updating Microsoft API. Supported aliases consist of the
exact product below followed by an allowed edition, optionally prefixed by
`Microsoft`. Comparison ignores case and leading/trailing whitespace only.

| Exact product | Allowed editions | Mainstream end (inclusive PT) | Extended end (inclusive PT) | Evidence |
|---|---|---|---|---|
| Windows Server 2012 R2 | Standard, Datacenter, Essentials, Foundation | 2018-10-09 | 2023-10-10 | [Microsoft lifecycle](https://learn.microsoft.com/en-us/lifecycle/products/windows-server-2012-r2) |
| Windows Server 2016 | Standard, Datacenter, Essentials | 2022-01-11 | 2027-01-12 | [Microsoft lifecycle](https://learn.microsoft.com/en-us/lifecycle/products/windows-server-2016) |
| Windows Server 2019 | Standard, Datacenter, Essentials | 2024-01-09 | 2029-01-09 | [Microsoft lifecycle](https://learn.microsoft.com/en-us/lifecycle/products/windows-server-2019) |
| Windows Server 2022 | Standard, Datacenter, Essentials | 2026-10-13 | 2031-10-14 | [Microsoft lifecycle](https://learn.microsoft.com/en-us/lifecycle/products/windows-server-2022) |
| Windows Server 2025 | Standard, Datacenter, Essentials | 2029-11-13 | 2034-11-14 | [Microsoft lifecycle](https://learn.microsoft.com/en-us/lifecycle/products/windows-server-2025) |

These are a deliberate subset of Microsoft's edition listings, not a claim that
omitted editions lack support. Caption `Microsoft Windows Server 2022 Standard`
matches. Bare `Windows Server 2022`, build `10.0.20348`, evaluation captions,
localized/modified captions, Azure Edition, MultiPoint, Embedded, Annual Channel/
Semi-Annual Channel versions, unknown editions and unmapped releases remain
`unknown`; no substring, fuzzy-name or build-to-release inference is made.
The [servicing-channel reference](https://learn.microsoft.com/en-us/windows-server/get-started/servicing-channels-comparison)
distinguishes release-named long-term versions from version-named Annual Channel
products. The tool reports the matched product's fixed lifecycle policy, not an
independently discovered channel attribute. Adding an alias requires source-backed
applicability review; never relabel customer inventory to force a match.

Missing OS captions are `unknown`. Non-Windows or unknown OS families remain in
the result as `unsupported` by this assessment profile; that does not assert that
their vendor has ended support. Other OS inventory is not changed. Windows tools
still do not ingest universal Linux/custom-OS inventory.

## Dates, phases and ESU

`asOfDate` is the server clock's current **Pacific calendar date**, using the
Microsoft lifecycle pages' declared timezone (`Pacific Standard Time`, with DST).
The host must supply correct time and timezone data. Source pages contain
`local-time` UTC timestamps; their browser-rendered Pacific dates, not the
unconverted UTC day, determine the dates above.

`mainstream` includes its end date; `extended` starts on the next day and includes
the extended end date; `ended` starts the day after that. `daysToExtendedEnd` is
the signed calendar-day difference, zero on the last supported day. Approaching
means **0 through the configured threshold, inclusive**, before extended support
ends; it does not mean mainstream has ended or extend support to an ESU deadline.
These are product-level calendar assessments, not proof that the installed
servicing state meets all Microsoft support requirements.

The [Microsoft ESU FAQ](https://learn.microsoft.com/en-us/lifecycle/faq/extended-security-updates)
states that ESU does not extend the product lifecycle or provide ordinary technical
support. This profile cites the optional Windows Server 2012/R2 ESU program end
**2026-10-13** separately. `esuEntitlement` is always `notAssessed`: inventory
cannot establish enrollment, payment, eligibility or installed security updates.
Other entries' null ESU dates mean no program assessed by this catalog, not proof
that a program does not exist. ESU never changes the reported baseline phase.

## Configuration and evidence freshness

Both server configuration examples include:

```json
"Lifecycle": {
  "ApproachingSupportDays": 365,
  "MaxCatalogAgeDays": 90
}
```

Defaults are 365 days and 90 days. Startup rejects thresholds outside 1..3650
and catalog ages outside 1..90. A shorter evidence-age policy is allowed; do not
raise it to bypass stale evidence. A catalog verified in the future relative to
the assessment date is `notYetVerified`; after the allowed age it is `stale`.
Mapped products then return `unknown`, with null days/approaching classifications.
Dates remain visible as **unusable catalog evidence**, not confident conclusions.
Explicit unknown counts must accompany any zero approaching count.

The response includes catalog version, verification date, age, state, configured
policy, per-row Microsoft URLs and the inventory envelope's dataset, collected
time, freshness, expected membership and limitations. Fresh catalog evidence
does not make stale inventory fresh. Failed/expired source reads still deny the
tool instead of silently assessing an old dataset.

Maintainers should review the cited sources at least monthly and before the
90-day limit. Review exact product/edition/channel applicability and ESU policy,
update catalog facts/version/verification date in `ScomMcp\OsLifecycle.cs`, and
rerun lifecycle plus full regressions and package checks. Updating only a date
without source review is not verification. Distribute an approved new build
through normal change control; this feature does not self-update binaries or
scrape pages automatically. Customer operators should monitor `catalogState`
and route stale evidence to the package maintainer.

## Counts, paging and agent interpretation

Arguments are optional `scopeId`, `assessment`, `pageSize` (1..50, default 20)
and opaque `cursor`. `assessment` accepts `mainstream`, `extended`, `ended`,
`unknown`, `unsupported` or `approaching`. It filters rows, not coverage counts.
Rows are ordered by hostname then entity ID, using existing cursor protections.
Cursors bind caller, scope, dataset/fingerprint, filter, page size, Pacific
assessment date, catalog version and policy. Restart paging after a change,
expiry or restart; never stitch pages from different assessments.

`scopeComputerCount`, phase counts, classified/unknown/unsupported counts and
approaching count cover **all retained computers before paging/filtering**.
`matchedComputerCount`/`totalCount` cover all matching rows, not just this page.
Missing expected members are counted separately and cannot be assessed.
`countsAreCompleteForRetainedComputers` does not certify complete source scope.
`assessmentComplete` requires fresh evidence, no unknown/unsupported rows and no
missing expected computers, fresh non-partial inventory and no validation gaps;
still read source coverage/freshness. Responses remain
qualified `partial` because inventory alone cannot certify support eligibility.

An agent should say "among the retained approved computers, using catalog X
verified on Y" and include unknown/missing counts. It must not equate extended
support with mainstream support, `unsupported` profile coverage with vendor
end-of-support, or ESU availability with entitlement.

## Verification versus customer acceptance

Offline tests cover every mapped edition, date boundaries, threshold inclusivity,
unknown captions/editions/channels, missing attributes, stale/future catalog,
ESU separation, complete counts, paging bindings and legacy/indexed authenticated
HTTP access. The six existing tool contracts are unchanged.

This is implemented evidence-backed assessment, not accepted customer deployment.
Clean-host/source-caption/customer-scope/Studio-answer acceptance and recurring
operation still require separate authorization. No automatic release, installation,
permission change or source probe is part of this feature.
