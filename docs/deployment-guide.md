# Deployment guide

An end-to-end, numbered walkthrough from an empty server to a Copilot Studio agent that
answers SCOM inventory questions. Every value you create is given a name, recorded once in
the [values register](#values-register), and referenced later as **(from Step N)**, so you
always know which value goes where.

> Unsigned community release, lab-validated on a 3-server Windows Server 2022/2025 lab,
> not production-certified. Whole-management-group scope is validated on 3 servers only;
> start with group scope ([sizing-and-scope](sizing-and-scope.md)).

This guide stitches together the reference pages; it does not replace them. Follow it in
order, and open the linked page when you need detail.

## Contents

- [What you will build](#what-you-will-build)
- [Before you start](#before-you-start)
- [Values register](#values-register)
- [Step 1. Download and verify the package](#step-1-download-and-verify-the-package)
- [Step 2. Prepare the host: DNS, TLS, IIS, network](#step-2-prepare-the-host-dns-tls-iis-network)
- [Step 3. Prepare SCOM and the worker account](#step-3-prepare-scom-and-the-worker-account)
- [Step 4. Create the publisher certificate](#step-4-create-the-publisher-certificate)
- [Step 5. Create the three Entra app registrations](#step-5-create-the-three-entra-app-registrations)
- [Step 6. Write the configuration files](#step-6-write-the-configuration-files)
- [Step 7. Run setup: Prepare to Host Publish](#step-7-run-setup-prepare-to-host-publish)
- [Step 8. Connect Copilot Studio](#step-8-connect-copilot-studio)
- [Step 9. Acceptance checks and Accept](#step-9-acceptance-checks-and-accept)
- [Step 10. Register recurrence](#step-10-register-recurrence)
- [Step 11. Agent acceptance test](#step-11-agent-acceptance-test)
- [Find a value again](#find-a-value-again)
- [Troubleshooting](#troubleshooting)

## What you will build

![SCOM MCP reference architecture: scheduled remote SCOM collection, authenticated ingestion, separate local stores, and delegated Copilot Studio access](images/scom-reference-architecture.png)

- A **collector worker** (scheduled task, read-only account) reads SCOM through the
  OperationsManager SDK and publishes a snapshot over HTTPS with an app-only certificate token.
- An **MCP receiver** (IIS, ASP.NET Core) validates and stores the snapshot in a local read
  model and serves read-only tools at `https://<FQDN>/mcp`.
- A **Copilot Studio agent** calls those tools with each user's own Entra sign-in. SCOM is
  never queried when a question is asked.
- **Three Entra app registrations** keep the roles apart: API (the resource), Publisher
  (the worker), Reader (Copilot Studio).

Component detail: [architecture](architecture.md). Trust model: [security](security.md).

## Before you start

You need the following. People and roles differ per organization; one person may hold several.

| You need | Why | Used in |
|---|---|---|
| Local administrator on the Windows Server that will host the receiver and worker (Windows PowerShell 5.1, elevated) | Install, IIS, certificates, scheduled task | Steps 1, 2, 4, 7, 9, 10 |
| A DNS name for the server (`<FQDN>`) that resolves to it, **including from the server itself** | HTTPS binding, token audience checks, Activate health check | Steps 2, 6, 7, 8 |
| A TLS certificate for `<FQDN>` trusted by the server and by Copilot Studio (public or enterprise CA) | HTTPS endpoint | Step 2 |
| IIS and the .NET 8 ASP.NET Core Hosting Bundle (install after IIS) | Hosts the receiver | Steps 2, 7 |
| A SCOM management group, a reachable management server and the Operations Manager console or PowerShell module on the collector host | The data source | Steps 3, 6, 7 |
| A dedicated read-only domain account with Allow log on locally and Log on as a batch job | Runs the worker | Steps 3, 4, 7, 10 |
| An Entra tenant where you can register apps (Application Administrator or equivalent) and grant admin consent (or an administrator who can) | Three app registrations | Step 5 |
| Copilot Studio access in the same tenant | The agent | Step 8 |
| The Entra user(s) allowed to ask questions | Caller allow-list | Steps 5, 6, 9 |

Network: inbound 443 only; the worker reaches the management server on the SDK port.
Details: [network-and-ports](network-and-ports.md).

> **Warning:** Never paste secrets, PFX files or tokens into any file in this repository,
> an issue or a screenshot. The only secret in the whole flow is the reader app's client
> secret, and it is entered only in the Copilot Studio connector.

## Values register

Each row is created in one step and used in later steps. Keep your own copy in a private
note (not in the repository). Where a step says **(from Step N)**, use the value from this
table that Step N created.

| Value | What it is | Created in | Used in |
|---|---|---|---|
| `<FQDN>` | Public host name of the receiver | Step 2 | Steps 6, 8, 9 |
| `<TLS_THUMBPRINT>` | Thumbprint (40 hex) of the TLS certificate for `<FQDN>` | Step 2 | Step 6 |
| `<WORKER_ACCOUNT>` | Worker domain account, `DOMAIN\user` | Step 3 | Steps 4, 6, 7, 10 |
| `<MGMT_SERVER_FQDN>` | A SCOM management server the worker reaches | Step 3 | Step 6 |
| `<MG_ID>` | SCOM management group ID (GUID) | Step 3 | Step 6 |
| `<GROUP_ID>` | SCOM group ID (GUID), for group scope | Step 3 | Step 6 |
| `<OM_MODULE_PATH>` | Full path to the installed `OperationsManager.psd1` | Step 3 | Step 6 |
| `<CERT_THUMBPRINT>` | Thumbprint of the publisher client-auth certificate | Step 4 | Steps 5, 6 |
| `<PUBLISHER_CER_PATH>` | Path to the public `.cer` (no private key) | Step 4 | Step 5 |
| `<TENANT_ID>` | Entra tenant (directory) ID | Step 5 | Steps 6, 8 |
| `<API_APP_ID>` | API app (client) ID; resource is `api://<API_APP_ID>` | Step 5 | Steps 6, 8 |
| `<PUBLISHER_APP_ID>` | Publisher app (client) ID | Step 5 | Step 6 |
| `<PUBLISHER_SP_OBJECT_ID>` | Object ID of the publisher **Enterprise application** (service principal) | Step 5 | Step 6 |
| `<READER_APP_ID>` | Reader app (client) ID | Step 5 | Steps 6, 8 |
| Reader client secret | Credential for the reader app | Step 5 | Step 8 only (never in a file) |
| `<READER_USER_OBJECT_ID>` | Object ID of each allowed **user** | Step 5 | Step 6 |
| `<CALLBACK_URI>` | Redirect URI Copilot Studio shows for the connector | Step 8 | Step 8 (added to the reader app) |

Config-file mapping for these values: [entra-setup, where each value lands](entra-setup.md#where-each-value-lands).

## Step 1. Download and verify the package

1. Download `scom-mcp-v0.1.0-lab.zip` and its `.sha256` from the
   [latest release](https://github.com/AhmedElshawa/scom-mcp-server/releases/latest) into an empty folder.
2. Verify the download (PowerShell prints `True` when the hash matches):

   ```powershell
   (Get-Content .\scom-mcp-v0.1.0-lab.zip.sha256) -match (Get-FileHash .\scom-mcp-v0.1.0-lab.zip).Hash
   gh attestation verify .\scom-mcp-v0.1.0-lab.zip --repo AhmedElshawa/scom-mcp-server
   ```

3. Extract it and place the extracted folder at `C:\ScomMcp\package` (the folder that
   contains `setup\`, `server\`, `examples\` and `docs\`). This guide calls it `<pkg>`:

   ```powershell
   New-Item -ItemType Directory C:\ScomMcp -Force | Out-Null
   Expand-Archive .\scom-mcp-v0.1.0-lab.zip -DestinationPath C:\ScomMcp\_extract
   Move-Item (Get-ChildItem C:\ScomMcp\_extract -Directory).FullName C:\ScomMcp\package
   Remove-Item C:\ScomMcp\_extract
   ```

> **Note:** The package is unsigned. If your PowerShell execution policy requires signed
> scripts, see [local-evaluation-signing](local-evaluation-signing.md). Documentation on
> `main` may be newer than the ZIP; the code is identical.

At this point you should have: `<pkg>` with `setup\Start-ScomSetup.ps1` inside it.

## Step 2. Prepare the host: DNS, TLS, IIS, network

1. **DNS.** Create a record for `<FQDN>` and make sure the server itself resolves it
   (`Resolve-DnsName <FQDN>`). Step 7's Activate fails if it cannot, and the failure is hard
   to undo ([install-cli failure notes](install-cli.md#failure-notes)).
2. **TLS certificate.** Install a certificate whose subject or SAN is `<FQDN>` in
   `LocalMachine\My`, issued by a CA trusted by the server and by Copilot Studio. Record its
   thumbprint:

   ```powershell
   Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -like '*<FQDN>*' |
     Select-Object Subject, Thumbprint, NotAfter
   ```

3. **IIS and the Hosting Bundle.** Install the Web Server (IIS) role, then the .NET 8
   ASP.NET Core Hosting Bundle (in that order). Setup creates the site, pool and HTTPS
   binding for you in Step 7; do not create them by hand.
4. **Network.** Allow inbound TCP 443 from the Power Platform service tags
   `AzureConnectors` and `PowerPlatformPlugins` (use the regionless tags). Allow outbound
   443 to `login.microsoftonline.com` and the worker's path to the management server.
   Rule examples (NSG and Windows Firewall): [network-and-ports](network-and-ports.md).

📷 *Screenshot pending: IIS HTTPS binding; see [screenshots/README.md](screenshots/README.md).*

**Record these values:** `<FQDN>`, `<TLS_THUMBPRINT>` (40 hex characters, no spaces).

## Step 3. Prepare SCOM and the worker account

1. **Create the worker account** (`<WORKER_ACCOUNT>`): a domain user, not a SCOM
   administrator. Add it to the **Read-Only Operator** user role in SCOM, scoped to the
   monitored objects. On the collector host grant **Allow log on locally** and
   **Log on as a batch job**. The full rights table is in
   [service-accounts](service-accounts.md).
2. **Gather SCOM facts** on the collector host (console or PowerShell module installed):

   ```powershell
   Import-Module OperationsManager
   (Get-Module -ListAvailable OperationsManager | Select-Object -First 1).Path   # <OM_MODULE_PATH>
   (Get-SCOMManagementGroup).Id                                                  # <MG_ID>
   (Get-SCOMManagementServer | Select-Object -First 1).DisplayName               # <MGMT_SERVER_FQDN>
   (Get-SCOMGroup -DisplayName '<GROUP_DISPLAY_NAME>').Id                        # <GROUP_ID>
   ```

3. **Choose the scope.** Start with **group scope**: create or pick a SCOM group that
   contains only the Windows computers you want and use its ID as `<GROUP_ID>`. Whole
   management group is possible but validated on 3 servers only
   ([sizing-and-scope](sizing-and-scope.md)).

**Record these values:** `<WORKER_ACCOUNT>`, `<MGMT_SERVER_FQDN>`, `<MG_ID>`, `<GROUP_ID>`,
`<OM_MODULE_PATH>`.

## Step 4. Create the publisher certificate

The worker proves its identity to the receiver with a client-authentication certificate, not
a secret. Create it on the collector host (elevated Windows PowerShell 5.1). This is not the
TLS certificate and not a code-signing certificate.

```powershell
$subject    = 'CN=scom-mcp-publisher'
$publicFile = 'C:\ScomMcp\scom-mcp-publisher.cer'
$cert = New-SelfSignedCertificate -Type Custom -Subject $subject `
  -CertStoreLocation 'Cert:\LocalMachine\My' `
  -Provider 'Microsoft Software Key Storage Provider' -KeyAlgorithm RSA -KeyLength 2048 `
  -HashAlgorithm SHA256 -KeyUsage DigitalSignature -KeyExportPolicy NonExportable `
  -NotAfter (Get-Date).AddYears(1)
$cert | Select-Object Subject, Thumbprint, NotAfter
Export-Certificate -Cert $cert -FilePath $publicFile -Type CERT -NoClobber
```

Then give `<WORKER_ACCOUNT>` (from Step 3) **Read** on the certificate's private key
(Certificates MMC > Local Computer > Personal > the certificate > All Tasks > Manage Private
Keys). Notes on reuse, renewal and enterprise-CA alternatives:
[guided-setup](guided-setup.md#publisher-identity-and-certificate-reuse-or-create).

> **Warning:** Upload only the public `.cer` to Entra. Never export or upload a private key
> or PFX. The certificate expires after one year; plan renewal.

**Record these values:** `<CERT_THUMBPRINT>`, `<PUBLISHER_CER_PATH>`.

## Step 5. Create the three Entra app registrations

Choose one route; both produce the same result.

- **Route A (script, recommended).** From `<pkg>`, as an administrator signed in to
  Microsoft Graph PowerShell:

  ```powershell
  .\setup\Provision-ScomEntra.ps1 -TenantId <TENANT_ID> -NamePrefix scom-mcp `
    -PublicCertificatePath <PUBLISHER_CER_PATH> `
    -StateDirectory C:\ScomMcp\config -GrantPublisherRole
  ```

  Use `<PUBLISHER_CER_PATH>` (from Step 4). It creates or verifies all three registrations
  and writes a nonsecret `identity.json` into `C:\ScomMcp\config`; open that file to read the
  app IDs. Details and prerequisites: [entra-setup, Route A](entra-setup.md#route-a-script).
- **Route B (portal).** Follow [entra-setup, Route B](entra-setup.md#route-b-portal).
  Upload the `.cer` from `<PUBLISHER_CER_PATH>` (from Step 4) to the publisher app.

Either way you also need to:

1. **Create the reader client secret** through your approved credential process. Keep it
   for Step 8; do not write it to a file.
2. **Record the publisher's Enterprise application Object ID** (the service principal, not
   the app registration):
   `(Get-MgServicePrincipal -Filter "appId eq '<PUBLISHER_APP_ID>'").Id`
3. **Record the Object ID of each user** allowed to ask questions (Entra > Users).
4. **Grant admin consent** for the publisher's `Scom.Ingest` app role. The reader's
   delegated `Scom.Read` is consented as your tenant policy requires.

📷 *Screenshots pending: `entra-setup-api-expose.png`, `entra-setup-api-approles.png`,
`entra-setup-publisher-cert.png`, `entra-setup-publisher-permissions.png`,
`entra-setup-reader-permissions.png`; see [screenshots/README.md](screenshots/README.md).*

At this point you should have these values:

1. `<TENANT_ID>`
2. `<API_APP_ID>` (the API's Application ID URI is `api://<API_APP_ID>`)
3. `<PUBLISHER_APP_ID>`
4. `<PUBLISHER_SP_OBJECT_ID>`
5. `<READER_APP_ID>`
6. The reader client secret (keep it private; used in Step 8)
7. `<READER_USER_OBJECT_ID>` for each allowed user

Do not request Microsoft Graph permissions for any of these apps. If you do not have these
values, see [Find a value again](#find-a-value-again).

## Step 6. Write the configuration files

You now have everything needed to write four files. Start from the examples in
`<pkg>\examples\` and replace every `<PLACEHOLDER>`. Keep them in a staging folder (`C:\ScomMcp\config`, never the repository); Configure in
Step 7 copies them into the private folder.

### 6a. `identity.json`

Route A already wrote this. For Route B, copy
[`identity.example.json`](../examples/identity.example.json) and fill it:

| Field | Value |
|---|---|
| `tenantId` | `<TENANT_ID>` (from Step 5) |
| `apiClientId`, and `resource` as `api://<API_APP_ID>` | `<API_APP_ID>` (from Step 5) |
| `publisherClientId` | `<PUBLISHER_APP_ID>` (from Step 5) |
| `publisherObjectId` | `<PUBLISHER_SP_OBJECT_ID>` (from Step 5) |
| `readerClientId` | `<READER_APP_ID>` (from Step 5) |
| `certificateThumbprint` | `<CERT_THUMBPRINT>` (from Step 4) |

### 6b. `receiver.json`

Start from [`receiver.example.json`](../examples/receiver.example.json); field notes in
[`receiver.example.md`](../examples/receiver.example.md).

| Setting | Value |
|---|---|
| `Auth.TenantId` | `<TENANT_ID>` (from Step 5) |
| `Auth.Audiences` | `<API_APP_ID>` (from Step 5); verify it equals the `aud` of a real token |
| `Auth.Callers[].ObjectId` | `<READER_USER_OBJECT_ID>`, one entry per allowed user (from Step 5) |
| `ReadModel.PublisherObjectIds` | `<PUBLISHER_SP_OBJECT_ID>` (from Step 5) |
| `ReadModel.ManagementGroupId` / `GroupId` | `<MG_ID>` / `<GROUP_ID>` (from Step 3) |
| `AllowedHosts` | `<FQDN>` (from Step 2) |

### 6c. `worker.json`

Start from [`worker.example.json`](../examples/worker.example.json); field notes in
[`worker.example.md`](../examples/worker.example.md).

| Setting | Value |
|---|---|
| `identity.tenantId` | `<TENANT_ID>` (from Step 5) |
| `identity.clientId` | `<PUBLISHER_APP_ID>` (from Step 5) |
| `identity.resource` | `api://<API_APP_ID>` (from Step 5) |
| `identity.certificateThumbprint` | `<CERT_THUMBPRINT>` (from Step 4) |
| `publisher.endpoint` | `https://<FQDN>` (from Step 2) |
| `profile.configuration.managementServer`, `managementGroupId`, `groupId` | `<MGMT_SERVER_FQDN>`, `<MG_ID>`, `<GROUP_ID>` (from Step 3) |
| `profile.configuration.operationsManagerModule` | `<OM_MODULE_PATH>` (from Step 3); must equal the plan's value in 6d |
| `schedule.interval` | `01:00:00` (default). The receiver's `Source.FreshMinutes` must exceed it |
| `profile.configuration.windowsMapping` | Keep the example's candidates; verify them against your management packs |

Setup's Configure step supplies the script path and hash, state paths and some identity
fields while merging ([worker.example.md](../examples/worker.example.md)); still fill in the
values above so the file can be reviewed before you run it.

> **Note:** The verification flags in `worker.json` are operator attestations. Leave them
> `false` unless you have reviewed the evidence they attest
> ([worker.example.md](../examples/worker.example.md#verification-flags)).

### 6d. `setup-plan.json` (the plan input)

This tells setup where to install and what to bind. Save it as `C:\ScomMcp\plan-input.json`:

```json
{
  "schemaVersion": 1,
  "packagePath": "C:\\ScomMcp\\package",
  "installPath": "C:\\ScomMcp\\install",
  "privatePath": "C:\\ScomMcp\\private",
  "receiverStatePath": "C:\\ScomMcp\\receiver-state",
  "workerStatePath": "C:\\ScomMcp\\worker-state",
  "mcpLogPath": "C:\\ScomMcp\\logs\\mcp",
  "collectorLogPath": "C:\\ScomMcp\\logs\\collector",
  "siteName": "ScomMcp",
  "poolName": "ScomMcpPool",
  "applicationPath": "/",
  "httpsBaseUrl": "https://<FQDN>",
  "tlsThumbprint": "<TLS_THUMBPRINT>",
  "scopeKind": "group",
  "wholeManagementGroupApproved": false,
  "managementGroupId": "<MG_ID>",
  "groupId": "<GROUP_ID>",
  "managementServer": "<MGMT_SERVER_FQDN>",
  "workerAccount": "<WORKER_ACCOUNT>",
  "operationsManagerModule": "<OM_MODULE_PATH>"
}
```

| Field | Value or rule |
|---|---|
| `packagePath` | Where you placed `<pkg>` in Step 1 |
| `httpsBaseUrl` | `https://<FQDN>` (from Step 2); the path must equal `applicationPath` |
| `tlsThumbprint` | `<TLS_THUMBPRINT>` (from Step 2) |
| `managementGroupId`, `groupId`, `managementServer` | From Step 3 |
| `workerAccount` | `<WORKER_ACCOUNT>` (from Step 3); must not be `IIS APPPOOL\<poolName>` |
| `operationsManagerModule` | `<OM_MODULE_PATH>` (from Step 3) |
| Paths | Seven distinct folders on local NTFS, none inside another or under `%WINDIR%`; the parent of `installPath` must already exist |
| Whole-management-group scope | `scopeKind` = `managementGroup`, `wholeManagementGroupApproved` = `true`, `groupId` = `null`. Validated on 3 servers only |

At this point you should have `identity.json`, `receiver.json` and `worker.json` in
`C:\ScomMcp\config`, and `plan-input.json` in `C:\ScomMcp`. Reference for every setting:
[configuration-reference](configuration-reference.md).

## Step 7. Run setup: Prepare to Host Publish

Run these seven commands in order from `<pkg>`, in **Windows PowerShell 5.1**. Stop at the
first command that does not print its expected line. The **Host** lane is an elevated shell
as a local administrator; the **Worker** lane is a separate session running as
`<WORKER_ACCOUNT>` (from Step 3), which must be able to read `<private>\worker.json` (created by step 4).

In the commands below, `<state>` is a new state directory (for example `C:\ScomMcp\setup-state`),
`<plan-input>` is `C:\ScomMcp\plan-input.json` (from Step 6) and `<private>` is
the plan's `privatePath`, `C:\ScomMcp\private` (from Step 6).

| # | Lane | What it does | Uses values from |
|---|---|---|---|
| 1 | Host | Initialize the private journal | none |
| 2 | Host | **Prepare**: preflight checks, creates the plan | `plan-input.json` (Step 6) |
| 3 | Worker | **Connect**: checks the account can see SCOM | `<WORKER_ACCOUNT>` (Step 3) |
| 4 | Host | **Configure**: merges your three config files | `identity.json`, `receiver.json`, `worker.json` in `C:\ScomMcp\config` (Step 6) |
| 5 | Host | **Activate**: installs the site, binds TLS, checks HTTPS | `<FQDN>`, `<TLS_THUMBPRINT>` (Step 2) |
| 6 | Worker | **Publish**: one real SCOM collection to the receiver | `<CERT_THUMBPRINT>` (Step 4) |
| 7 | Host | **Publish**: confirms the receiver committed the dataset | none |

The exact command lines and expected output of each step are in the
[install-cli runbook](install-cli.md#commands); run them verbatim. Two things commonly go wrong:

- Step 5 fails if the server cannot resolve its own `<FQDN>` (Step 2, item 1).
- Step 6 needs a package at or after commit `55f3062` (this release includes it).

Setup never enables recurrence; that is Step 10.

📷 *Screenshot pending: `readme-wizard.png` (GUI alternative); see
[install-wizard](install-wizard.md) and [screenshots/README.md](screenshots/README.md).*

At this point you should have the receiver running at `https://<FQDN>` with one published
dataset, and setup at the `published` stage.

## Step 8. Connect Copilot Studio

Full field list: [copilot-studio-integration](copilot-studio-integration.md#1-create-the-custom-mcp-connector).
In the agent: enable generative orchestration, then **Tools > Add a tool > New tool >
Model Context Protocol** and fill in:

| Field | Value |
|---|---|
| Server URL | `https://<FQDN>/mcp` (from Step 2) |
| Transport | **Streamable HTTP** |
| Authentication | **OAuth 2.0, Manual** |
| Client ID | `<READER_APP_ID>` (from Step 5) |
| Client secret | The reader client secret (from Step 5); entered only here |
| Authorization URL | `https://login.microsoftonline.com/<TENANT_ID>/oauth2/v2.0/authorize` (`<TENANT_ID>` from Step 5) |
| Token URL and Refresh URL | `https://login.microsoftonline.com/<TENANT_ID>/oauth2/v2.0/token` |
| Scopes | `api://<API_APP_ID>/Scom.Read offline_access` (`<API_APP_ID>` from Step 5) |

1. Click **Create** and copy the **callback (redirect) URI** the connector shows
   (`<CALLBACK_URI>`).
2. In Entra, open the **reader** app (`<READER_APP_ID>`, from Step 5) > Authentication >
   add `<CALLBACK_URI>` as a **Web** redirect URI, exactly as shown. Save.
3. Back in Copilot Studio: **Next > Create new connection**, sign in as one of the users
   whose `<READER_USER_OBJECT_ID>` you allow-listed in Step 6, then **Add to agent**. Keep
   individual-user authentication.
4. Paste [`examples/agent-instructions.md`](../examples/agent-instructions.md) verbatim into the
   agent's Instructions.

📷 *Screenshots pending: `copilot-studio-connector.png`, `copilot-studio-connection.png`;
see [screenshots/README.md](screenshots/README.md).*

At this point the agent can call the server. If sign-in fails with a redirect mismatch,
`<CALLBACK_URI>` was not saved on the reader app exactly as shown; see
[Troubleshooting](#troubleshooting).

## Step 9. Acceptance checks and Accept

Setup's Accept records that you ran these checks; it does not run them. State which you
skip.

1. `GET https://<FQDN>/healthz` returns 200.
2. `POST https://<FQDN>/mcp` without a token returns 401.
3. In Copilot Studio, `get_source_status` returns the expected computer count with complete
   coverage.
4. The Operations console computer count matches the inventory.
5. `get_inventory_summary` counts and health match the console.
6. `get_device_details` works for each computer.
7. A signed-in user who is **not** in `Auth.Callers` gets 403.
8. A reader token against the ingestion endpoint gets 403.

Then run the Host **Accept** command (type `APPROVE`) from the
[install-cli runbook](install-cli.md#commands), step 8. Expected:
`SETUP_STAGE=accepted. No recurrence, metrics or targeted-live enabled.`

## Step 10. Register recurrence

Register the single scheduled task that refreshes the snapshot every hour. It runs as
`<WORKER_ACCOUNT>` (from Step 3) and uses the `worker.json` from Step 6; the command and its
expected output are in [operations, recurrence](operations.md#recurrence). The credential is
entered at a prompt; never pass a password as an argument.

Confirm with `get_source_status` after the first run: `collected_at` advances and freshness
is `fresh`.

## Step 11. Agent acceptance test

Ask the nine prompts in [copilot-studio-integration, Agent acceptance test](copilot-studio-integration.md#agent-acceptance-test)
and compare against the pass criteria. A transient "source unavailable" answer while a
collection commits is correct behaviour, not a failure.

📷 *Screenshot pending: `readme-agent-answer.png`; see [screenshots/README.md](screenshots/README.md).*

You are done. Day-two operation (logs, policy changes, recovery, lifecycle catalog):
[operations](operations.md).

## Find a value again

| Value | Where to find it |
|---|---|
| `<TENANT_ID>` | Entra admin center > Overview, or `identity.json` |
| `<API_APP_ID>`, `<PUBLISHER_APP_ID>`, `<READER_APP_ID>` | Entra > App registrations > the app > Overview (Application (client) ID), or `identity.json` |
| `<PUBLISHER_SP_OBJECT_ID>` | Entra > **Enterprise applications** > the publisher > Object ID (not the app registration's Object ID) |
| `<READER_USER_OBJECT_ID>` | Entra > Users > the user > Object ID |
| Reader client secret | Not recoverable; create a new secret on the reader app and update the connector |
| `<CERT_THUMBPRINT>`, `<TLS_THUMBPRINT>` | `Get-ChildItem Cert:\LocalMachine\My \| Select Subject, Thumbprint, NotAfter` |
| `<CALLBACK_URI>` | Copilot Studio > the connector > its settings after creation |
| `<MG_ID>`, `<GROUP_ID>`, `<MGMT_SERVER_FQDN>`, `<OM_MODULE_PATH>` | The Step 3 commands, or the Operations console |
| Anything already installed | `<private>\worker.json`, `identity.json` and the receiver's installed `appsettings.json` |

## Troubleshooting

Start with [troubleshooting](troubleshooting.md) (first five checks, log locations and
every error code). The most common first-time problems:

| Symptom | Likely cause | See |
|---|---|---|
| Activate fails or a retry is refused | Server cannot resolve its own `<FQDN>` | [install-cli failure notes](install-cli.md#failure-notes) |
| 401 on every tool call | Token `aud` does not match `Auth.Audiences` (Step 6b) | [troubleshooting](troubleshooting.md) |
| 403 for a legitimate user | Their `<READER_USER_OBJECT_ID>` is not in `Auth.Callers` | [troubleshooting](troubleshooting.md) |
| Ingestion rejected | `<PUBLISHER_SP_OBJECT_ID>` is the app registration's ID, not the Enterprise application's | [entra-setup](entra-setup.md#where-each-value-lands) |
| Connector cannot reach the server | NSG rule missing `AzureConnectors` / `PowerPlatformPlugins`, or TLS certificate not trusted by Copilot Studio | [network-and-ports](network-and-ports.md) |
| Answers say data is stale | Recurrence not registered (Step 10) or interval longer than the freshness budget | [operations](operations.md) |
