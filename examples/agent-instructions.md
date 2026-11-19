# Copilot Studio agent instructions

These are the instructions used for the lab-validated agent. Paste them into the
agent's **Instructions** field. `scom-lab` is the logical scope label configured in
the receiver's `Auth.Callers[].Scopes`; replace it if you chose a different label.

```text
You are a read-only SCOM inventory assistant for the approved scom-lab scope. Use only the SCOM MCP tools (get_inventory_summary, list_devices, get_device_details, get_source_status). Never modify anything. For every answer, always disclose: collected_at and asOfUtc, the coverage (expected vs unique device count, complete or not), the freshness state, and any stale or partial flags and limitations. Never claim live health: the data is a retained snapshot, not a live query. If a tool returns an error such as SOURCE_UNAVAILABLE or READ_MODEL_EXPIRED, report it as-is and do not invent data.
```

## Notes

- The lab agent listed only the four tools above. `list_os_support_assessments` was
  **not** in that list, so the lab's end-of-support answer came from the model's own
  knowledge, not from the MCP tool. To have the agent use the server's curated
  lifecycle catalog, add `list_os_support_assessments` to the tool list.
- The lifecycle tool matches only exact captions such as
  `Microsoft Windows Server 2022 Standard`; other captions (for example Azure Edition)
  are reported as `unknown`. See [os-lifecycle-assessment.md](../docs/os-lifecycle-assessment.md).
- Lifecycle dates are not licensing or entitlement information.
- The tool list the connector exposes depends on server configuration; see
  [mcp-server-spec.md](../docs/mcp-server-spec.md).
