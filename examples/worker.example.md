# worker.example.json — field notes

JSON cannot hold comments, so each non-obvious setting is explained here.
Replace every `<PLACEHOLDER>`. In a guided install the wizard supplies identity,
thumbprint, script path/hash, publisher endpoint and state paths during its merge
step; these values are shown so you can review the whole file. Full reference:
[configuration-reference.md](../docs/configuration-reference.md).

## Schedule

`schedule.interval` is `01:00:00` (hourly), the recommended default for inventory.
The receiver freshness budget must exceed it: see `Source.FreshMinutes` in
[receiver.example.md](receiver.example.md). The lab ran a 5-minute interval; that is
not the recommended default.

## `windowsMapping` — Windows Server 2022 management-pack candidates

Run `setup\New-ScomMappingProfile.ps1` to generate this section from your own SCOM
instead of editing it by hand ([deployment-guide.md](../docs/deployment-guide.md#6c-workerjson)).
The table below documents the lab candidates the generator replaces.

The class, relationship and property names below are **candidates** taken from the
lab probe of Windows Server 2022 agents. They are not guaranteed for your management
packs. Verify each against your own probe output before setting any `*Verified` flag.

| Key | Candidate | Note |
|---|---|---|
| `computerClassName` | `Microsoft.Windows.Computer` | |
| `operatingSystemClassName` | `Microsoft.Windows.OperatingSystem` | |
| `volumeClassName` | `Microsoft.Windows.Server.LogicalDisk` | A base class of the observed disk class. The collector requires that the observed disks are instances of it, not that it is the most-derived class. |
| `computerHostsOsRelationship` | `Microsoft.Windows.ComputerHostsOperatingSystem` | Must also appear in `additionalRelationshipNames`. |
| `computerHostsVolumeRelationship` | `Microsoft.Windows.ComputerHostsLogicalDevice` | Direct computer-to-device edge, includes non-disk devices; non-volume targets are filtered. Use this **or** `osHostsVolumeRelationship`, never both. Must also appear in `additionalRelationshipNames`. |
| `hostnameProperty` | `PrincipalName` | |
| `osVersionProperty` | `OSVersion` | |
| `osCaptionProperty` | `OSVersionDisplayName` | Optional; drives lifecycle matching. |
| `memoryProperty` / `memoryToMiB` | `PhysicalMemory` / `0.0009765625` | The value is KB, so the factor is `1/1024`. Evidence: `get_device_details` returned 14,335.6 MiB for a server with 14 GiB of RAM; the management pack metadata labelling the property as MB is wrong for this value. |
| `processorCountProperty` | `LogicalProcessors` | |
| `volumeNameProperty` | `DeviceID` | |
| `fileSystemProperty` | `FileSystem` | |
| `volumeSizeProperty` / `volumeSizeToMiB` | `SizeInMBs` / `1` | |

`allowedPropertiesByClass` is keyed by the **most-derived** class actually returned
(for Windows Server 2022 in the lab: `…Server.10.0.Full.Computer`,
`…Server.10.0.Full.OperatingSystem`, `…Server.10.0.LogicalDisk`). It must be identical
in the receiver's `AllowedPropertiesByClass`.

## `relationshipReadMode`

`perSource` (default) reads the configured
relationships once per source object. `set` reads the OS and disk hosting relationships
once each for the whole scope; it was lab-validated on SCOM 2025 (identical inventory to
`perSource`, 2 SDK calls instead of 22), and it
requires the reviewed `windowsMapping` and no `hostingLabPreview`. The mapping generator
writes `set` when both relationships were observed on at least one sampled computer,
regardless of non-disk devices sharing the disk relationship (the reader skips and counts
them); the choice and its reason are recorded
in the evidence file. Any other value is rejected (`SCOM_SET_READ_MODE_INVALID`). The result
count of a set read is never evidence of completeness; completeness is always decided by
reconciling the expected computers and disks.

## Verification flags

`verified`, `agentMonitoringVerified`, `scopeVisibilityVerified`,
`wholeManagementGroupApproved`, `enumerationQueryContractVerified`,
`additionalRelationshipContractVerified` and `membershipQueryContractVerified` are
**operator attestations**, not SDK measurements. They are `false` here. Set a flag to
`true` only after you have reviewed the evidence it attests (probe output and the
Operations console Agent Managed list). Never set them to make validation pass.

## Placeholders

| Placeholder | Where to obtain it |
|---|---|
| `<MGMT_SERVER_FQDN>` | A SCOM management server the worker account can reach on the SDK port. |
| `<MG_ID>` / `<GROUP_ID>` | Management group ID / group ID from SCOM. For management-group scope set `scopeKind` to `managementGroup`, `groupId` to `null` and `wholeManagementGroupApproved` to `true` after approval. |
| `<SCRIPT_SHA256>` | SHA256 of the installed `Invoke-SCOMGenericGroupProfile.ps1` (the wizard computes it). |
| `<TENANT_ID>`, `<PUBLISHER_APP_ID>`, `<API_APP_ID>` | Your `identity.json` ([entra-setup.md](../docs/entra-setup.md)). |
| `<CERT_THUMBPRINT>` | 40-hex thumbprint of the publisher certificate in `LocalMachine\My`. |
| `<FQDN>` | Public host name of the receiver. |
