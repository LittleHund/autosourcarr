# TrueNAS deployment

## Upgrading to 0.3.42 — installed Health and procfs diagnosis

Preserve destination env files, both app databases, source profiles, manual choices,
signing identity and repair workspace. Overlay the complete archive, rebuild the
existing image and refresh to load 0.3.42 assets (extension fallback 1.4.3042).
Health now displays installed sources only and keeps removed-source history.
Use Tasks → Repairs directly; General Tasks no longer has an Open Repairs button.

The new reported mount denial occurs with `autosourcarr-internal (enforce)` active,
so reloading that profile alone does not address the observed procfs step. For
this exact failure, add `- systempaths=unconfined` to the existing shared
AutoSourcarr `security_opt` list in the saved Custom App Compose, retain the other
policies, then Save/Redeploy. This removes Docker's default proc/sys masks;
the [host guide](security/repair-host-setup.md#private-procfs-mount-denied-with-the-repair-profile-active)
explains scope and the credential-free inspect check. Run Tasks → Repairs → Check
repair tools after recreation; only a passing full probe establishes readiness.
The supplied log is consistent with proc masks but does not independently prove
the denying layer. No live host changes or successful source repair are claimed.

## Upgrading to 0.3.41 — source health and repair workspace

Keep the destination's active env files, both app databases, source profiles,
manual choices, signing identity and repair workspace. Overlay the complete
archive, rebuild the existing image and refresh to load 0.3.41 assets. The existing
catalog database gains a small source-health table without resetting its data.
The Codex knowledge database stays outside the app and is not a runtime dependency.

Source health checks are available in Sources → Health, with on-demand and daily
sampling. Daily checks are enabled as requested, yield to browser activity and can
be disabled in that panel. Tasks → General shows their progress and cancellation.
No AI inference, helper service, Docker socket or extra Compose setting is needed.
The last repair date comes from confirmed installed-job evidence or the parent's
successful installation receipt; ready jobs and unknown older dates remain distinct.

Tasks → Repairs contains all repair forms, readiness checks, jobs and evidence.
Sources → Repair AI is now limited to the AI provider and account controls.

For the reported `apparmor failed to apply profile: write ... no such file` startup
error, remove the explicit `apparmor=autosourcarr-internal` entry to restore Docker's
normal profile selection, retain the supplied seccomp policy and Save/Redeploy.
The user reports startup is restored. The write error alone does not prove AppArmor
is absent: an unloaded profile can also cause it. Before enabling the override again,
verify load exit code 0 and its entry in the host's registered profile list; the
[host guide](security/repair-host-setup.md) includes exact commands. Sandbox preflight
remains mandatory for source execution. No live deployment is performed by preparing
this archive; actual Linux/image/browser/source checks remain separate gates.

## Upgrading to 0.3.40 — repair Tasks and host readiness

Retain the destination's active env files, both app databases, source profiles,
manual decisions, signing identity, uploaded APKs and repair workspace. Overlay
the complete archive, rebuild the existing image with Save/Redeploy, then refresh
the browser and confirm 0.3.40 in Status. This source update adds no host settings
or services. Preparation of the archive does not deploy or modify the live host.

Repair jobs, review/install/discard actions and **Evidence & session** are now in
**Tasks**. Use its **Repair tools** button for the existing account/setup controls.
Finished repair history can be hidden until restart; on-disk evidence is retained.
Running jobs and candidates awaiting installation review survive Clear history.

The supplied 0.3.39 log confirms a namespace denial on fresh beehentai repairs.
The nhentai manifest rejection is an older retained attempt. The log did not include
the exact sandbox stderr or host checks, so it cannot identify the denying layer.
This release retains those safely in future diagnostics. The working ChatGPT login
is preserved; it does not establish sandbox availability.

Run **Tasks → Repair tools → Check repair tools**. If denied, expand the reported
host checks and use [the supplied host-permission instructions](security/repair-host-setup.md).
Only Docker/the host can grant these inherited kernel/container permissions. Keep
all existing settings, apply the supplied policy block only if required by that
host, and recreate the affected containers after a host-policy change. Seccomp=2
does not identify the loaded policy; a positive namespace limit is not proof of
permission. Do not start another repair until the full check passes. Existing
blocked jobs are not retried automatically. Successful Linux/image/live repair
and rendered-browser checks remain required on the intended deployment.

## Upgrading to 0.3.39 — orphan repair input and evidence

Retain the destination's active env files, both app databases, source profiles,
manual choices, signing identity and `/data/source-maintenance`, including uploaded
APKs and backups. Overlay the complete archive and rebuild the existing image;
confirm 0.3.39 in Status. No new Compose options, mounts or services are required.
Preparing the archive does not deploy, reset data or perform personal sign-in.

The user confirmed 0.3.38 ChatGPT sign-in succeeds. Its account resource policy is
preserved. Start a new **Orphaned sources → Check and repair** job. An APK-derived
installed JAR can lack its loading manifest; this update can use the retained
original APK to prepare a validation copy after an isolated conversion matches the
saved bytecode/assets. The raw rollback backup stays unchanged. Loading metadata
is attributed to its installation receipt or your explicit original-file confirmation.
Existing package/class/library/source/website and sandbox checks still apply.

If the original APK is unavailable, use **Sources → Source repairs → Start a source
repair → Original APK for repair**. Select the installed source, attach that exact
version's original APK and confirm it is the original file; then start a new repair.
Attaching evidence keeps the installed source and preferences in place. A mismatching
APK or conversion stays blocked. Review successful candidates before installing them.

For another failure, open **Evidence & session** and download diagnostics from Status.
The export now includes observed job state/history and orphan preparation errors,
with private prompts, conversations, patches and package bodies excluded. It uses
cached responses and local logs instead of triggering extra remote work. Run the
required Linux suite and project checker before deployment; actual image/conversion,
rendered browser and live source repair remain separate unavailable local gates.

## Upgrading to 0.3.38 — account launch limits and retained signing identity

Overlay the complete project archive while retaining the destination's active
env files, both application databases, source profiles, manual choices and
`/config/.extension-signing`. Rebuild the existing AutoSourcarr image and confirm
0.3.38 in Status. No additional Compose options, mounts or services are required.
The archive retains this workspace's active env files; do not overwrite a different
instance's personal configuration while copying it. Preparing this archive does
not deploy, reset data or sign in to an account.

In the Web UI, retry **Sign in with ChatGPT**. The account launcher now inherits
the container's process limit, avoiding its old shared-user 1024 limit, and defaults
to two Tokio workers. Repair jobs keep their existing isolation and resource policy.
If startup still fails, expand **Account error details** and download diagnostic
logs from Status. Validated numeric limits and visible cgroup counters help identify
the remaining constraint; unavailable or ancestor limits are reported as unknown.

Reader builds retain an existing signing pair and publish a new pair only after
successful generation. Incomplete pre-existing signing files stop the build; restore
both files from the same backup rather than deleting them or creating a new identity.
Signer failures now preserve their original exit status. APK/JAR compilation and
live ChatGPT sign-in must be verified in the Linux image; local fixtures cannot
certify those outcomes. Run the required backend suite and project checker there
before deployment. The manual-reset confirmation remains the working 0.3.37 flow.

## Upgrading to 0.3.37 — reset confirmation and account startup

Run the required Linux test suite and `bash scripts/check-project.sh` before
deployment; the complete Linux/image/browser gates remain unverified here. Overlay
the complete archive and rebuild the existing AutoSourcarr image, then confirm
0.3.37 in Status. Preserve both app databases, existing env files, source profiles,
manual choices and signing identity. No new Compose options, mounts or services.

The image now validates and links the bundled native Codex account executable at
build time. In the Web UI, retry **Sign in with ChatGPT**. If startup still fails,
expand **Account error details** and download diagnostic logs from Status; local
supervisor failures now include a safe stage and OS error number. The supplied
exit-70 record does not prove an OpenAI account-policy or network failure. This
update does not establish successful live sign-in or remove source-repair isolation.

Reset all manual actions now keeps **RESET ALL MANUAL ACTIONS** visible below a
scrollable preview. Type the phrase to enable Reset; no shell command is needed.
Reset is an explicit user action and is not part of this upgrade.

## Upgrading to 0.3.36 — diagnostic logs and continued fixes

Run the required Linux backend suite and Bash project checker before deployment;
this Windows workspace cannot certify the complete release gate. Then overlay the
complete archive, rebuild the existing image and confirm 0.3.36 in Status. Retain
both app databases, env files, signing identity, source profiles and manual choices.
No Compose fields, mounts or additional services are required.

After startup, **Status → Download diagnostic logs** produces one redacted text file
with app, integrated source-engine and helper output plus local diagnostic context.
It includes compiler failure details previously discarded. Three bounded files in
the existing data directory survive restarts; the download describes retention and
unavailable sections. Raw configuration, databases, cookies and account/signing
files are excluded. Host/proxy/other-container logs are not collected.

Saved source repositories can be applied with **Sources → Repositories → Apply
saved repositories**, which restarts only this instance's source process and keeps
its sign-ins and library data. This update does not resolve the reported host
namespace blocker or establish a successful live provider sign-in.

## Upgrading to 0.3.35 — source candidate pending the Linux gate

This update fixes cached-feed writer contention, early-startup cleanup, task
diagnostics and bounded tag-suggestion selection. It changes no Compose fields,
mounts or source isolation requirements. The development knowledge database is
for Codex; it is outside the image build context and is never an app database.

Run the required backend suite and `bash scripts/check-project.sh` on Linux with
the pinned test dependencies before deployment. Native Windows verification is
partial; CHANGELOG.md retains the failed/unavailable checks. After those gates
pass, overlay the complete archive, rebuild your existing image and confirm
0.3.35. Preserve existing env files, both app databases, accounts, source profiles,
presets and signing keys. No deployment or live login was performed here; the
reported no-Compose source-repair host blocker remains open.

## Upgrading to 0.3.34 — identify the immediate sign-in failure

Overlay the full archive and rebuild/redeploy your existing AutoSourcarr image.
Refresh and confirm 0.3.34 in Status. Leave your Compose, ports, mounts, env files,
databases, source profiles, presets, credentials and signing keys unchanged.

Select Sign in with ChatGPT in setup or Source repairs. A failed code request now
opens Account error details with Copy error details. It identifies the observed
stage, exit/HTTP status and a safe category. Unknown errors no longer tell you to
change ChatGPT settings. HTTP 401/403 cannot distinguish OpenAI from a filtering
network gateway; HTTP 404 is a missing endpoint, not proof of an account policy.
Explicit disabled-login errors still explain the personal/workspace setting.

The controller can read only bounded regular logs in the current private account
operation, without symlink/FIFO access. It classifies them without exporting any
text or credentials; cancellation drains this read before releasing the account
lock. Account changes, previous saved login and source repair isolation are retained.
No new host step, Compose option, helper service, permission grant or TLS bypass.

The user's exact server failure still requires its new observed details. Real
TrueNAS, real login and image Node22/Codex0.157.0 are unverified here. Controlled
real Node24/Codex0.159.2 fixtures cover code, denial/malformed-response and TLS
failures through the production supervisor with loopback-only mock transport.
They do not resolve the no-Compose requirement for executing source repairs.

## Upgrading to 0.3.33 — sign in without changing Compose

Overlay the complete archive and rebuild/redeploy the existing AutoSourcarr image.
Refresh and confirm 0.3.33 in Status. Keep your current Compose, ports, mounts,
env files, databases, source profiles, presets, accounts and signing keys.

ChatGPT Sign in / Check saved login / Sign out no longer require Bubblewrap or
Check repair tools. They run a fixed trusted bundled account command in a fresh
private home, separate from untrusted source code. API connection checks for all
providers remain available too. The existing Web UI opens the official sign-in
page and displays OpenAI's one-time code, or a bounded actionable account error.
Enable device-code login in ChatGPT security settings if your account requires it.

The current host namespace denial still blocks source repair execution with every
provider. This update removes the unnecessary account prerequisite; **it does not
resolve the user's no-Compose requirement for source execution on this host**.
No extra Compose option or helper service is introduced. Optional host policy
instructions below concern running edited source code only. They can be left
alone while connecting accounts and using normal library features.

Research rejected TCG emulation alone as a security boundary: QEMU's official
security model excludes it from guest-isolation guarantees. A confined internal
VM remains a possible future design, requiring host-supported confinement and
actual guest/toolchain/negative isolation tests. Native Landlock also depends on
host kernel/LSM support; rootless gVisor uses a user namespace. No unverified
fallback is shipped. Sources: [QEMU security](https://www.qemu.org/docs/master/system/security.html),
[Landlock](https://docs.kernel.org/userspace-api/landlock.html),
[gVisor rootless](https://gvisor.dev/docs/user_guide/rootless/).

Controlled tests verify account lifecycle and credentials, not a live OpenAI login
or TrueNAS deployment. The executor's /proc mount reports a different PID namespace
from getpid, so account fixtures replace sampled /proc accounting while retaining
actual supervision, process limits and cleanup receipts. See CHANGELOG.md.


## Upgrading to 0.3.32 — resolve the reported sandbox check and test every AI provider

Overlay the full source archive, rebuild/redeploy your existing image, refresh and
confirm 0.3.32 in Status. Keep your actual mounts, ports, env files, databases,
source profiles, credentials, editable presets and reader signing keys.

The reported NoNewPrivs=1 confirms the internal startup protection is present.
The literal “No permissions to create new namespace” message is now correctly
classified as namespace-denied. It occurs before any AI repair tool starts,
including ChatGPT device authentication. This affects all providers. The report
also shows docker-default AppArmor, so the supplied repair profile is not selected.
Seccomp=2 cannot establish which seccomp file Docker applied; a positive namespace
limit cannot establish permission. The exact denying host layer remains unproven.

In Sources → Source repairs → Setup and account, run **Check repair tools**. The
host denial opens its guidance automatically. **Download host policies and
instructions** supplies the exact reviewed AppArmor/seccomp files from the image,
their license and a README with the host load command and one shared Compose block.
The same guidance is in [security/repair-host-setup.md](security/repair-host-setup.md).
Load the profile in the TrueNAS host Shell, then select the supplied policies and
Save/Redeploy to recreate the app containers. Explicit service-level security_opt
must not override the shared block. In an existing merge list put the security
alias first: `<<: [*autosourcarr-security, *existing-defaults]`; earlier mappings
win. Consolidate conflicting options and check the effective service settings.
The AppArmor load/selection applies to an AppArmor-enabled host, confirmed by the
reported docker-default profile. No extra permanent service is needed.

Recheck after redeployment. If creation is still denied, verify the applied Docker
security options and correlate the new check with the host kernel/AppArmor denial
log. Diagnostics include optional AppArmor restriction sysctls when exposed. A
missing value is not reported, not disabled. Forced user-namespace mediation or a
host policy ABI override is a conditional additional cause, not established by this
report. The supplied ABI 3 profile is retained for Docker Unix-socket compatibility.
The app cannot load host policy or relax inherited restrictions from inside itself.

For API providers, **Test AI connection** remains separate from the sandbox check.
It makes at most two small model requests, validates an inert function/tool result
round trip and changes no source files. Fix billing, API-key, model, permission or
quota errors according to the specific message. A successful API test is not a
successful repair check. ChatGPT still uses the official device sign-in after
isolation is available; enable device-code login in the account/workspace settings.

Setup supports all seven choices. Claude multi-workspace keys can now specify
their Workspace ID; a workspace-scoped key can leave it empty. OpenRouter requires
tool parameter support internally. For Ollama, enter an existing model server's
LAN/service URL, such as http://MODEL-SERVER-IP:11434/v1. The server must listen on
that reachable interface; localhost refers to this container and the default local
Ollama listener is loopback. host.docker.internal is not an automatic Linux Engine
hostname. No mandatory extra_hosts key, helper container or guessed IP is added.

Official host/provider research and controlled fixtures support these changes;
live host policy loading, namespaces and provider accounts remain unverified here.
The existing minimal host policy example below still applies. See CHANGELOG.md.

## Upgrading to 0.3.31 — internal defaults and shared host security

Overlay the full source archive and rebuild/redeploy the existing image using its
normal start command. Refresh and confirm 0.3.31 in Status. Keep actual config,
data, source mounts, ports, env files, presets, credentials and signing keys.
Internalizing every feature wherever technically possible is a permanent user
requirement. No new services, mounts, packages or environment fields are needed.

The integrated image now supplies the default UID/GID 568 user. Its normal startup
enables no-new-privileges before Python, inherited by its threads and managed
children; failure stops startup. Existing explicit user/no-new-privileges settings
remain compatible and may be kept. If your deployment uses a different intentional
non-root user, retain that override. The supplied Compose templates omit the two
redundant options and share the remaining host security keys in one block.

**For the reported bubblewrap namespace denial:** this internal startup change
does not grant the host's namespace permissions. Docker must apply the supplied
seccomp policy, and your AppArmor-enabled host must load/select the supplied
profile. In the TrueNAS **host Shell**, first run:

```bash
sudo apparmor_parser -r /mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/project/security/autosourcarr-internal.apparmor
```

Only after that succeeds, merge this shared block into the existing Compose:

```yaml
x-autosourcarr-security: &autosourcarr-security
  cap_drop: [ALL]
  security_opt:
    - seccomp=/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/project/security/repair-seccomp.json
    - apparmor=autosourcarr-internal

services:
  autosourcarr:
    <<: *autosourcarr-security
    # Keep this service's existing image/build, ports, mounts, env files, etc.
  private-autosourcarr:
    <<: *autosourcarr-security
    # Keep this instance's existing settings; omit if you only have one instance.
```

This is a merge example, not a complete replacement deployment. Consolidate the
existing cap_drop/security_opt keys into the shared block, retaining any extra
security options. Explicit service-level keys override YAML defaults, so old
security_opt blocks must not mask the selected profile. If a service already has
a merge list, put this alias first: `<<: [*autosourcarr-security, *existing-defaults]`.
Earlier mappings win; consolidate conflicting security options. Apply only to AutoSourcarr;
retain the main Suwayomi configuration. The shipped templates leave the AppArmor
line commented until the host profile has been loaded.

Save/Redeploy to recreate affected containers. Run Check repair tools again;
NoNewPrivs should be `1` and the selected AppArmor profile autosourcarr-internal
(enforce). Retry sign-in after isolation is available. A host administrator must
also reload the AppArmor profile after a host reboot, using the supported boot
configuration. The app cannot safely grant outer kernel permissions to itself.
No privileged/unconfined fallback, SYS_ADMIN or Docker socket is used.

The local runtime already had NoNewPrivs=1. Real setpriv checks establish exec/PID
ownership, descendant inheritance and fail-closed startup; a fresh 0→1 transition,
Docker image boot, host profile load, namespaces and real OpenAI login were not
performed here. See CHANGELOG.md for the complete gate result.

## Upgrading to 0.3.30 — retain sign-in errors and stop stalled code requests

Overlay the full source archive, rebuild/redeploy the existing image, refresh the
browser and confirm 0.3.30 in Status. Retain env files, runtime databases, source
profiles, saved credentials, signing keys and builder locks. No Compose change is
required; this source update does not deploy or reset a running instance.

Sign in with ChatGPT opens the official OpenAI page. Return to AutoSourcarr to
copy the code. Source repairs now retains a failed sign-in POST even if the popup
hid the app before its response arrived. Both worker modes stop waiting for an
initial code after 45 seconds; the status explains the failure instead of waiting
for the full authorization deadline. A code that arrives keeps its normal wait.
If the problem continues, report the exact account-status text and Check repair
tools result. Keep credentials and login codes out of shared screenshots/logs.

Local regression and controlled native mock checks cover these changes. Actual
OpenAI sign-in and Docker/TrueNAS image deployment remain unverified.

## Upgrading to 0.3.29 — saved preset editor and device-code recovery

Overlay the full source archive, rebuild/redeploy the existing AutoSourcarr image
and refresh the browser; confirm 0.3.29 in Status. Retain env files, both runtime
databases, source profiles, saved AI credentials, reader signing keys and builder
selection locks. The existing services and mounts remain sufficient.

Use Sources → Automatic matching rules → Create and edit matching presets to
create, edit, copy or delete a template. Save draft as a preset captures a direction’s
current checks. Built-ins offer Save as new. To apply an edited preset, select it
in the intended source/group direction and Save; active rules do not follow later
template edits. Presets persist in the retained catalogue database.

Sign in with ChatGPT opens the official OpenAI page. Return to AutoSourcarr to copy
the one-time code; both account screens refresh immediately on return. Complete
codes survive later command output, source inventory cannot block the account
poll and obsolete reads cannot block Cancel or restore cancelled instructions.
Remaining expiry time comes from the worker. Existing account retention and scoped
cancellation stay in place. Enable ChatGPT device-code login if OpenAI requires it.

Local executable, API and controlled UI fixtures cover these changes. Actual
OpenAI account login and a Docker/TrueNAS deployment were not performed here.

## Upgrading to 0.3.28 — matching presets and AI account sign-in

Overlay the full archive and rebuild/redeploy the existing AutoSourcarr image.
Keep env files, both runtime databases, source profiles, saved AI credentials,
reader signing keys and Keiyoushi selection locks. Existing services/mounts and
installed reader extensions remain compatible.

Sources → Matching rules now has five draft presets for each source/group and
matching direction. Choose, review and Save each direction independently. Inherit
and Disabled retain their meanings; no existing catalogue decisions are rewritten.

Setup → Repair and AI and Sources → Source repairs → Setup and account open the
official OpenAI page from the Sign in with ChatGPT click. Use the fallback link if
popups are blocked; copy the code from AutoSourcarr into the OpenAI page. Progress
and failures remain visible. Failed/cancelled attempts retain the saved login.
New credentials commit only after the owned repair account process has stopped.
Device-code login may need enabling in ChatGPT security settings or by the workspace
administrator. No account password is collected by AutoSourcarr.

The source archive has automated fixture coverage; actual OpenAI sign-in and a
Docker/TrueNAS image deployment were not performed here. See CHANGELOG.md.

## Upgrading to 0.3.27 — reader checks and local recovery

Overlay the full archive and rebuild/redeploy the existing AutoSourcarr image.
Keep env files, both runtime databases, source profiles, reader signing keys and
Keiyoushi selection locks. The existing services and mounts remain sufficient;
existing installed reader extensions remain compatible with these backend fixes.

Open **Setup → Reader extensions** to check an unsaved reader draft, or use
**Status → Review reader settings** to open that step. The wizard and builder share their
address/key rules. Finish can still save internal/development configurations;
building both APK/JAR requires a valid reader key and public HTTPS address by
default. Status uses the running settings. After changing reader settings, Finish
and restart this instance before building; reinstall artifacts if their embedded
addresses/key changed. Previously published downloads remain available.

The optional daily source watcher gives unavailable local repair readiness a
one-minute retry and five-second deadline. No source check or repair POST occurs
before that readiness check. Once ready, it retains the durable daily reservation
before source traffic and weekly protection after uncertain submissions.

Reader image SQLite work runs outside the HTTP event loop, with current source
ownership checked before streaming and owned HTTP responses closed on failure or
cancellation. HTTP pool saturation stays a local deferral instead of a website
failure; all-empty local-capacity feeds can recover on the next request.

No live Docker/TrueNAS deployment or native APK/JAR build is claimed. The existing
Suwayomi pin remains unchanged. See CHANGELOG.md for the automated checks.

## Upgrading to 0.3.26 — durable decisions and reliable recovery

Overlay the complete archive, then rebuild/redeploy the existing AutoSourcarr
image. Keep both runtime databases, all retained env files, source profiles,
reader signing keys and existing Keiyoushi selection locks. No Compose services,
Docker socket or additional mounts are required. The development knowledge database
remains a separate code/research artifact.

The additive catalogue migration creates a normalized preferred-title index,
manual group suppression and pending chapter-origin merge/split journals. Migrations
own each file's DDL/backfill transaction. The first startup backfills the index;
subsequent restarts recover interrupted merge/split/import
work without replaying newer user decisions. Manual **Unmatch duplicates** and
**Reset groups** choices persist. Source Search/Chapters choices and disabled solver
intent survive temporary source disappearance or restart.

Saved/recovered managed settings load before full validation. Invalid stale injected
values no longer prevent valid saved settings from loading; final invalid settings
still need correction.

**Status → Build reader extensions** publishes APK/JAR as one complete generation.
A fresh build uses the shipped default Keiyoushi commit; a retained lock remains
selected until an explicit update. Keep old signing state and retained locks when
overlaying. An unreadable/incomplete committed generation fails closed at download.
Existing reader addresses and API authentication are unchanged.

**Sources → Source repairs → Check repair tools** now shows inherited process
limits and sampled memory/process/workspace budgets. Host namespace/egress isolation
is still required; these budgets do not replace it or impose a hard aggregate memory
reservation. Aggregate usage can exceed a sampled limit briefly before termination.
Actual host resource pressure, image toolchains and provider execution require
verification on the deployed instance.

Use **Discard validated repair** to decline an idle ready candidate. The artifact,
conversation and evidence remain available; it no longer blocks later maintenance
or permits installation. Start a new repair if you reconsider.

FastAPI 0.142.0 / Starlette 1.6.0 are included. The source engine remains v2.3.2243;
a newer Suwayomi pin is deferred until retained-profile, API and Java-agent tests can
run against a real image. See CHANGELOG.md for the fresh automated checks and limits.


## Upgrading to 0.3.25 — matching safeguards

Overlay the complete project and rebuild/redeploy the existing AutoSourcarr image.
Keep config, databases, source profiles and signing keys. No Compose changes or
extra service is needed. This release fixes automatic import-owner merges, native
title-floor enforcement, retained database layout compatibility and rule-save UI.
The separate development knowledge database is for future code/research work; do
not mount it over catalog.sqlite or chapter-origins.sqlite.

## Upgrading to 0.3.23 — merge history and matching policies

Overlay the complete archive and rebuild/redeploy the existing AutoSourcarr image.
Keep all retained config, databases, source profiles and signing keys. No Compose
edits or additional services are needed. The additive catalogue migration creates
per-source/group matching rules and an evidence field on the existing merge audit.
No existing match is automatically split because of a newly chosen rule.

Open Duplicates → Automatically combined for old/new recorded merges. Open
Sources → Installed → Automatic matching rules for independent same-source and
cross-source requirements; each direction saves separately. Default matching is
unchanged until a custom rule is saved. Each AutoSourcarr instance has its own rules.


## Upgrading to 0.3.22 — current-instance repair setup

Overlay the complete project and rebuild/redeploy your existing AutoSourcarr
service(s). Keep all retained config, data, source profiles and signing keys.
No new Compose service, mount, worker token or AI environment field is needed.
Open Sources → Source repairs → Setup and account → Configure repair AI. It opens
the same Repair and AI wizard step as Settings → Run setup again. Finish saves
changed settings; restart this instance when prompted.

Each instance automatically owns its internal repair process. No service-name
choices, Compose inspection or manual instance list is necessary. The old external
connection and Compose-generator controls and web endpoints are removed. A bundled
image ignores stale APP_SERVICES_MODE=external values. Missing tools now report a
runtime problem instead of restoring a remote worker connection. The generic
settings editor also hides the unused worker address/token/mode fields while
retaining their old file values for rollback.

Run Check repair tools before acting on an isolation diagnostic. An unchecked
sandbox no longer presents host setup instructions. A genuine host restriction
still requires administrator attention; this release does not change or bypass
container isolation policy. The older host-policy notes below apply only when the
actual check identifies such a restriction, not when connecting an AI provider.


## Upgrading to 0.3.21 — reset decisions, AI accounts and isolation diagnostics

Overlay the complete project and rebuild/redeploy the existing AutoSourcarr
service(s), retaining all config, data, source profiles and signing keys. No new
service, mount or AI environment variable is needed. Account setup is directly
inside Settings → Run setup again → Repair AI. Finish saves changed configuration;
restart when prompted. Explicit account actions are immediate, even if you cancel
the configuration draft. Named cloud APIs require API credentials; Custom and local
providers require a reachable API address and an exact tool-capable model ID.

Manual actions → Reset all manual actions shows counts and requires confirmation.
It forgets manual rules while keeping the present library and completed operations.
Use your normal retained-data backup before a bulk reset; it is not an undo-merge
or undo-split operation. Skipped imports return to Unresolved for review.

For the reported **Repair isolation is unavailable** screen, run the tools check
again and expand its diagnostics. The prior generic message did not identify the
blocked host operation. This release reports the underlying failure; it cannot
change the host's policy from inside an app container. If the per-service repair
policy is absent, add this to **both AutoSourcarr services** (not main Suwayomi),
using the existing shared project path:

```yaml
cap_drop:
  - ALL
security_opt:
  - no-new-privileges:true
  - seccomp=/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/project/security/repair-seccomp.json
```

Rebuild alone does not update container security settings: Save/Redeploy after a
Compose change. If AppArmor blocks the reported operation, follow the named-profile
instructions in the 0.3.17 section below; the profile must exist on the host before
you reference it in Compose. A disabled host user-namespace facility needs an
administrator's review. `Seccomp: 2` only means filtering is active; it does not
prove the correct profile was applied. Do not use privileged mode, unconfined
profiles, SYS_ADMIN or a Docker socket to bypass the check. If it still fails,
copy the new diagnostic with the active service security options for diagnosis.

## Upgrading to 0.3.20 — setup accounts and AI connections

Overlay the complete project and Save/Redeploy the existing AutoSourcarr service(s).
Keep each instance’s existing Compose mounts, data and source profiles. No new
service or Compose environment fields are required. Open Settings → Run setup
again: new steps cover repair AI, accounts/keys and companion-app browser links.
After Finish, restart when prompted; refresh to continue the guided account checks.

ChatGPT uses the bundled Codex device sign-in. Custom/local AI uses a configured
Responses API, model and optional key, with an edit-only relay inside the integrated
worker. A successful connection check is not a completed repair or a guarantee of
model tool support. Browser-only chat clients cannot be connected as API servers.
Use the Suwayomi HTTP username/password fields only for HTTP Basic authentication;
reverse-proxy form login and source website sessions use their own browser pages.

## Upgrading to 0.3.19 — Android SDK image build correction

The September 29 lifecycle log fails during the image build, before app containers
are created. Google publishes `platforms;android-37.0`; the previous Dockerfile
requested the nonexistent `platforms;android-37`. Build tools remain `37.0.0`.

Overlay the release into your existing project folder, preserving live config/data
and source-profile directories. Keep your current Compose and select Save/Redeploy
in TrueNAS; both AutoSourcarr services use the same corrected image build context.
You do not need to remove containers, databases, images or build caches. Earlier
missing env-file/image-pull errors in the supplied log are separate historical
failures; do not change active settings based on those entries alone.

If a later build step fails, send the new deployment error with its Dockerfile
line and the preceding command output. A full Docker build was unavailable in
the release workspace; package availability checks do not prove image startup.

## Upgrading to 0.3.18 — guided setup

Replace the project source with this release and rebuild/redeploy using the existing
AutoSourcarr service. No new service, port or mount is needed. Retain each instance's
actual config, data and source-profile directories and all signing keys.

The first browser visit opens a review of current settings. Finish saves changed
values; the wizard offers an app restart when required. Later, Settings → Run setup
again can be cancelled without saving. Changing a reader endpoint/key also requires
Status → Build extensions and reinstalling those artifacts in the corresponding reader.

The post-setup scan only covers app-visible installation files. It cannot clean ZIPs
or old reports in a host project folder that is not mounted into the container. Do
not mount the whole host or delete retained source/data folders to enable cleanup.

## Upgrading to 0.3.17 — only AutoSourcarr and main Suwayomi

Use the existing Custom App. Overlay this complete project, preserving every live
config/data/source/signing directory. Keep your existing service names, actual
mounts, published ports and main Suwayomi settings. Do not replace a working main
Suwayomi profile with the example's empty directory.

1. Keep the AutoSourcarr build context pointing to `project/autosourcarr`, with
   `pull_policy: build`. Add `APP_SERVICES_MODE: integrated` alongside
   `SOURCE_ENGINE_MODE: integrated`. Both app instances still listen on 8282.
2. Keep `user: '568:568'`, `stop_grace_period: 90s` and separate writable /config,
   /data and source-engine mounts for each instance. Existing directories must be
   writable by that UID. Internal tools use /data/tools; no extra mount is required.
3. Add the repair security options below. Remove only AutoSourcarr's obsolete
   helper service dependencies after the new image is built. The new examples have
   two services, or four in `docker-compose.two-instances.yml`. Do not remove shared
   services still used by other applications. Keep old repair data for rollback.
4. Save/Redeploy the app(s), then refresh. In Settings → Services check internal
   tool status. In Sources → Source repairs → Repair tools & account run Prepare
   repair tools, then sign in for that instance. Routine work needs no worker URL,
   token, generated Compose or terminal. The official OpenAI sign-in page remains
   the account authorization step.

For each AutoSourcarr service, using the actual extracted project path:

```yaml
cap_drop:
  - ALL
security_opt:
  - no-new-privileges:true
  - seccomp=/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/project/security/repair-seccomp.json
```

Repair uses mandatory rootless user, mount, PID and network namespaces. The custom
seccomp policy is based on Moby's default at commit
`85e237f1fe229a0c61c9c7d8e743fa780d3b97ca`, adding only the user-namespace and mount
operations required by bubblewrap. It does not grant SYS_ADMIN or Docker access.
No privileged/unconfined fallback is used. The app cannot grant host kernel policy
permissions from inside the container.

**If AppArmor is enabled:** the host administrator must load the supplied profile
once (and persist/reload it according to the host's supported boot configuration):

```bash
sudo apparmor_parser -r /mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/project/security/autosourcarr-internal.apparmor
```

Then add `apparmor=autosourcarr-internal` to the same security_opt list and redeploy.
Do not add that option before the host profile exists. This profile is a Moby-default
derivative retaining explicit proc/sys denials while allowing private namespace
mount operations. A host that disables unprivileged user namespaces still cannot
run repair. Prepare repair tools reports this and repair stays disabled; browsing,
imports and other application functions do not depend on repair being available.
Do not disable host-wide protections to silence that check.

Existing externally published readers rebuild after startup when the version is
outdated. Otherwise use Status → Build extensions separately in normal/private apps.
Each app retains its own build cache and signing key. Download its current JAR/APK
from Status. No external builder service is needed. Preserve the normal internal URL
`http://autosourcarr:8282` and private `http://private-autosourcarr:8282`; keep their
main library URLs pointed at the corresponding Suwayomi service.

The first image build downloads the bundled tools and is substantially larger.
Translation models download on first use and are retained. Old external repair job
history/account state is not automatically migrated into both apps: keep it and sign
in independently. Old compatibility files remain for rollback, not default setup.
This release's workspace could not run Docker/TrueNAS or actual repair namespaces;
read the limits and test evidence in CHANGELOG.md before replacing a working image.

---

The sections below describe earlier versions; follow the current section above for
integrated deployments.


## Upgrading to 0.3.16 — clickable repair setup

Overlay the complete project, Save/Redeploy both AutoSourcarr app containers and
refresh the browser. Keep retained config/data, source profiles and signing keys.
Existing 0.3.15 repair workers and reader extensions remain compatible; no worker
or reader rebuild or port change is needed for this UI correction.

Open Sources → Source repairs and click Set up repair worker. It opens both setup
sections immediately without requiring a working worker connection. Enter the actual
project and separate repair data host paths; placeholder examples are not values.
Generate now reports missing input beside the button. For first-time installation,
copy the generated complete Compose into the same Custom App and Save/Redeploy once.


## Upgrading to 0.3.15 — repair setup in the web UI

Overlay the complete project and update/redeploy the AutoSourcarr and optional
repair controller images. Existing reader extensions remain compatible; no reader
rebuild or port change is required. Keep retained config/data, source profiles,
repair worker data/auth and signing keys.

Open Sources → Source repairs → Setup and account. If the controller has not been
installed, paste the complete current Custom App Compose into First installation,
select the existing services and enter separate host project/repair data paths.
Copy the generated Compose into the same Custom App and Save/Redeploy once.
AutoSourcarr itself receives no Docker socket. The generator preserves existing
app volumes, ports, network aliases and unrelated services. Keep generated tokens
private. Existing repair data paths and tokens are retained; no relocation is done.

Then use Prepare repair tools and Sign in with ChatGPT in AutoSourcarr. Account
checks, sign-out, repair work, validation evidence, installation and rollback all
remain in that tab. The same account serves both connected instances. The initial
Docker deployment and official OpenAI authorization remain outside AutoSourcarr;
routine terminal commands are no longer necessary.

## Upgrading to 0.3.14 — reader browse progress and repair setup

Overlay the complete project and Save/Redeploy both AutoSourcarr app containers.
The reader fix is backend-only; keep the existing 0.3.12/0.3.13 reader extensions.
Both AutoSourcarr containers still listen internally on 8282. Keep all existing
configuration, data, integrated source profiles and signing keys.

A cold catalogue batch now retains completed ingestion across work timeouts, so
reader retries advance instead of fetching/reprocessing the same initial titles.
Known records are served from current stored metadata; force-live refresh retains
metadata updates. A genuinely new large page may still need preparation time.

For optional source repair setup, follow README → Private source repair worker.
The TrueNAS helper now accepts repeated --service options for autosourcarr and
private-autosourcarr in the same Custom App. Preview before --apply, which builds
images and redeploys the existing Custom App. Shared worker job history is visible
to both selected instances; app source data and ports stay separate. Domain
migration still requires a separately reviewed change; it is not auto-approved.

## Upgrading to 0.3.13 — local and manually marked orphan sources

Overlay the complete source and Save/Redeploy both AutoSourcarr apps, then refresh
browser assets. No Compose change or reader extension rebuild is needed for this
classification update; keep the working 0.3.12 readers. Preserve each instance's
separate config, data and source-engine mounts.

Successful manual APK uploads appear in Sources → Discover → Installed orphan,
including uploads recorded by older releases. Mark other installed packages whose
repository is no longer configured with **Mark as orphaned**. Use **Return to
Installed** to undo the label. Settings, Update and Uninstall stay on the orphan
card. Classification is saved in each instance's retained source-maintenance data;
it changes neither source IDs nor library records and does not run a repair.

## Upgrading to 0.3.12 — Discover settings and reader catalogue recovery

Overlay the complete project and Save/Redeploy both apps. Refresh browser assets.
Run the existing paired builder and reinstall each newly generated reader JAR
in its corresponding main Suwayomi; the preparation-response fix includes Kotlin
transport code, so an old installed JAR will not receive the full fix.

The private Suwayomi log starts successfully on **30059**, whereas your supplied
Since 0.4.5 the Suwayomi services publish no host port. Set `BIND_PORT: '30058'` in
private-suwayomi, leave `ports` out, and set the private app's
`MAIN_SUWAYOMI_URL=http://private-suwayomi:30058`. Save/Redeploy those existing
services. If deliberately keeping the process on 30059, use mapping 30062:30059
and the internal URL ending :30059 instead. Host/browser access stays on 30062.
This does not change `AUTOSOURCARR_INTERNAL_URL=http://private-autosourcarr:8282`.

Discover → Installed now opens source settings per extension and language variant.
Popular/Latest/filter responses allow time for result assembly and the new reader
polls the same preparing job at most twice; ordinary website failures are not
retried by this mechanism. Source selections also constrain local cached matches.
Preserve both config directories, databases, engine profiles and signing keys.
No new service, port exposure or source-host container is required.

## Upgrading to 0.3.11 — source settings, APK uploads and reader response fixes

Overlay the complete project, Save/Redeploy both apps, and refresh the UI. Preserve
both retained config directories, databases, engine profiles and signing keys.
No new service, source-host container or port exposure is required.

For the reported `private-autosourcarr:8283` connection refusal, edit the private
retained `autosourcarr.env` so `AUTOSOURCARR_INTERNAL_URL=http://private-autosourcarr:8282`.
The supplied mapping `30061:8282` uses 30061 on the host and 8282 between containers.
The normal internal URL stays `http://autosourcarr:8282`. Rerun the already working
paired builder, then reinstall the newly generated JAR in each corresponding main
Suwayomi. A stale installed JAR retains its previous compiled URL even after the app
has been updated. Keep Android public URLs and signing keys unchanged.

Source preferences are available under Sources → Installed → Source settings. Local source
APKs can be dropped into Sources → Discover. The AutoSourcarr reader JAR must instead
be installed into the main/private Suwayomi reader, never AutoSourcarr's own engine.
The README contains builder commands and per-instance output paths. Cold reader feeds
may briefly say “catalog is preparing”; retry shortly while the admitted job finishes.

## Upgrading to 0.3.10 — gallery chapters, responsive details and paired builds

Overlay this complete project and Save/Redeploy the existing app. Preserve both
instances' config, databases, source profiles and signing keys. Cached gallery
chapter reconciliation runs locally in the background; no source-host container
is reintroduced. Refresh the browser to load the new UI assets.

The supplied private diagnostic points at `http://private-suwayomi:30062`. Set the
private app's `MAIN_SUWAYOMI_URL` to `http://private-suwayomi:30058`. Since 0.4.5
there is no host/browser port for Suwayomi; open entries in AutoSourcarr's WebView.

Replace the existing builder service using `docker-compose.builder.yml`; set its
private host config mount to the actual retained directory mounted at `/config`
by `private-autosourcarr`. Enable `AUTOSOURCARR_BUILD_MODE: paired` and both config
variables. A single old config mount cannot produce the private artifact. The
README gives exact keys, commands and output filenames. Run `--check` before the
build. This does not replace either app service or their data mounts.

Normal/private outputs now live in the two `extension/dist-instances/` subfolders;
the old single-instance `extension/dist/` is not the paired output. The release ZIP
contains the updated source. Generate the deployment-specific APK/JAR files with the SDK
builder, which embeds the actual endpoints/token and retains the existing signing key.


This project is designed to run on TrueNAS SCALE/Community Edition using Docker-based Apps.

## Upgrading to 0.3.9 — two-instance builds, gallery tags and orphan archives

Overlay this complete project and use the existing App Save/Redeploy flow. Keep
both instances' separate config, data and integrated source-engine mounts. The
new tag tables migrate automatically; background indexing uses saved metadata.
No source-host container is reintroduced. For sequential APK/JAR compilation,
update the existing SDK builder to call `extension/build-in-container.sh` with
`AUTOSOURCARR_CONFIG_DIR` and `AUTOSOURCARR_PRIVATE_CONFIG_DIR` pointing to separately
mounted retained config directories. See `docker-compose.builder.yml` and README;
replace sample host paths before use. One builder run produces both outputs, in
order, with independent signing keys and reader endpoints. Do not share writable
app data or source profiles between instances.

Orphan discovery/install appears under Sources. Automatic code repair needs the
optional worker setup below. If already configured, rerun its configuration helper
to build the 0.3.9 worker images while retaining account/data mounts.

## Upgrading to 0.3.8 — optional private source repair worker

The app update remains a normal Save/Redeploy. Source repairs are optional and need
the one-time **Private source repair worker** setup in README. Use
`scripts/configure-repair-worker.py --apply` for the existing managed Custom App;
it preserves its actual configuration and never starts a competing Compose stack.
The repair controller alone receives Docker-socket access. Source builds/tests run
on a separate internal network with public-only egress, without live source data.
Sign in yourself with `scripts/repair-worker.sh login`; never paste credentials here.

## Automatic builds and deployment repair

This release adds Mixed entries and fixes stale manual import state. The small
review table is created automatically in the retained catalog database. Preserve
both databases and refresh the browser after redeploying to load the new tab.
No Compose, mount, source-host or solver configuration changes are required.


You no longer run a separate image-build command for each update. The supplied
Compose service uses the stable local image `autosourcarr:local`, the project build
context and `pull_policy: build`. TrueNAS builds it during **Save**, **Redeploy** or
**Stop → Start**. Unchanged image layers are reused; changing application code or
VERSION no longer invalidates the Python dependency-install layer.

The 0.3.2 build also compiles the source-browser Java adapter. Its builder stage
needs the official Temurin 21 JDK image once; the runtime remains the pinned Suwayomi
image. Saved browser identity lives in the retained `/config/source-browser.json`.
Applying a browser identity or clearing browser data from Settings restarts only the
integrated source process. Normal source loading stays asynchronous.

Version 0.3.3 fixes repeated optional-solver health checks and shows the actual
destination and failure reason under Settings → Source browser. If FlareSolverr is
unreachable, verify the existing service, `FLARESOLVERR_URL`, and shared Docker
network. The integrated source engine does not itself provide a FlareSolverr server.
Preserve retained configuration; no solver address is silently replaced by this update.

### Repair the saved TrueNAS deployment

The latest supplied 0.3.0 export confirms the integrated source engine is ready.
For that working deployment, overlay this release and Save/Redeploy normally.
Use the following one-time repair only if an older deployment still reports
`runtime_available=false`, from its existing `project/` directory on the **TrueNAS host**:

```sh
sudo python3 scripts/repair-truenas.py --apply
```

This one-time helper identifies the existing Custom App, reads its complete saved
Compose configuration, builds the integrated image, and updates only AutoSourcarr's
launch/build configuration and the old source-host service. It reuses the exact
retained source-data mount and old numeric source UID/GID, and preserves other services, ports, networks, config
and database mounts. **Applying restarts the entire Custom App stack.** A private
original-configuration backup is retained once in the sibling `truenas-recovery/`
directory; it is not overwritten. Nothing runs on the server until you invoke it.
Omit `--apply` for a preview; add `--app NAME` if more than one app is found.

The helper refuses ambiguous, empty, read-only or shared source datasets and detects
concurrent configuration edits. Named/interpolated source mounts need manual mapping;
it never guesses a new empty source directory. If the old source service and mount
were removed from saved YAML, provide the retained host directory explicitly. For
your confirmed source dataset and the 568:568 source identity used in the previous
TrueNAS service example, run:

```sh
sudo python3 scripts/repair-truenas.py --apply \
  --source-data /mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/source-host \
  --source-user 568:568
```

`--source-data` takes only the host directory; the helper restores its mount at
`/home/suwayomi/.local/share/Tachidesk` with automatic directory creation disabled.
It must already exist and contain your source data. A conflicting existing mount
is rejected. `--source-user UID:GID` explicitly restores the previous source process
identity without changing file ownership; use the old service's identity if you
customized it. The source data directory and `source-host.env` remain in use by the
integrated process; no separate source-host container is recreated. If a deployment job remains pending,
inspect that job before retrying. Runtime-file presence is checked after deployment;
then verify **Status → Integrated process · ready**. The engine still uses the
retained source database, extension settings and cookies. Future source updates use
normal Save/Redeploy or Stop → Start with automatic builds.

The helper follows the official [TrueNAS configuration update API](https://api.truenas.com/v25.10.0/api_methods_app.update.html)
and [stable local API client](https://github.com/truenas/api_client/blob/TS-25.10.7/truenas_api_client/__init__.py).
It has been tested with controlled API/Docker fixtures; live execution is unverified.

### Manual deployment repair alternative



The attached log came from the legacy wrapper in a container without the integrated
runtime. Your successful `docker build` created an image but did not switch the
existing container to it. 0.2.96 also keeps the UI and setup diagnostics accessible
on an old Python container, instead of exiting repeatedly. Source execution still
requires the integrated image.

1. Overlay this release into the existing `project/` directory. Keep the persistent
   config, both AutoSourcarr databases, source database and signing keys.
2. In the existing TrueNAS Custom App YAML, replace its `autosourcarr` service with
   the service in `examples/manga-cluster-autosourcarr-services.yml`. For a standalone
   AutoSourcarr app use `docker-compose.truenas.yml`. Keep your real ports, networks,
   `/data`, `/config`, optional `/database` and download mounts. The build context is:
   `/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/project/autosourcarr`.
   If your project is elsewhere, change that one absolute path.
3. Remove old `python:...` / numbered AutoSourcarr image declarations, custom startup
   `command`/`entrypoint`, `/app` code mount and `/deps` mount from that service. The
   built image supplies the code and dependencies. Keep environment values in your
   existing env file; the supplied YAML explicitly selects integrated mode.
4. If the separate source-host service is still present, stop it and remove its
   service/dependency entry before starting the integrated engine. Mount its **same
   existing data directory** at `/home/suwayomi/.local/share/Tachidesk` in AutoSourcarr.
   Keep the source data and `source-host.env`; never replace them with empty data.
   The main reading Suwayomi and `autosourcarr-translate` remain in your stack.
5. Save the YAML and start/redeploy the app. The build now runs automatically and
   the new container uses its result. Status should show **0.3.7** and
   **Integrated process · ready**. A successful build alone does not verify engine
   startup; if it remains unavailable, export the new diagnostics/container log.

### Every later source update

Copy the new release into the same `project/` directory, then use **TrueNAS Apps →
Stop → Start** (or Save/Redeploy). No separate build command or per-version image-tag
edit is needed. For normal Docker Compose, `docker compose up -d` performs the build.
A plain `docker restart` / `docker compose restart` only restarts the existing image.
TrueNAS **Update/Pull Images** is a registry workflow, not this local-source workflow.
The old `scripts/build-integrated-image.sh` remains an optional prebuild helper.

Sources run as a managed Suwayomi v2.3.2243 process inside AutoSourcarr. WebView uses
that engine's Chromium session through the authenticated bridge; no separate source
container or published source port is required. This does not remove the main reader.
Source IDs, extension preferences and cookies stay in the retained source dataset.

Implementation references: [Docker build policy](https://docs.docker.com/reference/compose-file/services/#pull_policy)
and [TrueNAS 25.10.7 app lifecycle](https://github.com/truenas/middleware/blob/TS-25.10.7/src/middlewared/middlewared/plugins/apps/app_scale.py).
The normal Start/Redeploy paths were checked in 25.04.2.6 and 25.10.7; live execution
on this server remains unverified.

### Source WebView, settings and rollback

Sources → **Open WebView** uses the engine's Chromium session and shared source
cookies. Each installed source has a WebView button; new source errors link to the
affected chapter when its URL is available. One WebView connection is supported at
a time. Complete website verification yourself, then retry after the short cooldown.
HTTP 403/521 can reflect a website restriction/outage that WebView cannot resolve.
For HTTPS through NPM/Authelia, keep AUTOSOURCARR_PUBLIC_URL set to the actual public
AutoSourcarr origin and enable WebSocket forwarding for this app. No source port is
published; the bridge requires a short-lived, scoped admin session.

Source repositories and source settings remain in `/config/source-host.env`.
Restart AutoSourcarr after editing that file; it is read by the child process on each
start. Changes to `autosourcarr.env` still require container recreation by Compose.
Keiyoushi remains the default repository. Existing extensions/preferences stay in
the reused source database. The integrated image does not mount the old `python-deps` cache; keep it if you want to roll back.

Rollback: stop the integrated container completely, restore the previous project/image
and old source service against the same directories. Select `SOURCE_ENGINE_MODE=external` explicitly for that legacy topology. The integrated startup writes
loopback/no-auth/WebUI-off engine settings; the previous source-host.env must restore
its intended bind/auth/WebUI settings on startup. Snapshot the source database before
upgrading because a different JVM engine version may migrate its own H2 database.
Restore that snapshot if its previous engine cannot open the upgraded format.

## Historical upgrade notes (pre-integrated deployments)

## Upgrading to 0.2.90

Stop AutoSourcarr and snapshot/back up the existing project, configuration and both
SQLite databases (including the external database dataset when used). Overlay the
archive contents into your existing `project/` folder. Preserve the live
`config/autosourcarr.env` and `config/source-host.env`, data, dependency cache and
signing directories. This release contains env examples, not live env replacements.
Restart/redeploy the backend; rebuild its image if using an image-based install.
Refresh the browser and confirm **0.2.90** in Status. Existing APK/JAR readers can
continue using the same API and source identity.

Use Subbed > Repair unmatched for source-less subscriptions. Tasks now show source
progress and partial completion; Retry continues with sources not yet searched.
`SOURCE_RECOVERY_TIMEOUT_SECONDS=45` and `SUBBED_SEARCH_ENTRY_TIMEOUT_SECONDS=120`
are optional new defaults; existing env files work without adding them. SQLite
adds indexes on the first upgrade, which can take longer than later restarts on a
large database. Do not reset the database to apply this update.

For rollback, stop the backend and restore the previous project files, then restart.
The new indexes are additive and can remain on 0.2.89. Keep any newer user data;
restore a database snapshot only when deliberately reverting those newer changes.
Live TrueNAS, Authelia and upstream source checks require your deployed instance.
See `CHANGELOG.md` for the checks performed on this release.

## Upgrading to 0.2.89

Stop AutoSourcarr and snapshot/back up the project, configuration and both SQLite
databases, including any external database dataset. Overlay the release into
`project/`, retaining live env files and sibling `config`, `autosourcarr-data`,
`source-host`, `python-deps` and signing directories. Restart/redeploy; image-based
deployments require rebuilding the backend image. Confirm 0.2.89 in Status and
refresh the browser. No reset, backup re-upload or reader reinstall is required.

Check one unresolved import, then Skip a waiting Match/Retry and confirm it stays
skipped. Check an existing subscription and one manually selected title. Use
Subbed > Repair unmatched before a broad Search all. Rejection preserves existing
or unverified historical subscriptions, and now coordinates with concurrent adds.

See `CHANGELOG.md` for automated evidence and live integration limits.

## Upgrading to 0.2.88

Stop AutoSourcarr and back up/snapshot the project, configuration and both databases
(including an external database dataset if configured). Overlay this archive into
`project/`; retain sibling `config`, `autosourcarr-data`, `source-host`, `python-deps`
and persistent signing keys. Keep active environment files. Restart/redeploy;
image-based installs must rebuild the backend image.

Confirm 0.2.88 in Status, then refresh the browser. No database reset, backup
re-upload, reader reinstall or dependency change is required. The additive
`library_add_verified` column defaults to false for existing import history.
Older Added labels remain visible, but Reject preserves their subscriptions because
the importer cannot retrospectively establish who added them.

Check one unresolved retry, cancel one queued action, and verify that existing
subscriptions/manual choices remain. Use Subbed **Repair unmatched** for entries
without source anchors; review ownership or verification outcomes before choosing
broader **Search all**. Live CAPTCHA failures still require source access recovery.
See `CHANGELOG.md` for the precise automated/live verification distinction.

## Upgrading to 0.2.87

Stop AutoSourcarr and snapshot/backup the project plus persistent databases/config.
Overlay the complete archive into `project/`, preserving sibling `config`,
`autosourcarr-data`, `source-host`, `python-deps` and signing keys. Restart/redeploy;
image-based installations must rebuild the backend image. Do not copy example env
files over active env files. No database reset or backup re-upload is needed.

Verify 0.2.87 in Status and hard-refresh the browser if required. In Subbed run
**Repair unmatched** once to recover first sources. Broader **Search all** remains
available. Do not repeatedly queue either action; both share one bulk discovery task.
Review ownership conflicts rather than forcing merges of distinct works.

Requirements pins are unchanged from 0.2.86. The dependency-cache compatibility key
now includes libc and its path guard protects config/data/application mounts. A
one-time staged package download may therefore occur; keep Internet access and free
space available. Old generations are retained, and a failed refresh fails startup
visibly without deleting the working generation. `/deps` must be a dedicated mount.

Both extension builders are retained; Kotlin source is unchanged from 0.2.86 and no
reader reinstall is needed for these backend/UI fixes. Existing reader keys,
Authelia rules and persistent signing identity must remain intact.

See `CHANGELOG.md`: automated local checks are not a live test of your
production database, sources, TrueNAS, Authelia or Android/Suwayomi installation.

## Upgrading to 0.2.86

Stop the backend and take a backup/snapshot, overlay this release in `project/`,
then restart/redeploy AutoSourcarr. Keep the sibling `config`, `autosourcarr-data`,
`source-host` and `python-deps` directories and persistent signing keys intact.
Use only the example config files as templates; do not replace your active config.

There are no dependency or reader-source changes relative to 0.2.85, and no reset
or backup re-upload is required. Static asset version URLs refresh the browser UI;
hard-refresh the page if it still shows the previous release. Normal startup resumes
only unfinished queued/searching import discovery. Finished candidates and review
results are retained; explicitly cancelled rows remain paused until Retry unresolved.
Different import runs still share one bulk execution lane, with bounded parallel
item/source work inside it. Waiting duplicate matching is an intentional priority rule.

This release makes Tasks/Errors/Cancel and import scheduling more responsive; it
cannot remove a source CAPTCHA or guarantee access to an unavailable upstream.
The separate test report describes local verification, not a live TrueNAS deployment.

## Upgrading to 0.2.85

Stop/redeploy the AutoSourcarr backend after overlaying this release in `project/`.
Keep persistent data, configuration, signing keys and source-host storage intact.
No reset is required; existing metadata, manual decisions, import audits and
subscriptions are not intentionally discarded by this upgrade.

The Python requirements changed. On its first start, the TrueNAS launcher downloads
packages into `python-deps/.packages-*`, validates them and publishes a `current`
symlink. Cache keys include Python ABI and architecture. A failed installation
leaves the previous generation intact, but startup still fails visibly rather than
running the new source with old dependencies. Restore the previous `project/`
release to roll back code; take a stopped-app copy/snapshot of persistent data before
upgrading when a reversible recovery point is required.

Old package generations and the builder's last-known-good output can consume extra
disk space. They are not automatically deleted while another process may be using
them. Do not delete `python-deps`, persistent signing keys or the database as a
routine upgrade step. The supplied environment files remain templates only.

Rebuild/update the reader extension to gain off-origin API-key filtering. Backend
fixes do not depend on an immediate reader reinstall. Live TrueNAS, Android and
Suwayomi compatibility still needs a deployment smoke test; the release's audit
report states which automated and isolated checks were performed.

## Recommended dataset layout

Replace `POOL` with your actual pool name.

```text
/mnt/POOL/apps/autosourcarr/
├── project/              # extracted project source
│   └── autosourcarr/
├── config/               # persistent autosourcarr.env + source-host.env
├── autosourcarr-data/    # catalog.sqlite + chapter-origins.sqlite
├── source-host/          # private Suwayomi data directory
└── python-deps/          # cached Python packages for YAML deployment
```

Create these datasets/directories before deploying the app.

## Method A — TrueNAS Apps → Install via YAML

Use `docker-compose.truenas.yml`.

1. Extract the project so this file exists:

```text
/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/project/autosourcarr/app/main.py
```

2. Create the persistent env files once. With the layout used in this project:

```bash
bash /mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/project/scripts/init-truenas-config.sh
```

3. Edit `config/autosourcarr.env`: keep `AUTOSOURCARR_INTERNAL_URL=http://autosourcarr:8282` for the main Suwayomi JAR, set `AUTOSOURCARR_PUBLIC_URL` to the address your Android Tachiyomi/Mihon client can reach, verify `MAIN_SUWAYOMI_URL`, and optionally set `ADMIN_TOKEN`. Edit `config/source-host.env` for source-host/FlareSolverr settings. If you want Status to calculate downloaded disk usage, additionally bind-mount the main Suwayomi AutoSourcarr download directory read-only into the `autosourcarr` service and set `MAIN_SUWAYOMI_DOWNLOADS_PATH` to that container path.
4. Check the absolute build context and persistent mount paths in `docker-compose.truenas.yml`. Keep your existing network connection to main Suwayomi. The app reads its runtime settings through `env_file`; the engine reads `/config/source-host.env`. TrueNAS builds the image automatically when deploying this YAML.
5. In TrueNAS open **Apps → Discover → Custom App → Install via YAML**, name it `autosourcarr`, paste the edited YAML, and save.

The TrueNAS Compose file uses the stable `autosourcarr:local` image with `pull_policy: build`. Save/Redeploy or Stop → Start rebuilds changed layers automatically. Python and the JVM/WebView runtime are installed during that build; normal container startup does not reinstall dependencies.

## Method B — Dockge/Portainer/normal Compose

If you already manage custom stacks with a Compose manager, use the root `docker-compose.yml` instead. It builds the complete AutoSourcarr integrated image locally.

```bash
cp .env.example .env
# edit .env
docker compose up -d
```

## First startup

Open:

```text
Source WebView: AutoSourcarr → Sources → Open WebView
AutoSourcarr admin:     http://TRUENAS-IP:8282
```

The AutoSourcarr health badge should eventually show `source engine online`. The badge is clickable; when it reports an error/warning it opens **Errors & diagnostics** with current dependency state and recent runtime failures. Docker/TrueNAS container health uses `http://127.0.0.1:8282/api/v1/live`, which checks only the local databases and does not repeatedly query external sources. Use `/api/v1/health` when you want the fuller dependency/status check.

### Integrated source process

Use Sources → Discover to manage installed extensions and Sources → Open WebView
for source website login/verification. The engine keeps its own existing database;
your main reading library remains in the main Suwayomi instance.

### AutoSourcarr

Open the **Sources** tab and click **Sync sources**. Each website is grouped into one row; choose one or more languages from its checkbox selector, then adjust priority and the Search/Chapters toggles.


## Optional title translation service (recommended for stronger matching)

AutoSourcarr 0.2.5 can use a small local LibreTranslate sidecar to generate search-only aliases when an origin-language title is missing. In the same manga Compose application add `autosourcarr-translate` and set `TRANSLATION_API_URL=http://autosourcarr-translate:5000` on AutoSourcarr. The supplied TrueNAS and manga-cluster example YAML already contain this service.

The default model set is restricted to `en,ja,ko,zh`. On its first start LibreTranslate may need time to obtain/load those language models. AutoSourcarr does not block normal browsing while the translator is unavailable; translation jobs simply retry on later discovery/manual searches. If you do not want the extra container, remove it and set `TRANSLATION_API_URL: ''`.

Recommended values in `config/autosourcarr.env`:

```dotenv
MANUAL_SEARCH_ALIAS_LIMIT=30
MANUAL_TRANSLATION_QUERY_LIMIT=6
MANUAL_TRANSLATE_CANDIDATE_LIMIT=12
TRANSLATION_API_URL=http://autosourcarr-translate:5000
TRANSLATION_TIMEOUT=5
TRANSLATION_OVERALL_TIMEOUT=7
TRANSLATION_CONCURRENCY=4
TRANSLATION_TARGET_LANGUAGES=ja,ko,zh
BACKGROUND_TRANSLATION_ON_DISCOVERY=true
BACKGROUND_TRANSLATION_ENTRIES_PER_FEED=12
BACKGROUND_TRANSLATION_ENTRIES_PER_STARTUP=40
TRANSLATION_STARTUP_DELAY_SECONDS=45
```

## Main Suwayomi connection

The main Suwayomi does not need network access to the private source-host on port 4568. It needs access only to AutoSourcarr port 8282.

Before building, set:

```dotenv
AUTOSOURCARR_INTERNAL_URL=http://autosourcarr:8282
AUTOSOURCARR_PUBLIC_URL=https://autosourcarr.coobnetwork.com
AUTOSOURCARR_READER_API_TOKEN=<64+ character random secret>
```

The main Suwayomi JAR keeps using `/api/v1/` over the Docker network. The Android APK uses the same public hostname but requests `/api/reader/v1/` and sends `X-AutoSourcarr-API-Key` automatically.

## Authelia rule for Android reader API access

AutoSourcarr 0.2.85 does **not** require an Authelia service-account login. Keep your existing browser/UI rule and add this rule **before it**:

```yaml
access_control:
  rules:
    - domain: autosourcarr.coobnetwork.com
      resources:
        - '^/api/reader/v1([/?].*)?$'
      policy: bypass

    # Existing human/browser policy remains after it.
    - domain: autosourcarr.coobnetwork.com
      policy: two_factor
```

This is safe because the bypass applies only to the dedicated reader prefix. AutoSourcarr validates `AUTOSOURCARR_READER_API_TOKEN` itself, allows only `GET`/`HEAD`, and returns `401` for a missing/wrong token. If the token is blank, the reader prefix returns `503` instead of opening anonymously. **Do not bypass `/api/v1/`, `/api/admin/`, `/`, or the whole domain.**

Usually no NGINX Proxy Manager change is required if the current proxy host already sends every request through Authelia's normal auth-request snippet: Authelia sees the reader path, applies `bypass`, and NPM forwards it to AutoSourcarr. If you created custom NPM locations that skip your normal auth-request include, remove/avoid them; the path-level Authelia rule is sufficient.

Generate the token, for example, with:

```bash
openssl rand -hex 32
```

Put that value in the persistent sibling `autosourcarr/config/autosourcarr.env`, restart/redeploy AutoSourcarr, then rebuild the Android APK. The APK contains the token, so rotate it if the device/APK becomes untrusted.

## Persistent Android signing key

The first 0.2.68+ Android build creates:

```text
/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/config/.extension-signing/autosourcarr-extension.jks
/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/config/.extension-signing/signing.env
```

Back up this directory with the rest of AutoSourcarr's config. Future APK builds are signed with the same certificate and can update the installed extension in place. An APK already installed with an older/different signing key will produce `INSTALL_FAILED_UPDATE_INCOMPATIBLE`; uninstall that old AutoSourcarr extension once, install a current APK, and future updates will work while the signing directory is retained.

## Ports

| Port | Service | Needed by |
|---|---|---|
| 8282 | AutoSourcarr API/Admin | Main Suwayomi + your browser |
| 4567 | Internal source engine | Loopback only; not published |

If those ports conflict with existing Apps, change the host-side port mapping. If the Android-facing URL changes, update `AUTOSOURCARR_PUBLIC_URL` and rebuild/reinstall the APK. The internal Docker port can remain 8282 even when the host-side port differs.

## Updating

### AutoSourcarr source

Copy the complete release contents over the existing `project` directory with overwrite enabled, then use TrueNAS Stop → Start or Save/Redeploy. You do not need to delete the old project first. Keep sibling `config`, `autosourcarr-data`, `source-host`, and `python-deps` directories intact. The extension build stages an explicit current-file list, so obsolete Kotlin files left by an overlay are ignored. Compose performs the build automatically; preserve your persistent mounts. No per-release image-tag change is required.

### Integrated source engine

The source engine version is pinned in `autosourcarr/Dockerfile`. Compose rebuilds automatically when this changes. Keep the `source-host` dataset intact; no separate source-host service is required.

### AutoSourcarr extension

Rebuild when files under `extension/` change, the extension API requires an update, or you want the displayed Suwayomi extension version to match the new AutoSourcarr release. The builder pins the Keiyoushi build-tree revision in `extension/keiyoushi-commit.lock` for reproducible rebuilds. To intentionally refresh that lock, run the builder once with `KEIYOUSHI_UPDATE=1`. Backend-only changes generally do not require a new extension binary for functionality if the public API stays compatible.

## Backup/restore

Back up the persistent configuration and data locations:

```text
/mnt/POOL/apps/autosourcarr/config
/mnt/POOL/apps/autosourcarr/autosourcarr-data
/mnt/POOL/apps/autosourcarr/source-host
```

The `project` folder is replaceable from this source bundle, and `python-deps` is disposable/cacheable.

## Troubleshooting

### AutoSourcarr says source host offline

Check Status → Source engine and the AutoSourcarr container logs. Integrated mode uses `http://127.0.0.1:4567` inside that same container. Confirm the integrated image and existing source data mount; opening a host port is not required.

### Main Suwayomi source opens but pages fail

The Suwayomi JAR uses `AUTOSOURCARR_INTERNAL_URL`; the Android APK uses `AUTOSOURCARR_PUBLIC_URL`. `http://autosourcarr:8282` is Docker-only and will fail on Android with `ERR_NAME_NOT_RESOLVED`. For remote Android access, keep the normal HTTPS hostname behind Authelia, set `AUTOSOURCARR_READER_API_TOKEN`, and bypass only `/api/reader/v1/` in Authelia. The APK supplies the AutoSourcarr API key automatically.

### One underlying source fails

Open that source inside the private Suwayomi and test it directly. Login/WebView/Cloudflare configuration belongs there. AutoSourcarr records per-source browse errors without failing every other source.

### Searches are slow

Reduce enabled Search sources or lower `MAX_SOURCE_CONCURRENCY`. Live discovery intentionally queries enabled sources; known catalog entries are reused on later searches.


## Extension installation: use the JAR

Build with `extension/build.sh`, `extension/build.ps1`, or the existing TrueNAS extension-builder service. AutoSourcarr runs Keiyoushi's release build and publishes the signed release JAR:

```text
extension/dist/AutoSourcarr-v<version>-Suwayomi.jar
```

Install that JAR in the main Suwayomi instance.

### Federated browsing timeouts

`INTERACTIVE_SOURCE_TIMEOUT=4` and `INTERACTIVE_OVERALL_TIMEOUT=6` keep the first uncached browse responsive. A complete background refresh continues with `FEDERATED_SOURCE_TIMEOUT=8` and `FEDERATED_OVERALL_TIMEOUT=14`. Slow sources are skipped for that request and shown in `source_errors`; completed source results are still returned.

### Persistent browsing cache

`BROWSE_CACHE_REFRESH_SECONDS=600` keeps previously acquired browse/search feeds in `catalog.sqlite`. Saved results are returned immediately and, once older than the refresh interval, are refreshed in the background. `BACKGROUND_REFRESH_ON_STARTUP=true` prewarms Popular and Latest after `STARTUP_REFRESH_DELAY_SECONDS` (default 20 seconds); `BROWSE_PREFETCH_PAGES=2` prewarms pages 1 and 2. `BACKGROUND_DEDUP_ON_STARTUP=true` runs a conservative catalog duplicate pass after startup.

Use `COVER_FETCH_CONCURRENCY=4` to cap asynchronous cover retrieval and `MANUAL_SEARCH_TIMEOUT=12`, `MANUAL_SEARCH_OVERALL_TIMEOUT=24`, `MANUAL_SEARCH_ALIAS_LIMIT=30`, and `MANUAL_SEARCH_LIVE_QUERY_LIMIT=4` so a broken source search reports an inline error rather than holding the match editor indefinitely.

v0.2.42 defaults to `MAX_SOURCE_CONCURRENCY=6` with a global private-source-host work cap of 4. If particular extensions produce 522/Cloudflare errors, lower `MAX_SOURCE_CONCURRENCY` to 4 while leaving foreground UI/task execution asynchronous. Cached results keep the UI responsive while background discovery is gentler on upstream sites.


### Multiple records per source and duplicate review

`AUTO_VARIANT_DISCOVERY=true` enables background discovery of additional manga records on sources that are already matched to a unified entry. The defaults intentionally use stricter evidence than normal cross-source matching. Use the Matrix/Subbed **Multiple records on one source** filter to inspect them, or use **Status → Find extra source records** for an on-demand scan. All accepted records contribute chapter candidates.

Potential duplicate catalog entries are reviewed on the dedicated **Duplicates** tab. **Same work — merge** consolidates them; **Different works** stores a persistent non-duplicate decision so the same pair is not repeatedly surfaced. Orange attention cards on Status link directly to the relevant Duplicates, Sources, Subbed, or Matrix view.

## Main Suwayomi connection (Subbed tab)

Set this in the persistent `autosourcarr/config/autosourcarr.env` file:

```dotenv

### Optional downloaded-size Status metric

`MAIN_SUWAYOMI_DOWNLOADS_PATH` is configured for this TrueNAS deployment as `/mnt/manga-downloads`, mounted read-only from `/mnt/hdd/storage/komga/manga`. Status uses this path only to calculate downloaded-data size; AutoSourcarr never writes to it.

MAIN_SUWAYOMI_URL=http://suwayomi:30058
```

The **Subbed** tab reads the main Suwayomi library over GraphQL and shows only manga whose source is AutoSourcarr. It uses the AutoSourcarr entry ID stored in the manga URL, so the mapping does not depend on title matching.

## v0.2.6 dependency/bootstrap recovery

The TrueNAS YAML deployment runs `/app/start-truenas.sh`. It verifies both the
requirements hash and that all required Python modules can actually be imported.
If `/deps` is incomplete or corrupt it is rebuilt automatically. You should not
need to manually delete `python-deps` for a missing-module error.

The extension builder now keeps Gradle under `extension/.gradle-home` inside the
persistent project directory and retries interrupted wrapper downloads. A failed
builder container can simply be recreated/restarted; a successful Gradle download
is reused on later builds.

## Keep service settings outside project/

AutoSourcarr keeps the live env files outside the replaceable release tree:

```text
/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/config/autosourcarr.env
/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/config/source-host.env
/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/config/.extension-signing/
```

Run `scripts/init-truenas-config.sh` after placing v0.2.85 in `project/`. It migrates old `project/config` values/signing state once, adds new variables without overwriting existing values, and generates a persistent reader API token if that setting is blank. After migration you can replace all of `project/` on future upgrades without resetting these values.

For the Settings cog, the AutoSourcarr service also needs the persistent config directory mounted read/write as `/config`:

```yaml
volumes:
  - >-
    /mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/config:/config
```

Use the stable `compose-env-file.yml` helper if you are updating an existing manga-stack Compose. Its filename does **not** contain a version number, so later drag/drop releases overwrite it rather than accumulating copies. Run `scripts/cleanup-old-helper-files.sh` once to remove old `autosourcarr-v*-compose*.yml` files; the extension builder also performs this cleanup automatically.


## Optional separate database drive / pool

By default `catalog.sqlite` and `chapter-origins.sqlite` live in the existing `/data` mount. To place only these growing databases on another TrueNAS dataset/drive, **mount the host dataset into the container first**. For example, add this under the `autosourcarr` service's `volumes:` and redeploy once:

```yaml
- /mnt/OTHERPOOL/apps/autosourcarr/database:/database
```

Then open **Settings → Configuration → Database storage**, enter `/database`, and press **Move databases**. Do not manually set `DATABASE_DIR=/database` while that location is empty. The mover copies the existing live databases, verifies both copies, briefly pauses DB transactions for a final lossless sync, switches the running process immediately, and persists `DATABASE_DIR` for the next container recreation. The old files are kept as a safety copy until the next redeploy verifies the target again and removes them.

A separate `DATABASE_DIR` is fail-closed: if its mount disappears or either expected SQLite file is missing, AutoSourcarr refuses to start instead of letting SQLite create an empty replacement database. Moving within an already mounted `/data` subtree needs no additional TrueNAS/Compose change; moving to another physical pool/drive requires the bind mount above.

## Settings cog

Open the cogwheel at the far right of the AutoSourcarr navigation bar to edit `autosourcarr.env` or `source-host.env`. The editor keeps the file structure/comments and only changes existing variables. After saving, recreate the TrueNAS Custom App for autosourcarr.env changes. Source-host.env is read by the integrated child on restart. If the page reports a file as read-only, the `/config` bind mount or host ACL/permissions are not writable by the AutoSourcarr container.

## TrueNAS Custom App env files

The supplied `env_file:` entries are absolute host paths. TrueNAS/Docker Compose reads them before starting the containers; they are not paths inside the containers. Keep the two persistent files under `autosourcarr/config/` and use the exact paths in `docker-compose.truenas.yml`. The env files are plain `KEY=value`, so they work with the Custom App "Install via YAML" Compose flow without `${...}` interpolation.
