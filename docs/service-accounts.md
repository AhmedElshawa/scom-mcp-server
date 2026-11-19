# Service accounts and permissions

Two runtime identities, deliberately separate. Neither is a SCOM administrator.

## Worker account (collector)

A **domain user** you create for this purpose (`<WORKER_ACCOUNT>`). Built-in
`SYSTEM`, `LocalService` and `NetworkService` are rejected by the task registration.

| Principal | Location | Right |
|---|---|---|
| `<WORKER_ACCOUNT>` | SCOM management group | Member of the **Read-Only Operator** user role, scoped to the monitored objects |
| `<WORKER_ACCOUNT>` | Collector host, local security policy | **Allow log on locally** (the Windows PowerShell profile child runs as this account) |
| `<WORKER_ACCOUNT>` | Collector host, local security policy | **Log on as a batch job** (required to register and run the scheduled task) |
| `<WORKER_ACCOUNT>` | Installed package directory | Read & Execute |
| `<WORKER_ACCOUNT>` | Private worker configuration (`worker.json`) | Read |
| `<WORKER_ACCOUNT>` | Checkpoint directory | Modify |
| `<WORKER_ACCOUNT>` | Collector log and runtime-state directories | Modify (new, empty, local NTFS) |
| `<WORKER_ACCOUNT>` | Publisher certificate private key (`LocalMachine\My`) | Read (Certificates MMC > All Tasks > Manage Private Keys) |

The worker authenticates to SCOM with its own Windows identity; no SCOM password is
stored in configuration. It authenticates to the receiver with the certificate, not a secret.

## IIS application pool identity

`IIS AppPool\<POOL_NAME>` (`ApplicationPoolIdentity`, No Managed Code, Integrated pipeline).

| Principal | Location | Right |
|---|---|---|
| `IIS AppPool\<POOL_NAME>` | Application directory | Read & Execute |
| `IIS AppPool\<POOL_NAME>` | Read-model database directory | Modify |
| `IIS AppPool\<POOL_NAME>` | Receiver log directory | Modify |

## What NOT to grant

- SCOM Administrator or any role broader than Read-Only Operator.
- The IIS pool access to the worker's checkpoint, private configuration or the
  publisher certificate's private key; never run the pool as the SCOM account.
- The worker write access to the package or configuration directories.
- Broad groups (Users, Everyone, Authenticated Users) on any private directory.
- Interactive/RDP logon for either account, or Domain Admin membership.
- Microsoft Graph permissions to either Entra identity.

Directory ACLs should retain SYSTEM and Administrators and disable inheritance;
the guided setup does this and verifies owner and rules. Administrators of the
machine can still read logs and configuration. Task registration:
[operations.md](operations.md).
