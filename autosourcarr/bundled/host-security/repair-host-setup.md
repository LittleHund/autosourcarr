# Repair host permissions

These host instructions apply when source-repair isolation is blocked. In 0.3.33,
ChatGPT account setup and every API connection check work without repair namespace
permissions or Compose changes. The reported source-execution blocker remains open.

All seven AI connections use the same isolated repair runtime. A namespace denial
occurs before source editing, compilation or ChatGPT device authentication. API-key
connection tests do not execute source code and can run independently.

The image supplies the tools, non-root default user and no-new-privileges. Docker
and the host enforce the outer namespace, seccomp and AppArmor permissions. An
application cannot change those inherited permissions itself.

## Apply the supplied seccomp policy

These files are public deployment policies, not account data. Put the `security`
folder in your extracted project folder on the TrueNAS host. The example below uses
the existing AutoSourcarr project path; change it if your project is elsewhere.
Keep your existing Compose, ports, mounts, env files, user overrides and data.

Merge this shared fragment into the saved Custom App Compose:

```yaml
x-autosourcarr-security: &autosourcarr-security
  cap_drop: [ALL]
  security_opt:
    - seccomp=/mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/project/security/repair-seccomp.json
    # Enable an AppArmor override only after the host checks below succeed:
    # - apparmor=autosourcarr-internal
    # For a private /proc mount denial, see the conditional setting below:
    # - systempaths=unconfined

services:
  autosourcarr:
    <<: *autosourcarr-security
    # Keep all existing settings for this service.
  private-autosourcarr:
    <<: *autosourcarr-security
    # Omit if absent; keep this instance's existing settings.
```

This is a merge example, not a replacement deployment. Apply it only to actual
AutoSourcarr services; keep Suwayomi as it is. A service's explicit security_opt
overrides the anchor: consolidate it into the shared block, retaining other security
options. If you already use a merge list, put this alias **first**:
`<<: [*autosourcarr-security, *existing-defaults]`. Earlier mappings win. Remove
conflicting inherited security_opt entries or consolidate them into the shared
block, and verify the effective service options include both supplied policies.
Save/Redeploy to recreate the affected containers.

## AppArmor hosts: verify before selecting the custom profile

If the repair check reports `docker-default` and denies namespace/mount creation,
load and verify the custom profile in the **TrueNAS host Shell**:

```bash
sudo apparmor_parser -r /mnt/ssd/apps/komga/automation-apps/manga/autosourcarr/project/security/autosourcarr-internal.apparmor
printf 'Load exit code: %s\n' "$?"
sudo grep -F 'autosourcarr-internal ' /sys/kernel/security/apparmor/profiles
```

The parser is normally silent. Require exit code 0 and a matching enforced-profile
entry before uncommenting `apparmor=autosourcarr-internal` in the shared block.
If loading fails or the profile is absent, keep the override commented and inspect
the host error. A host without AppArmor does not use this override. Reload the
profile after reboot using the TrueNAS version's supported initialization setup.

An OCI startup error containing `apparmor failed to apply profile: write
/proc/thread-self/attr/apparmor/exec: no such file or directory` occurs before the
application starts. It can indicate a profile that is not registered; the path in
that write error alone does not prove the kernel interface is absent. Remove the
explicit AppArmor override to restore Docker's normal profile selection, retain
the supplied seccomp policy, and Save/Redeploy. Then verify the profile and effective
container security options before enabling it again. This startup recovery does
not bypass the app's mandatory repair sandbox check or certify source execution.

Run **Check repair tools** again. A successful check must say isolation is available;
NoNewPrivs=1 alone is not enough. On AppArmor hosts the active profile should be
autosourcarr-internal, not docker-default. Start a new repair only after the check
passes. Follow its progress and evidence in **Tasks**. Existing blocked jobs remain
historical evidence and are not retried automatically. A working ChatGPT login
does not require signing in again. API connection and repair readiness are separate checks.

## If creation is still denied

### Private procfs mount denied with the repair profile active

The reported `bwrap: Can't mount proc on /newroot/proc: Operation not permitted`
occurs while creating the sandbox's private process filesystem. An active
`autosourcarr-internal (enforce)` label confirms that the custom profile was
selected. Docker's default masked/read-only proc paths are another possible
blocker: the kernel can refuse a nested procfs mount that would expose covered
entries. The error alone does not identify every applied host restriction.

For this proc-specific failure, add this **one entry to the existing shared
AutoSourcarr `security_opt` list** in the saved TrueNAS Custom App YAML:

```yaml
    - systempaths=unconfined
```

Retain the supplied seccomp entry and your already loaded AppArmor profile,
`cap_drop: [ALL]`, and the image's non-root/no-new-privileges defaults. Apply it
only to AutoSourcarr instances, then Save/Redeploy to recreate those containers.
This Docker option removes the default masked/read-only paths under both `/proc`
and `/sys`; it changes that outer protection and must not replace seccomp or
AppArmor confinement. The app continues to require a private PID namespace and
fresh procfs for every repair; it does not bind the application's `/proc` into
repair code. No app-only change can remove Docker's inherited locked mounts.

Verify the saved option was applied in the **TrueNAS host Shell** (omit a missing
instance name):

```bash
sudo docker inspect --format '{{.Name}} user={{.Config.User}} masked={{json .HostConfig.MaskedPaths}} readonly={{json .HostConfig.ReadonlyPaths}} caps={{json .HostConfig.CapDrop}}' autosourcarr private-autosourcarr
```

The masked and readonly lists should be empty. Compose translates this option
into those lists, so it may not appear in `.HostConfig.SecurityOpt`. In **Tasks →
Repairs**, run **Check repair tools** again. The complete sandbox probe must pass
before starting a new repair. If it still fails, inspect the denial at that time;
do not keep adding permissions based only on `Seccomp=2`.

New diagnostics report counts of known proc masks and read-only proc overrides.
Zero counts do not rule out other mounts/policies; missing, malformed or oversized
mount data is `not reported`. Raw mount tables, host paths and mount sources are
excluded from diagnostic downloads. The supplied log predates these extra facts,
so the proc-mask diagnosis remains an inference until checked on the host.

### Other namespace or mount denials

The diagnostics do not identify the exact denying layer. Seccomp=2 means filtering
is active; it does not prove which policy is loaded. A positive max_user_namespaces
does not grant permission. A missing setting means not reported, not disabled.

The host administrator should verify Docker's applied security options and inspect
the host kernel/AppArmor denial log at the time of a new check. The app also reports
the optional apparmor_restrict_unprivileged_userns and
apparmor_restrict_unprivileged_userns_force values when the kernel exposes them.
Forced user-namespace mediation or a policy ABI override may need a host-compatible
profile review; these settings are not evidence of the reported cause by themselves.
The profile retains ABI 3 compatibility with Docker's Unix-socket behavior.

Repair stays disabled until the full sandbox test passes. Normal library features
remain available. No privileged mode, unconfined seccomp/AppArmor profile,
SYS_ADMIN, Docker socket or helper service is required by these instructions.

Official references (Docker policy documentation rechecked 2026-10-11):

- https://docs.docker.com/engine/security/apparmor/
- https://docs.docker.com/engine/security/seccomp/
- https://github.com/opencontainers/runc/issues/3831
- https://apps.truenas.com/managing-apps/installing-custom-apps/
- https://github.com/containers/bubblewrap/blob/v0.9.0/bubblewrap.c
- https://github.com/moby/buildkit/blob/master/docs/rootless.md#docker
- https://github.com/docker/compose/blob/main/pkg/compose/create.go
- https://github.com/torvalds/linux/blob/master/fs/namespace.c
- https://gitlab.com/apparmor/apparmor/-/wikis/unprivileged_userns_restriction
