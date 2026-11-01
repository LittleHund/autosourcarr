#!/usr/bin/env bash
# Proof for native/repair-runner.c (phase 6e): the runner cannot read /config or the user's
# home, cannot connect anywhere directly, reaches only allowed hosts through the proxy, cannot
# signal the app's other processes, and has a bounded task count.
# Needs: gcc, python3, curl, Linux with Landlock ABI >= 6, a non-root user.
# As root (development containers), run it as another user, for example:
#   setpriv --reuid=nobody --regid=nogroup --clear-groups env HOME=<dir owned by nobody> bash scripts/repair-runner-check.sh
# Without direct egress, chain the stand-in proxy: EGRESS_STANDIN_UPSTREAM=host:port.
# Optional: passwordless sudo to create a real /config/secret (otherwise a stand-in path).
set -u
# Development containers without direct egress may override the hosts (CI keeps the defaults).
allowed_host=${RUNNER_CHECK_ALLOWED_HOST:-example.com}
allowed_text=${RUNNER_CHECK_ALLOWED_TEXT:-example domain}

repo=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d)
failures=0
pids=()
created_config=0
cleanup() {
    for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null; done
    [ "$created_config" = 1 ] && sudo -n rm -rf /config/autosourcarr-runner-check
    rm -rf "$work" "${home_probe:-}"
}
trap cleanup EXIT

pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }

if [ "$(id -u)" = 0 ]; then echo "FAIL run as a non-root user (the runner refuses root)"; exit 1; fi

echo "kernel $(uname -r)"
python3 -I -c 'import ctypes; l = ctypes.CDLL(None, use_errno=True); print("landlock abi", l.syscall(444, None, 0, 1))'

runner=$work/repair-runner
test_runner=$work/repair-runner-test
gcc -O2 -Wall -Werror -o "$runner" "$repo/native/repair-runner.c" || { echo "FAIL compile"; exit 1; }
gcc -O2 -Wall -Werror -DAUTOSOURCARR_TEST -o "$test_runner" "$repo/native/repair-runner.c" || { echo "FAIL compile (test build)"; exit 1; }
pass "compiles with -O2 -Wall -Werror (production and test builds)"

hex=$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')
job=$work/data/source-maintenance/runner-jobs/$hex
mkdir -p "$job" && chmod 700 "$work/data/source-maintenance/runner-jobs" "$job"

# Forbidden material, readable by this user so only the sandbox can be what blocks it.
secret_text="runner-check-secret-$hex"
if sudo -n true 2>/dev/null; then
    sudo -n mkdir -p /config/autosourcarr-runner-check
    echo "$secret_text" | sudo -n tee /config/autosourcarr-runner-check/secret >/dev/null
    sudo -n chmod 755 /config/autosourcarr-runner-check
    sudo -n chmod 644 /config/autosourcarr-runner-check/secret
    created_config=1
    config_secret=/config/autosourcarr-runner-check/secret
else
    mkdir -p "$work/config" && echo "$secret_text" > "$work/config/secret"
    config_secret=$work/config/secret
    echo "INFO no passwordless sudo; $config_secret stands in for /config/secret"
fi
home_probe=$(mktemp -d "$HOME/.autosourcarr-runner-check.XXXXXX")
mkdir -p "$home_probe/.ssh" && echo "$secret_text" > "$home_probe/.ssh/id_check"
data_secret=$work/data/secret.db
echo "$secret_text" > "$data_secret"
for file in "$config_secret" "$home_probe/.ssh/id_check" "$data_secret"; do
    grep -q "$secret_text" "$file" || { echo "FAIL control: $file is not readable outside the runner"; exit 1; }
done

python3 -I "$repo/scripts/egress-proxy-standin.py" --allow "$allowed_host" --port-file "$work/port" --log "$work/proxy.log" &
pids+=($!)
for _ in $(seq 50); do [ -s "$work/port" ] && break; sleep 0.1; done
port=$(cat "$work/port" 2>/dev/null) || { echo "FAIL proxy stand-in did not start"; exit 1; }
echo "INFO stand-in proxy on 127.0.0.1:$port"

# A decoy on another loopback address but the SAME port: Landlock rules are port-only, so
# only the connect gate can stop this one.
python3 -I - "$port" "$work/decoy-hit" <<'PY' &
import socket, sys
s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(("127.0.0.2", int(sys.argv[1]))); s.listen(4)
open(sys.argv[2] + ".ready", "w").close()
while True:
    c, _ = s.accept(); open(sys.argv[2], "w").write("hit"); c.close()
PY
pids+=($!)
for _ in $(seq 30); do [ -e "$work/decoy-hit.ready" ] && break; sleep 0.1; done

run() { # run <label> <runner-binary> <program> [args...]; output in $work/out, $work/err
    local binary=$2; shift 2
    timeout 60 "$binary" "$job" "$port" -- "$@" >"$work/out" 2>"$work/err" </dev/null
}

# Proof line and a clean run.
if run proof "$runner" /usr/bin/true && head -1 "$work/err" | grep -Eq "^AUTOSOURCARR_RUNNER_V1 $hex \{\"pid\":[0-9]+,\"uid\":$(id -u),\"ppid\":[0-9]+,\"pdeathsig\":9,\"landlock\":([6-9]|[1-9][0-9]+),\"nnp\":1,\"seccomp\":2,\"netgate\":1,\"proxy\":$port,\"scoped\":3\}$"; then
    pass "runner starts, prints its isolation proof and propagates exit 0 ($(head -1 "$work/err" | cut -c1-200))"
else
    fail "runner proof line or clean exit: $(head -c 600 "$work/err")"
fi

# a. /config
if ! run a "$runner" /usr/bin/cat "$config_secret" && ! grep -q "$secret_text" "$work/out"; then
    pass "a. cannot read $config_secret ($(grep -v AUTOSOURCARR "$work/err" | head -1))"
else fail "a. read $config_secret"; fi

# b. home and data
if ! run b "$runner" /usr/bin/cat "$home_probe/.ssh/id_check" && ! grep -q "$secret_text" "$work/out"; then
    pass "b. cannot read \$HOME/.ssh-like file ($(grep -v AUTOSOURCARR "$work/err" | head -1))"
else fail "b. read \$HOME/.ssh-like file"; fi
if ! run b2 "$runner" /usr/bin/cat "$data_secret" && ! grep -q "$secret_text" "$work/out"; then
    pass "b. cannot read the data directory outside its job"
else fail "b. read the data directory outside its job"; fi
if run b3 "$runner" /bin/sh -c 'echo inside > "$HOME/note" && cat "$HOME/note"' && grep -q inside "$work/out"; then
    pass "b. the private job directory is writable as HOME"
else fail "b. job directory write: $(head -c 400 "$work/err")"; fi

# c. direct connections
target_ip=$(python3 -I -c 'import socket; print(socket.getaddrinfo("example.com", 443, socket.AF_INET)[0][4][0])' 2>/dev/null || echo 1.1.1.1)
if curl --noproxy '*' -sS -m 10 -o /dev/null --resolve "example.com:443:$target_ip" https://example.com 2>/dev/null; then
    echo "INFO control: direct https://example.com ($target_ip) works outside the runner"
else
    echo "INFO control: direct egress is unavailable outside the runner too; check c relies on the gate log"
fi
if ! run c "$runner" /usr/bin/curl --noproxy '*' -sS -m 10 https://example.com; then
    pass "c. curl --noproxy '*' https://example.com fails ($(grep -v AUTOSOURCARR "$work/err" | head -1))"
else fail "c. direct https://example.com succeeded"; fi
if ! run c2 "$runner" /usr/bin/curl --noproxy '*' -sS -m 10 --resolve "example.com:443:$target_ip" https://example.com \
    && grep -q "refused direct connect to $target_ip:443" "$work/err"; then
    pass "c. direct connect to $target_ip:443 is refused by the connect gate, even with DNS bypassed"
else fail "c. direct connect to $target_ip:443: $(head -c 400 "$work/err")"; fi
if ! run c3 "$runner" /usr/bin/curl --noproxy '*' -sS -m 5 "http://127.0.0.2:$port/" \
    && [ ! -e "$work/decoy-hit" ] && grep -q "refused direct connect to 127.0.0.2:$port" "$work/err"; then
    pass "c. same-port decoy on 127.0.0.2:$port is unreachable (address check, not just Landlock's port rule)"
else fail "c. same-port decoy reached or not refused: $(head -c 400 "$work/err")"; fi

# d. disallowed host through the proxy
if ! run d "$runner" /usr/bin/curl -x "http://127.0.0.1:$port" -sS -m 20 https://example.org \
    && grep -q "403" "$work/err" && grep -q "REFUSED example.org:443" "$work/proxy.log"; then
    pass "d. https://example.org through the proxy is refused with 403"
else fail "d. example.org through the proxy: $(head -c 400 "$work/err")"; fi

# e. allowed host through the proxy
if run e "$runner" /usr/bin/curl -x "http://127.0.0.1:$port" -sS -m 30 "https://$allowed_host/" && grep -qi "$allowed_text" "$work/out" \
    && grep -q "ALLOWED $allowed_host:443" "$work/proxy.log"; then
    pass "e. https://$allowed_host through the proxy succeeds"
else fail "e. example.com through the proxy: $(head -c 600 "$work/err")"; fi

# h. a JVM (the real repair workload) starts, uses the proxy from JAVA_TOOL_OPTIONS and is gated.
java_bin=$(command -v java 2>/dev/null && readlink -f "$(command -v java)")
java_bin=${java_bin##*$'\n'}
if [ -n "$java_bin" ]; then
    cat > "$job/Fetch.java" <<'JAVA'
import java.io.*; import java.net.*; import java.net.http.*;
public class Fetch { public static void main(String[] a) throws Exception {
    var r = HttpClient.newBuilder().proxy(ProxySelector.getDefault()).build()
        .send(HttpRequest.newBuilder(URI.create(a[0])).build(), HttpResponse.BodyHandlers.ofString());
    System.out.println("status " + r.statusCode());
    try (var s = new Socket()) { s.connect(new InetSocketAddress(a[1], 443), 5000); System.out.println("DIRECT-CONNECTED"); }
    catch (IOException e) { System.out.println("direct refused"); }
    try (var f = new FileInputStream(a[2])) { f.read(); System.out.println("SECRET-READ"); }
    catch (IOException e) { System.out.println("secret refused"); }
}}
JAVA
    if run h "$runner" "$java_bin" "$job/Fetch.java" "https://$allowed_host/" "$target_ip" "$config_secret" \
        && grep -q "^status 200" "$work/out" && grep -q "^direct refused" "$work/out" && grep -q "^secret refused" "$work/out"; then
        pass "h. a JVM ($java_bin) fetches through the proxy, cannot connect directly, cannot read $config_secret"
    else fail "h. JVM run: $(head -c 300 "$work/out") $(grep -v '^Picked up' "$work/err" | head -c 600)"; fi
else
    echo "INFO no java on PATH; JVM check skipped"
fi

# f. a host without Landlock signal scoping (ABI 4/5) is refused, with no fallback
if AUTOSOURCARR_TEST_FORCE_ABI=5 timeout 30 "$test_runner" "$job" "$port" -- /usr/bin/true >/dev/null 2>"$work/err"; then
    fail "f. forced ABI 5 ran the program"
else
    code=$?
    if [ "$code" = 78 ] && grep -q "no isolation fallback" "$work/err"; then pass "f. forced Landlock ABI 5 exits 78 (no isolation fallback)"
    else fail "f. forced ABI 5 exit $code: $(head -c 300 "$work/err")"; fi
fi
if AUTOSOURCARR_TEST_FORCE_ABI=5 timeout 30 "$runner" "$job" "$port" -- /usr/bin/true >/dev/null 2>&1; then
    pass "f. the production build ignores AUTOSOURCARR_TEST_FORCE_ABI"
else fail "f. the production build honoured AUTOSOURCARR_TEST_FORCE_ABI"; fi

# i. a same-uid process outside the sandbox (standing in for the app) cannot be signalled:
#    once with both layers, once with the seccomp signal gate alone (test build, no Landlock scope).
sleep 300 &
victim=$!
pids+=($victim)
signal_check() { # signal_check <label> <runner-binary> [env...]
    local label=$1 binary=$2; shift 2
    env "$@" timeout 30 "$binary" "$job" "$port" -- /bin/sh -c "kill -9 $victim; /usr/bin/kill -9 $victim; exit 0" >"$work/out" 2>"$work/err" </dev/null
    if kill -0 "$victim" 2>/dev/null; then pass "i. cannot signal a same-uid process outside the sandbox ($label: $(grep -v AUTOSOURCARR "$work/err" | head -1))"
    else fail "i. $label killed the outside process: $(head -c 400 "$work/err")"; fi
}
signal_check "Landlock scope + seccomp gate" "$runner"
signal_check "seccomp gate alone" "$test_runner" AUTOSOURCARR_TEST_NO_SIGNAL_SCOPE=1
grep -q "refused signal to pid $victim outside the repair run" "$work/err" \
    && pass "i. the seccomp gate itself refused the signal (logged)" || fail "i. no gate refusal logged: $(head -c 400 "$work/err")"

# j. signals inside the run still work (kill of its own children goes through the gate) and the
#    task ceiling holds: a fork loop is cut off, then cleans up after itself.
cat > "$job/forks.py" <<'PY'
import os, signal, sys, time
children, failed = [], 0
for _ in range(int(sys.argv[1])):
    try:
        pid = os.fork()
    except OSError:
        failed += 1
        continue
    if pid == 0:
        time.sleep(60)
        os._exit(0)
    children.append(pid)
for pid in children:
    os.kill(pid, signal.SIGKILL)
reaped = sum(1 for pid in children if os.WIFSIGNALED(os.waitpid(pid, 0)[1]))
os.kill(os.getpid(), 0)
print(f"started {len(children)} failed {failed} killed {reaped}")
PY
if run j "$runner" /usr/bin/python3 -I "$job/forks.py" 50 && grep -q "^started 50 failed 0 killed 50$" "$work/out"; then
    pass "j. the program can fork and signal its own children ($(cat "$work/out"))"
else fail "j. in-run fork/signal: $(cat "$work/out") $(head -c 400 "$work/err")"; fi
if AUTOSOURCARR_TEST_NPROC_BUDGET=24 timeout 60 "$test_runner" "$job" "$port" -- /usr/bin/python3 -I "$job/forks.py" 200 >"$work/out" 2>"$work/err" </dev/null \
    && read -r _ started _ failed _ killed < "$work/out" && [ "$failed" -gt 0 ] && [ "$started" -lt 200 ] && [ "$killed" = "$started" ]; then
    pass "j. RLIMIT_NPROC bounds a fork loop (test budget 24: $(cat "$work/out"))"
else fail "j. NPROC bound: $(cat "$work/out") $(head -c 400 "$work/err")"; fi

# Workspace rules.
chmod 755 "$job"
if ! run g "$runner" /usr/bin/true && grep -q "private (0700)" "$work/err"; then pass "workspace must be 0700"; else fail "non-private workspace accepted"; fi
chmod 700 "$job"
if ! timeout 10 "$runner" "$work/data" "$port" -- /usr/bin/true >/dev/null 2>&1; then pass "workspace must be under source-maintenance/runner-jobs/<32 hex>"
else fail "workspace scope not enforced"; fi
if ! timeout 10 "$runner" "$job" 80 -- /usr/bin/true >/dev/null 2>&1; then pass "proxy port below 1024 refused"; else fail "proxy port 80 accepted"; fi

echo "proxy log:"; sed 's/^/  /' "$work/proxy.log" 2>/dev/null
if [ "$failures" -ne 0 ]; then echo "repair-runner-check: $failures failure(s)"; exit 1; fi
echo "repair-runner-check: all checks passed"
