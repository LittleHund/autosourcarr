# User guide

[Back to the overview](../README.md) · [Installation](INSTALLATION.md)

## Browse and manage your library

AutoSourcarr searches enabled sources and keeps a local catalogue. Browsing does
not subscribe entries in your main Suwayomi library. Add a title through your
reader when you want it in the library. **Subbed/Library** shows subscribed entries;
**Wanted**, the download queue and **Tasks** expose outstanding work and progress.

Use **Manage** for entry details, serving choices, metadata and IDs. **Match
matrix**, **Duplicates** and **Mixed entries** help review source bindings. Manual
title and match choices remain saved; automatic matching cannot replace a locked
manual match. Review or reset specific decisions through **Manual actions**.

Automatic title matching has a **0.94 minimum**, plus edition, creator and original
source-member checks. Similar names alone do not join different editions, colored
and monochrome releases, adaptations or animated and still galleries. Translated
titles help searching; they are not independent proof that two works are identical.

Chapter rows show the candidate that would serve next and its alternatives.
Source priority, completeness, official preference and optional quality samples
inform selection. Rejected candidates remain rejected until restored. Served
origin history remains recorded when a later read uses a different candidate.
AutoSourcarr does not automatically replace chapters already downloaded in your
main Suwayomi library. Quality analysis is on demand.

## Import a reading-library backup

Open **Imports** and upload a Mihon/Tachiyomi backup. The import adds to the main
Suwayomi library and separates Added, Existing, Unresolved, Failed and Skipped
results. Existing-library and saved manual mappings are checked before discovery.

Review unresolved entries using the original title, source, summary and cover.
Choose **Match**, search again, retry or skip. A high suggestion score alone is not
an accepted match. Reject an incorrect accepted result to reopen review; that
rejection and saved manual import choices survive normal catalogue rebuilds.

## Gallery matching

Open **Sources → Installed → Gallery matching** for a source. Each source language
has its own settings; an explicit gallery profile supports providers that are not
detected automatically. Shared records also expose **Gallery rules**.

- **Combining on:** compatible numbered uploads become corresponding chapters or
  parts. Unnumbered uploads retain independent chapter slots.
- **Combining off:** distinct galleries stay separate. Existing cached/shared
  groups are adjusted through a visible Task.
- **Criteria:** select artist/author, circle/group, parody/franchise, language,
  category and informative tag overlap. Set the title minimum from 94–100% and,
  when used, shared-tag count/Jaccard requirements. All selected automatic checks
  need evidence on both records.

Edition, animation, original-member and saved **Separate** checks remain enforced.
Rule changes preserve chapter pages, served history and reader aliases through
the existing split/merge recovery process. Failed layout Tasks retain the saved
rule for explicit retry; restoring an old manual group cannot bypass an off rule.

## Source health

**Sources → Health** lists installed sources, including disabled installed
languages. Use **Check now** or **Check enabled sources**, and switch daily checks
on/off there. Daily checks sample enabled installed sources, wait for browser idle
and reserve interactive capacity. Progress and cancellation appear in Tasks.

Results show the last check, last successful sample, checked package version and
last installed repair. Samples cover catalogue/search, one title's metadata and
chapters, a cover and a chapter page. They do not prove every title works.
Timeouts, local capacity waits, verification, empty results and rate limits remain
separate outcomes. Installation, a passing sample and your **Fixed** confirmation
have different dates. Recheck after source updates or repairs.

## Source code and repairs

**Sources → Repair AI** contains provider/account setup, login status and details.
**Tasks → Repairs** owns code inputs, readiness, jobs, evidence and repair actions.
Health checks do not enable AI repairs or use a provider's allowance.

### Retained original code

Normal installs and updates attempt to retain the original ecosystem code and
build files at an immutable revision. Keiyoushi's declared source project is
[keiyoushi/extensions-source](https://github.com/keiyoushi/extensions-source).
Other repositories can expose an unambiguous original-publisher code declaration;
unsupported hosts or missing/ambiguous code require input.

Public snapshots live in `/data/sources/raw`. Two instances may share that
**raw-only** directory. Keep their full `/data`, `/config`, installed binaries,
profiles, accounts and repair histories separate. Uploaded, reconstructed and
repaired code is private by default under `/data/source-maintenance/raw-private`.

Use **Tasks → Repairs → Source code archive → Find upstream code** to retry lookup
or select the original ecosystem for a local installation. A matching package name
or current repository HEAD is not proof of the installed version's original code.
Historical acquisition is bounded and retains candidate provenance.

When code is missing, choose **Provide code / Unavailable**. Supply the original
project ZIP or original GitHub/GitLab repository, with its commit/module if known.
Remove private settings, accounts and signing material first. Other hosts can
provide a ZIP. Choosing **Unavailable — reconstruct installed source** explicitly
requests reconstruction from the saved binary; generated Java may need correction
or fail to compile. The exact original binary remains separate for rollback.

### Repair workflow

1. Run **Tasks → Repairs → Check repair tools**. Isolation must pass before code
   execution; see [host permissions](../security/repair-host-setup.md) for the
   current release's host requirements.
2. Select the installed source, describe the failure and choose **Back up and
   check source**. Missing-code requests wait for your input without occupying an
   AI/build slot.
3. The worker uses retained code, checks website/API locations and reviews
   original-maintainer and first-party evidence for moves. Search hits or a valid
   HTTPS certificate alone do not establish a trustworthy replacement.
4. Candidate code is built and checked in isolation, separately from the live
   source, app configuration, library, accounts and other repair jobs. A compile
   does not establish a working source; native samples also check catalogue/search,
   metadata, chapters, covers and page images.
5. Eligible new repairs install after validation and final identity/hash checks.
   Ambiguous address moves wait for exact-candidate approval through **Install
   validated repair**. Installation retains rollback bytes and recovery history.

| Action | Result |
| --- | --- |
| **Fixed** | Confirms the currently installed repair and code as the last functioning version. |
| **Doesn't work / Retry** | Records feedback and starts one explicit attempt from retained code; unconfirmed code is not promoted. |
| **Revert** | Restores the preceding/last working binary and code reference. Reverting after Fixed, before another fix starts, also undoes that promotion. |

Old jobs are history, not automatically replayed attempts. A later repair uses the
confirmed version as its new working anchor. Source updates invalidate stale
hash/revision actions. Unknown initial working status stays unknown; waiting or
compiled candidates are not marked fixed.

Optional **Check missing sources daily** is separate from Health's daily samples.
It allows at most one new repair per day and one attempt per source each week.
Public code, problem descriptions and feedback use your selected AI provider's
allowance. Repairs do not run an unlimited automatic retry loop.

## Sharing catalogues

No central catalogue, account or server is required. Use **Database sharing** to
combine entries and saved manual decisions from two or more independent instances:

1. **Export** on each instance. Select manual decisions if wanted. Large exports
   use consecutive `.ascatalog` files; download all parts.
2. On the receiver, select each file, preview it, then choose **Add the previewed
   data**. Repeat for the other files/instances.
3. Review **Retained choices** and **Incoming versions**. Conflicting metadata and
   decisions stay reviewable. Structural source/group changes require local
   review through Tasks.

| Identity | Meaning across instances |
| --- | --- |
| Local entry `42` | Belongs only to that instance. Another instance's `42` cannot replace it. |
| `asc1:entry:autosourcarr:<UUID>` | An entry identity retained through export/import; independently created entries can have different UUIDs. |
| `asc1:record:tachiyomi:…` | An exact stable source ID and public source path. The same anchor maps records across instances. |
| Gallery/provider references | Public work, edition or gallery references. Work-only references never merge editions by themselves. |

Titles, discovery order and foreign Suwayomi numeric IDs are not identity proof.
Exact source overlap maps existing entries; ambiguous associations stay separate
until locally reviewed. Reimport is idempotent, including A → B → C → A exchanges.
Corrections retain history instead of silently relabelling an existing identity.
IDs remain opaque strings; they are not JavaScript numbers.

Manual titles, match/group choices, Separate decisions, import mappings/rejections
and identity review choices can travel with their decision identities. Existing
local metadata and decisions remain authoritative. Incoming positive identity
confirmations need fresh local review. Imported source facts remain unverified
and do not install a source, create a subscription or download chapters.

Exports exclude accounts, credentials, configuration, reading progress, downloads,
source profiles, extension binaries and raw code. Public titles, references,
metadata and selected decisions are intentionally included. Recognised secrets in
free text are redacted; review files before sharing. Import/export does not contact
other instances automatically. Files use bounded typed UTF-8 JSON, not SQL; each
file is limited to 64 MiB.

**Manage → IDs**, header Search and **Identity review** expose portable identities.
Lookup can return several editions and never merges them. Ordinary catalogue
rebuild retains dormant IDs/choices; explicit full data reset clears catalogue
data. Encrypted instance backups include exchange history and local reviews, but
are replacement restores rather than additive sharing.

## Diagnostics and recovery

Open **Status → Download diagnostic logs** for one redacted file with bounded app,
integrated engine and tool logs. It excludes configuration, accounts, signing keys,
cookies, private repair conversations/patches and raw source packages. Review any
free text before sharing. Host, reverse proxy and other-container logs may still
need collecting separately.

**Evidence & session** on a repair gives its retained state and checks. **Download
source inputs** deliberately exports selected original JAR/APK files and hashes;
these are unredacted source packages and should be shared privately.

Keep a consistent backup of config, both application databases and persistent
data. See [installation and backup guidance](INSTALLATION.md#upgrading-and-backups)
before upgrades, relocations, replacement restores or resets. Release-specific
changes and known validation limits are in [CHANGELOG](../CHANGELOG.md).
