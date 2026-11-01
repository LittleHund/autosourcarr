# Requirements changes applied 2026-10-29 (to the Docs artifact "AutoSourcarr requirements")

claude/requirements.md is the 2026-10-26 copy; apply these when regenerating it.

Note/text edits (research results, no requirement changed):
- UI-13: "push updates (SignalR)" -> "push updates (Server-Sent Events)".
- CON-2 note: built-in solver = extension host's own browser; FlareSolverr and Byparr via one compatible connection (cookies only); image CAPTCHAs shown to the user in the WebView.
- PIPE-7 note: relevant; extension host built from v2.4.2366 loader and extension-store code.
- PERF-8 note: decided; feeds live updates (UI-13) and notifications (SET-11).
- MAT-6 note: researched; official publisher sources first, then health and quality score.
- READ-1 note: reader extension offered from AutoSourcarr's own repo index (Mihon v0.21+ updates only from own repo).
- CHAP-7: downloads as Series/Chapter.cbz + cover + ComicInfo.xml, readable by Mihon local source, Kavita, Komga.
- SRC-2 note: checks in C#; hash lookup via MalwareBazaar connection.
- Section 17: all research items done (R1-R28); phase 0 only re-checks.

Section 18 proposals (waiting for the user):
- PROP-1 release split: 0.5.0 = conversion + standalone + security essentials; 0.6.0 = GST-1-9, PERF-11, AUTH-7, AUTH-9, SET-10-12, ARC-9/META-6 Postgres, CON-4, SRC-9 benchmark, MAT-21, MAT-7. Cutover ~45 h earlier.
- PROP-2 mark CON-4, SRC-9 benchmark, MAT-21 as Deferred.
- PROP-3 UI-3 drop theme-research clause (-1 h).
- PROP-4 PIPE-9 hand-maintained backup schema instead of auto-derivation (-1 h).
