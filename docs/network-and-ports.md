# Network and ports

Only **one inbound path** is needed: HTTPS 443 to the receiver. Never expose
management ports or the SCOM SDK service to the internet.

| # | Source | Destination | Port | Purpose |
|---|---|---|---|---|
| 1 | Collector worker | SCOM management server | TCP 5724 (SDK / Data Access service); TCP 5723 (management server / agent channel) | SDK read-only collection. Ports are Microsoft's documented Operations Manager defaults; confirm them in your environment. Not inbound from the internet. |
| 2 | Collector worker | Receiver `/ingestion` | TCP 443 | Publish datasets. Same host in the co-located layout (loopback or LAN). |
| 3 | Copilot Studio (Power Platform) | Receiver `/mcp` | TCP 443 inbound | Tool calls. Allow source service tags **`AzureConnectors`** (regionless) and **`PowerPlatformPlugins`**. |
| 4 | Receiver | `login.microsoftonline.com` | TCP 443 outbound | OIDC metadata and signing keys for token validation. |
| 5 | Worker | `login.microsoftonline.com` | TCP 443 outbound | Client-credentials token for ingestion. |
| 6 | Receiver / worker host | DNS | UDP/TCP 53 | The server must resolve **its own FQDN**; activation's health check fails otherwise. |

Lab note: a rule using a region-specific connector tag (for example `AzureConnectors.<region>`)
did not admit the connector's traffic, while the regionless `AzureConnectors` tag plus
`PowerPlatformPlugins` did. Service tags are broad: after go-live, review the IIS log
for the observed Power Platform source addresses and tighten the rule where
your policy allows.

## Azure NSG example

One inbound rule on the subnet (or NIC) NSG, priority **lower number than any deny**:

```powershell
$rule = @{
  Name='Allow-CopilotStudio-443'; Priority=<PRIORITY>; Direction='Inbound'; Access='Allow'
  Protocol='Tcp'; SourcePortRange='*'; DestinationPortRange='443'
  SourceAddressPrefix=@('AzureConnectors','PowerPlatformPlugins')
  DestinationAddressPrefix='<RECEIVER_NIC_PRIVATE_IP>'
}
# Add to your NSG with Add-AzNetworkSecurityRuleConfig / Set-AzNetworkSecurityGroup,
# or the equivalent portal / az network nsg rule create commands.
```

If connections still time out, check in this order: NSG effective rules for the NIC,
Windows Firewall, the IIS binding, and DNS/public IP mapping. Do not leave a broad
Internet-to-443 rule in place.

## Windows Firewall

Allow inbound TCP 443 on the receiver host with an explicit inbound rule scoped to the
required sources, and confirm it with `Get-NetFirewallRule -Enabled True -Direction Inbound`
filtered to local port 443 (`Get-NetFirewallPortFilter`), together with the effective NSG rules.

## TLS certificate

- Issued by a CA the connector trusts (public CA), valid and not expired.
- Subject Alternative Name contains the exact `<FQDN>`.
- Server Authentication EKU (`1.3.6.1.5.5.7.3.1`) and an accessible private key.
- Installed in `LocalMachine\My` on the receiver host.
- Certificate warnings are not an acceptable workaround.

## IIS binding

The HTTPS binding uses the exact format

```text
*:443:<fqdn>
```

(IP `*`, port `443`, host name `<fqdn>`) bound to the certificate above. The guided setup
creates a dedicated site or subapplication with this binding; if you configure IIS by
hand, use a dedicated application pool (No Managed Code, Integrated pipeline).

Verify from outside the host: `GET https://<FQDN>/healthz` returns 200 (reachability
only), and `POST https://<FQDN>/mcp` without a token returns 401.
