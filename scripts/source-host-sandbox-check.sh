#!/usr/bin/env bash
# Proof for native/source-host-sandbox.c (phase 13, SRC-5): compiles it, then checks that a program started through it
# can write only in its data folder, cannot read a stand-in for the app's config, reports its Landlock proof, and that
# a kernel without Landlock degrades to "unsandboxed" instead of refusing. Needs gcc and Linux; run as a non-root user.
set -u
repo=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
failures=0
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }

sandbox=$work/source-host-sandbox
gcc -O2 -Wall -Werror -o "$sandbox" "$repo/native/source-host-sandbox.c" || { echo "FAIL compile"; exit 1; }
pass "compiles with -O2 -Wall -Werror"

mkdir -p "$work/data" "$work/config"
echo "sandbox-check-secret" > "$work/config/secret"
proof=$("$sandbox" --data "$work/data" -- /bin/sh -c "echo inside > '$work/data/inside'; echo outside > '$work/outside' 2>/dev/null; cat '$work/config/secret' 2>/dev/null > '$work/data/leak'" 2>&1 >/dev/null | head -1)
echo "proof: $proof"
case "$proof" in AUTOSOURCARR_SOURCE_SANDBOX_V1\ *) pass "first stderr line is the proof";; *) fail "no proof line";; esac
[ -f "$work/data/inside" ] && pass "the data folder is writable" || fail "the data folder is not writable"
if echo "$proof" | grep -q '"landlock":[1-9]'; then
    [ ! -f "$work/outside" ] && pass "a write outside the data folder is refused" || fail "wrote outside the data folder"
    [ ! -s "$work/data/leak" ] && pass "a file outside the allowed paths is unreadable" || fail "read a file outside the allowed paths"
    if echo "$proof" | grep -q '"scoped":2'; then
        # Signal scoping (ABI 6): the sandboxed program cannot signal this shell.
        "$sandbox" --data "$work/data" -- /bin/sh -c "kill -0 $$ 2>/dev/null && echo signalled > '$work/data/signal'" 2>/dev/null
        [ ! -f "$work/data/signal" ] && pass "cannot signal processes outside the sandbox" || fail "signalled a process outside the sandbox"
    else echo "INFO kernel below Landlock ABI 6 or without the signal fix: signal scoping not used ($proof)"; fi
    if echo "$proof" | grep -q '"landlock":\([6-9]\|[1-9][0-9]\)'; then
        # 0.5.3: --signals off/on override the kernel erratum check; the proof says what was used.
        off=$("$sandbox" --signals off --data "$work/data" -- /bin/true 2>&1 | head -1)
        echo "$off" | grep -q '"scoped":0' && echo "$off" | grep -q '"signals":"off"' && pass "--signals off turns signal scoping off" || fail "--signals off: $off"
        on=$("$sandbox" --signals on --data "$work/data" -- /bin/true 2>&1 | head -1)
        echo "$on" | grep -q '"scoped":2' && echo "$on" | grep -q '"signals":"scoped"' && pass "--signals on forces signal scoping" || fail "--signals on: $on"
        echo "$proof" | grep -q '"signals":"\(scoped\|kernel-erratum\)"' && pass "auto reports the kernel erratum state" || fail "auto signals state: $proof"
    fi
    "$sandbox" --signals maybe --data "$work/data" -- /bin/true 2>/dev/null; [ $? -eq 64 ] && pass "an unknown --signals value is refused (usage)" || fail "accepted --signals maybe"
else
    echo "INFO kernel without Landlock: degraded to unsandboxed (expected behaviour, reported in health)"
fi
# A separate user without CAP_SETUID degrades, never refuses.
proof=$("$sandbox" --uid 569 --gid 569 --data "$work/data" -- /bin/true 2>&1 | head -1)
echo "$proof" | grep -q '"separated":0' && echo "$proof" | grep -q 'identity-unavailable' \
    && pass "a separate user without capabilities degrades to the app's user" || fail "identity switch without capabilities: $proof"
"$sandbox" --data relative -- /bin/true 2>/dev/null; [ $? -eq 64 ] && pass "relative paths are refused (usage)" || fail "relative data path accepted"
for bad in "$work/data/.." "$work/data/../config" "$work//data" "$work/./data"; do
    "$sandbox" --data "$bad" -- /bin/true 2>/dev/null; [ $? -eq 64 ] && pass "refused data path $bad" || fail "accepted data path with . or .. components: $bad"
done
echo "source-host-sandbox check: $failures failure(s)"
exit $failures
