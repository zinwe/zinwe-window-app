# Windows agent architecture

## Shape

```
 Employee session (per user)            Session 0 (LocalSystem)                         Cloud
┌──────────────────────────┐   pipe   ┌───────────────────────────────────┐  HTTPS   ┌──────────────────────┐
│ Zinwe.Agent.Tray (WPF)   │◄────────►│ Zinwe.Agent.Service               │◄────────►│ Django z_devices     │
│ widget: connect, consent,│ "Zinwe.  │  AgentWorker: enroll→beat→policy  │ Device   │ enroll / heartbeat   │
│ work card, chat, notices,│  Agent"  │  EventUploader (30s, outbox)      │ token    │ policy/current       │
│ update card              │          │  UsbWatcherService (WMI)          │          │ events/upload        │
└──────────────────────────┘          │  PipeServer                       │          └──────────────────────┘
                                      │        │                          │
                                      │        ▼                          │
                                      │  DataDirectory: device.dat        │
                                      │   policy.json · events.db         │
                                      │  HKLM\…\RemovableStorageDevices   │
                                      └───────────────────────────────────┘
```

**Why two processes.** Windows services run in Session 0 and cannot show UI, read the foreground window, or raise toasts. All enforcement (it needs SYSTEM rights) lives in the service. Everything the employee sees lives in the tray, and the tray holds no secrets and makes no network calls.

**Why no kernel driver.** USB, install, network and remote-access control are all reachable through policy (registry/GPO, WDAC/AppLocker, Windows Filtering Platform in user mode). A driver would need an EV certificate plus Microsoft attestation signing, which means months of lead time and a crash risk on customer PCs.

## Enrollment & identity

1. The owner adds a member in the dashboard; the member gets an email with their **Member ID** (`ZM-XXXX-XXXX`) and the download link.
2. The member installs the MSI (double-click) and types their email + Member ID in the widget's **Connect this PC**. The widget hands them to the service over the pipe (`enroll`), and the service saves them as `pending-credential.json` in its data folder. IT tools can instead install silently with `msiexec … MEMBER_EMAIL=… MEMBER_ID=… /qn`, which writes them to `HKLM\SOFTWARE\Zinwe\Agent`.
3. The service posts `email`, `member_id`, `machine_id` (Windows `MachineGuid`), hostname, OS and agent version to `POST /api/devices/enroll/`, and gets a **device token**. The backend stores only its SHA-256.
4. The token is saved DPAPI-encrypted (`device.dat`, LocalSystem scope) in a ProgramData folder locked to SYSTEM and Administrators. The pending file and the registry copy of the credential are deleted.
5. A reinstall on the same PC re-uses the same `Device` row (unique on owner + machine_id) and rotates the token.
6. The member then reads the monitoring notice and agrees or declines (see the consent rule below); nothing is recorded before "I agree".

The device token works **only** on device endpoints (`DeviceTokenAuthentication` is not in DRF's default auth classes). A token stolen from a PC can't read the owner's account.

## State machine (`AgentWorker`)

```
 service start ──► apply cached policy.json (no network) ──► enrollment loop
                                                                │
   NotEnrolled ──ID typed──► Enrolling ──ok──► Connected ◄──► Offline (network errors, backoff)
        ▲                         │ refused        │  │ 401
        └── new Member ID ◄── Rejected ◄───────────┼──┘  (401 = removed: company rules cleared)
                                                   │ each successful heartbeat:
                                                   ▼ policy_version ≠ cached version?
                              GET policy/current/ → save policy.json → apply → queue usb.policy_applied
```

| State | Meaning | Leaves when |
|---|---|---|
| NotEnrolled | no identity, no usable Member ID | the member connects in the widget (or a silent-install credential appears) |
| Enrolling | POST /enroll in flight | 2xx → Connected; 400 on email / Member ID → Rejected; network → NotEnrolled |
| Connected | last heartbeat ok; policy synced after each beat | network error → Offline; 401 (heartbeat *or* policy fetch) → Rejected |
| Offline | backend unreachable; exponential backoff ≤ 5 min | next success |
| Rejected | Member ID refused, or the PC was removed by its owner (`removed`); identity wiped | the member connects with a (new) Member ID |

The USB mode in force is independent of this state. It is set at startup from the cache and changes only when a new policy arrives. It survives Offline and a refused Member ID, and the tray shows it in the **USB drives** row (`AgentStatus.policyVersion` / `usbMode`, optional pipe fields).

Rule: **a removed PC keeps nothing of its company's rules.** A 401 on the heartbeat means the owner removed this PC: `PolicySync.ClearForRemoval()` sets USB back to allowed, restarts every feature module with everything off (which also takes the website block list out of the hosts file), deletes `policy.json`, and `removed.json` remembers who removed it, so after a restart the widget still says "{Company} removed this PC from Zinwe. Monitoring has stopped." instead of "not set up". The next successful enrolment deletes the marker.

Rule: **consent is the member's choice.** The widget writes the member's answer (`consent.json`: given / declined + the notice version). The notice version is derived from what the policy switches on (`ConsentNotice.VersionFor`, e.g. `2026-10-17.atrw`), so the owner turning on something new (like file names) asks again, while turning things off doesn't. Until the answer is "given" for the notice in force, `ConsentGate` keeps the event queue and metrics store empty; a decline is reported on the heartbeat (`consent: {state: "declined", version}`) and the backend then sends a policy with monitoring off. Work hours and the chat keep working either way.

Rule: **losing the backend never turns protection off.** `PolicySync.ApplyCached()` runs before the first network call. A failed policy fetch keeps the current policy and is retried on the next heartbeat. A failed apply (registry error) is retried on the next heartbeat too, without a refetch.

Rule: **a used or refused Member ID is never reused.** On successful enroll, on refusal, and whenever the agent starts already enrolled, any credential lying around is consumed: the pending file and registry values are deleted and the Member ID is remembered in memory. Without this, a PC the owner removed would silently re-join on its next 401. A live test caught exactly that bug. A Member ID the member types again after a refusal gets a fresh attempt.

## The pipe (tray ↔ service, FIX-015)

One JSON line in, one JSON line out (`PipeProtocol`): `status`, `enroll`, `update`, `usage`. The pipe carries enrolment and "install this update", so both ends check who is on the other side (`Core/Ipc/PipePeers.cs`):

| Check | Rule |
|---|---|
| Pipe ACL (`PipeServer.CreateSecurity`) | SYSTEM full control; **INTERACTIVE** read/write only (no creating instances, no ACL changes); **NETWORK denied** (never reachable from another PC). A dev service under `dotnet run` also gives its own account full control of its dev pipe |
| Service → client | `GetNamedPipeClientProcessId` → the process image must be `<install folder>\tray\Zinwe.Agent.Tray.exe` (the sibling of the service's `service\` folder). Anything else is closed without a reply, for every request type, and logged once per program per 10 minutes |
| `enroll` | also refused while the PC is Connected / Offline / UpdateRequired ("This PC is already connected to Zinwe."), so a connected PC can't be re-pointed at another member |
| Client → service | `GetNamedPipeServerProcessId` must equal the process id the Service Control Manager reports for `ZinweAgent` (`QueryServiceStatusEx`). A program that took the pipe name while the service was stopped - even the real service exe started by a user - is treated as "Zinwe isn't running" and is never sent the email + Member ID |
| Dev builds (FR-05) | a copy running from a source tree (`…\src\Zinwe.Agent.Service\bin\…`) and its tray (`…\src\Zinwe.Agent.Tray\bin\…`) trust each other when they're in the **same** tree. The installed service runs from Program Files, so this never applies to it |

Mixed versions during an update: an older tray still running from the same install folder passes (same path); an older service started by the SCM passes the new tray's check (same rule); a refused client only sees the pipe close, which every tray build already shows as "Zinwe isn't running". Limit: a program running as the same Windows user can still inject code into that user's real tray; the check stops scripts, look-alike programs and pipe squatting, not a determined local attacker. `PipeProtocolTests` that touch the real pipe stay safe: the test process is neither the tray nor the service, so it is refused and never sends anything.

## Policy & USB enforcement

| Piece | Where | What it does |
|---|---|---|
| `AgentPolicy` / `UsbMode` | `Core/Policy/AgentPolicy.cs` | `{version, usb: {mode}}`; modes serialize as `allow` / `read_only` / `block`. `IsWellFormed` guards against null members and integer enums; a malformed policy is never cached or applied |
| `PolicyStore` | `Core/Policy/PolicyStore.cs` | `policy.json`, plain indented JSON, atomic temp+move write; corrupt → treated as "no cache" |
| `PolicySync` | `Core/Policy/PolicySync.cs` | startup apply; per-heartbeat version compare → fetch → save → apply → event |
| `UsbProtection` | `Core/Usb/UsbProtection.cs` | the USB module: applies the mode, turns arrivals into `usb.connected` / `usb.blocked`, never throws |
| `IUsbEnforcer` | `Core/Usb/UsbEnforcement.cs` | `RegistryUsbEnforcer` (real) / `DryRunUsbEnforcer` (logs only) |
| `RemovableStoragePolicy` | same file | the value map below; both enforcers use it, so a dry run logs exactly what the real one would write |
| `UsbEnforcerSelector` | same file | **the safety rule** |
| `HklmRegistryWriter` | `Service/Usb/` | the only code that writes HKLM policy; in the service project, so no test can construct it |
| `WmiUsbDeviceWatcher` | `Service/Usb/` | `__InstanceCreationEvent` for `Win32_DiskDrive` with `InterfaceType='USB'` (2s poll); best-effort drive-letter lookup |

**Registry map**, under `HKLM\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}` (the documented "Removable Disks: Deny read/write access" Group Policy values):

| Mode | Deny_Read | Deny_Write |
|---|---|---|
| allow | deleted | deleted |
| read_only | deleted | 1 (DWORD) |
| block | 1 | 1 |

Writes are ordered so a transition never passes through a looser state than either end (read_only sets Deny_Write before deleting Deny_Read). Windows applies the policy to newly attached drives, so a stick already plugged in may need a replug.

**Safety rule (hard requirement).** The registry enforcer is built **only** when the process is LocalSystem (`WindowsIdentity.GetCurrent().IsSystem`) **and** `Agent:Enforcement:DryRun` is false. Anything else (`dotnet run`, tests, a misconfigured box) gets `DryRunUsbEnforcer` plus a warning log, and every `usb.policy_applied` event carries `dry_run` accordingly. `appsettings.Development.json` sets `DryRun=true`. A `usb.blocked` event raised in dry-run mode also carries `"dry_run": true`, because the drive was not really blocked.

**Uninstall** removes that registry key (`RemoveRegistryKey … removeOnUninstall` in `Package.wxs`), so removing Zinwe never leaves USB locked. On a major upgrade, the new service re-applies `policy.json` at startup.

## Events

```
 UsbProtection / PolicySync ──Enqueue──► EventQueue (events.db, SQLite) ──► EventUploadPump ──► POST events/upload/
                                             cap 10,000 (oldest dropped,        ≤100 per batch, delete only after 2xx
                                             warning logged)                    driven by EventUploader: every 30s,
                                                                                and at once after usb.policy_applied
```

- **Ids** are client UUIDs, and the backend is idempotent on (device, id). A batch re-sent after a lost response comes back as `duplicates`, not errors.
- **Validation at enqueue** mirrors the backend: type `^[a-z_]+(\.[a-z_]+)+$`, severity info/warning/critical, data is a JSON object ≤ 4096 bytes, and string fields are clipped at 200 characters. `occurred_at` is local time with offset, in whole milliseconds.
- **Failures:** on a network error or 5xx the events stay queued. A 401 stops the uploader for that token, and the AgentWorker's heartbeat owns the "removed" state and wipes the identity. Uploads resume when the PC is enrolled again with a new token. On a 400 the batch is retried one event at a time, and only the events refused on their own are dropped, so one bad event can't wedge the queue.
- **SQLite** comes from `Microsoft.Data.Sqlite`, with the native `e_sqlite3.dll` shipped in the service folder. Core marks the package `PrivateAssets="all"`, so the tray doesn't carry it. Connections are unpooled (no lingering file handles), and one in-process lock serialises access.

## Files on disk (`Agent:DataDirectory`)

| File | Contents | Protection |
|---|---|---|
| `device.dat` | device id + token | DPAPI (LocalSystem) + folder ACL |
| `policy.json` | last policy (`version`, `usb.mode`) | folder ACL (no secrets) |
| `events.db` | unsent events | folder ACL |

## Updates (FIX-016)

The heartbeat offers `update {version, notes, required, download_url, sha256, size}`. On **Update now**, `AgentUpdater` (LocalSystem, enforcement on) downloads to `updates\ZinweAgent-<v>.msi.part` in the SYSTEM-only data folder and installs only if all of these pass, else it deletes the file, reports "failed" on the status and logs the reason:

1. size (when given) and **SHA-256** match the heartbeat;
2. **Authenticode** (`AuthenticodeSignatureCheck`, after the move to the final `.msi` name): `WinVerifyTrust` (GENERIC_VERIFY_V2, no UI, no revocation, cache-only chain building) must return `S_OK`, `CERT_E_UNTRUSTEDROOT` or `CERT_E_CHAINING`. Those results come only from the last (chain) step, which runs only after the file hash and the signer's signature were verified, so in all three the signature is intact. A changed file gives `TRUST_E_BAD_DIGEST`, no signature `TRUST_E_NOSIGNATURE`; those and every other result (explicitly distrusted, revoked, expired without timestamp, wrong usage) are refused;
3. the signer certificate that `WinVerifyTrust` verified (read from its own state data) has a SHA-1 thumbprint on `ZinweSigners.Thumbprints` - today the self-signed *Zinwe Dev (SKYTECHNO)* `6FFAC69585F869B2FEF41581A8AA78DF7BD418E2`. Pinning is what makes an untrusted chain acceptable: anyone can make a certificate called "Zinwe", nobody else can sign with ours.

Then `msiexec /i … /qn BACKEND_URL=<current>` runs as SYSTEM. Dev / dry-run agents download and verify (both checks) but never install. A new signing certificate must be added to the pin list in a release signed with an already-pinned one first.

## Build defaults (FIX-017)

`Directory.Build.props` `<Version>` is the version line (1.1.2); `build.ps1` uses it when `-Version` is not passed. The committed backend is `https://prod.backend.zinwe.io` (a live host); a signed build must name its server with `-BackendUrl`, so release MSIs never rely on the default.

## Gotchas already hit

- `JsonContent` streams with chunked encoding (no `Content-Length`). Django's WSGI server reads that as an **empty body**. `BackendClient` buffers every body (`JsonBody`), and tests pin it for enroll and for event upload.
- The host's content root defaults to the working directory, and the SCM starts services in `System32`. `Program.cs` pins it to `AppContext.BaseDirectory`.
- WinForms (for `NotifyIcon`) and WPF both define `Application`, `MessageBox`, etc. The tray csproj removes the WinForms/Drawing implicit usings and aliases them.
- `e_sqlite3.dll` has a file version but no language, which fails WiX's ICE60. WiX also refuses a made-up `DefaultLanguage` for it (WIX1101), so the wixproj suppresses ICE60.
- A `BackgroundService` that throws stops the whole host (.NET 8+ default). The uploader and the USB watcher catch everything, because losing either must never take enforcement down.

## Not done yet (in rough order)

Done since the first version of this list: the live loop, the data-transfer / remote-software / web / network modules, the consent screen and notices, one-click self-update (SHA-256 + pinned Authenticode), dev-certificate signing, and pipe peer checks on both ends.

1. OV/IV code-signing certificate (cloud HSM/token) + Microsoft Defender submission (launch blocker). Ship its thumbprint in `ZinweSigners` in a dev-cert-signed release before the first OV-only release.
2. Tamper protection: service DACL (no `sc stop` / `sc delete` for Administrators), uninstall password, and detecting someone deleting the Deny_* values (re-apply on a timer or on a registry change notification).
3. Software-install control beyond detection (AppLocker/WDAC).
4. Revocation checking for the OV certificate once it exists (today: no revocation check; the pin list is the control).
