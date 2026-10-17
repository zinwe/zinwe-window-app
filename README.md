# Zinwe Windows agent

The program that runs on every employee PC. It enrolls the PC into the business owner's Zinwe account, checks in with the backend, applies the owner's rules (first module: **USB storage allow / read-only / block**) and reports what happens to the dashboard's activity feed. Later modules: installs, remote access, network. The owner never uses this UI: they use the web dashboard. Employees see only a tray icon and a status card.

**Stack:** C# / .NET 10 (LTS) · Windows Service + WPF tray · WiX v6 MSI · self-contained x64 (no .NET install needed on the PC).

```
window-app/
├─ src/Zinwe.Agent.Core/      shared: backend client + contracts, device identity (DPAPI), pipe protocol,
│                             Policy/ (policy cache + sync), Events/ (SQLite queue + uploader), Usb/ (enforcers, safety rule)
├─ src/Zinwe.Agent.Service/   Windows service "ZinweAgent" (LocalSystem): enroll → heartbeat → policy sync,
│                             event uploader, WMI USB watcher, pipe server
├─ src/Zinwe.Agent.Tray/      per-user tray icon + status card (talks only to the local service)
├─ tests/Zinwe.Agent.Core.Tests/
├─ installer/                 WiX MSI (service, tray autostart, registry, locked ProgramData folder)
├─ build.ps1                  test → publish → MSI
└─ docs/architecture.md       why it's shaped like this
```

## Prerequisites

- .NET SDK 10 (`dotnet --list-sdks`). Installed per-user at `%LOCALAPPDATA%\Microsoft\dotnet` on the dev box.
- The Django backend running locally (`backend/zinwe-backend`, `manage.py serve` on 127.0.0.1:8000, or `.cloude/start-local-env.sh --backend-only`).
- No separate WiX install needed: the WiX SDK comes from NuGet.

## Run it locally (no install, no admin)

1. Get an enrollment key for a local owner account:

   ```powershell
   cd ..\backend\zinwe-backend
   .venv\Scripts\python.exe -c "import os,django; os.environ.setdefault('DJANGO_SETTINGS_MODULE','config.settings'); django.setup(); from z_auth.models import User; from z_devices.services import get_active_key; u,_=User.objects.get_or_create(email='agent-dev@zinwe.local'); print(get_active_key(u).key)"
   ```

2. Run the service as a console app (dev settings → `http://127.0.0.1:8000`, state in `%LOCALAPPDATA%\Zinwe\Agent-dev`):

   ```powershell
   $env:DOTNET_ENVIRONMENT = "Development"
   $env:ZINWE_Agent__EnrollmentKey = "ZNW-XXXX-XXXX-XXXX"
   dotnet run --project src\Zinwe.Agent.Service
   ```

3. Run the tray (`--show` opens the status card straight away):

   ```powershell
   dotnet run --project src\Zinwe.Agent.Tray -- --show
   ```

To see the "removed" path, remove the person in the dashboard (or revoke the device in Django admin). The next heartbeat gets a 401: the agent wipes its identity, **clears the company's rules** (USB allowed, website block list removed, every feature off, `policy.json` deleted) and the widget says "{Company} removed this PC from Zinwe. Monitoring has stopped." until it is connected again with a new Member ID.

**USB rules in dev are always a dry run.** `appsettings.Development.json` sets `Agent:Enforcement:DryRun=true`, and on top of that the registry is only ever written when the process is LocalSystem. Under `dotnet run` you'll see log lines like `[dry run] USB block: would set Deny_Read=1, set Deny_Write=1 …` and the `usb.policy_applied` event carries `"dry_run": true` (the feed shows "(test mode)"). To try it:

1. Change the rule in the dashboard (`/app/rules`) or with `PUT /api/devices/policy/`.
2. Within one heartbeat (about 60s) the log shows `Policy v2 received (USB block)`, `policy.json` in the data folder updates, and the tray card's **USB drives** row changes.
3. Plug in a USB stick: a `usb.connected` event (or `usb.blocked` in block mode) is queued in `events.db` and uploaded within 30s.

Local state lives in `%LOCALAPPDATA%\Zinwe\Agent-dev`: `device.dat` (DPAPI), `policy.json` (plain JSON), `events.db` (SQLite outbox; open with any SQLite browser).

Real enforcement can only be tested with the installed service on a **test VM** (see Install). After setting a rule, unplug and replug the stick: Windows applies Removable Storage Access to newly attached drives.

## Test & build

```powershell
dotnet test Zinwe.Agent.slnx     # unit tests
.\build.ps1                      # tests + publish + artifacts\installer\ZinweAgent.msi
.\build.ps1 -Version 0.2.0
```

Warnings are errors (`Directory.Build.props`).

## Install (test VM first; needs admin)

```powershell
msiexec /i artifacts\installer\ZinweAgent.msi ENROLLMENT_KEY=ZNW-XXXX-XXXX-XXXX /qn /l*v install.log
# optional: BACKEND_URL=https://staging.example   (production default is in appsettings.json)
sc query ZinweAgent
msiexec /x artifacts\installer\ZinweAgent.msi /qn
```

Uninstall also deletes the USB policy key (`HKLM\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-…}`), so removing Zinwe never leaves a PC's USB drives locked. Check on the VM after an uninstall with `reg query "HKLM\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices"`.

## What the employee sees

- **Finding it:** "Zinwe" in Start (opens the widget), a single click on the tray icon, and an entry in Settings ▸ Apps ("Zinwe (company data protection)", publisher "SKYTECHNO (Zinwe)"; Uninstall is disabled). The double-click installer shows a short welcome (what Zinwe is, that Windows will ask for an administrator's permission) and opens the widget at the end.
- **Opens by itself** once per sign-in while something waits: not connected yet, the monitoring notice not answered (plus an hourly reminder toast), or the work day not started.
- **Consent is a choice:** the notice names the employer (`company_name` from the backend, else "your employer"), lists only what the policy switches on, and offers **I agree / I don't agree**. Declining keeps work hours and the chat; nothing is recorded. "What Zinwe records on this PC" (widget + tray menu) re-reads it and withdraws or agrees later.
- **Clear states:** a status line under the logo ("Connected to …", "Offline — your times are saved and will sync", "Zinwe isn't running", "Removed from …"); no-internet during Connect says so and lets the member retry; a required update never pauses the work clock; a blocked website posts "Zinwe blocked {site} — your company's rule".
- **Text** lives in `src/Zinwe.Agent.Tray/Resources/Strings.resx` (one property per entry in `Strings.cs`), so a `Strings.hi.resx` can be added without code changes. The installer's text is in `installer/Package.en-us.wxl`.

## Configuration

| Setting | Where | Notes |
|---|---|---|
| `Agent:BackendBaseUrl` | `appsettings.json` → registry `BackendBaseUrl` → env `ZINWE_Agent__BackendBaseUrl` | Must be `https`, except loopback |
| Enrollment key | registry `HKLM\SOFTWARE\Zinwe\Agent\EnrollmentKey` (MSI), or `Agent:EnrollmentKey` (dev) | Deleted by the service after use |
| `Agent:HeartbeatSeconds` | config | Initial cadence only; the backend's `next_heartbeat_seconds` wins |
| `Agent:DataDirectory` | config | Default `%ProgramData%\Zinwe\Agent`: `device.dat` (DPAPI), `policy.json`, `events.db` |
| `Agent:Enforcement:DryRun` | `appsettings.json` (false) / `appsettings.Development.json` (true) | true = log USB changes, never write them. Real enforcement **also** requires running as LocalSystem |

## Backend contract (`backend/zinwe-backend/z_devices`)

| Call | Auth | Result |
|---|---|---|
| `POST /api/devices/enroll/` `{enrollment_key, machine_id, hostname, os_version, agent_version, platform}` | none (throttled 60/h/IP) | 201 new / 200 re-enrolled → `{device_id, device_token, re_enrolled}`; 400 `enrollment_key` = refused |
| `POST /api/devices/heartbeat/` (+ `consent: {state: given\|declined, version}` once the member answered) | `Authorization: Device <token>` | `{device_id, server_time, next_heartbeat_seconds, policy_version}` + optional `consent_state`, `usb_restore` (restore USB to the policy, send `usb.restored`), `company_name`, `owner_name`; 401 = removed (rules cleared) |
| `GET /api/devices/policy/current/` | `Device` | `{version, usb: {mode: allow\|read_only\|block}}`; fetched when `policy_version` changes |
| `POST /api/devices/events/upload/` `{events: [{id, type, occurred_at, severity, data}]}` | `Device` | 1..100 per call, idempotent on `id` → `{accepted, duplicates}`; 400 = content refused |
| `GET /api/devices/`, `GET /api/devices/enrollment-key/`, `POST …/rotate/` | owner `Token` | dashboard |

## Code signing (launch blocker)

The MSI and both exes are **unsigned**. Before any customer install you need an OV code-signing certificate (or Azure Trusted Signing, if eligible in India) and a Microsoft Defender false-positive submission. An unsigned "monitoring agent" will be flagged by SmartScreen/Defender.
