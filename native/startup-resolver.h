#ifndef AUTOSOURCARR_STARTUP_RESOLVER_H
#define AUTOSOURCARR_STARTUP_RESOLVER_H

#include "startup-frame.h"
#include <signal.h>
#include <stddef.h>
#include <stdint.h>

#define AUTOSOURCARR_RESOLVER_DOTNET "/usr/share/dotnet/dotnet"
#define AUTOSOURCARR_RESOLVER_ASSEMBLY "/app/AutoSourcarr.Bootstrap.dll"
#define AUTOSOURCARR_RESOLVER_ASSET_ROOT "/app"
#define AUTOSOURCARR_RESOLVER_BUDGET_MS 30000U
#define AUTOSOURCARR_RESOLVER_TERM_MS 2000U
#define AUTOSOURCARR_RESOLVER_KILL_JOIN_MS 2000U

enum autosourcarr_resolver_status {
    AUTOSOURCARR_RESOLVER_SUCCESS = 0,
    AUTOSOURCARR_RESOLVER_REFUSED = 1,
    AUTOSOURCARR_RESOLVER_FAILED = 2,
    AUTOSOURCARR_RESOLVER_CANCELLED = 3,
    AUTOSOURCARR_RESOLVER_UNRESOLVED = 4
};
enum autosourcarr_resolver_diagnostic {
    AUTOSOURCARR_RESOLVER_DIAG_NONE = 0,
    AUTOSOURCARR_RESOLVER_DIAG_PARENT = 1,
    AUTOSOURCARR_RESOLVER_DIAG_ENVIRONMENT = 2,
    AUTOSOURCARR_RESOLVER_DIAG_NONCE = 3,
    AUTOSOURCARR_RESOLVER_DIAG_PIPE = 4,
    AUTOSOURCARR_RESOLVER_DIAG_FORK = 5,
    AUTOSOURCARR_RESOLVER_DIAG_CHILD = 6,
    AUTOSOURCARR_RESOLVER_DIAG_PROTOCOL = 7,
    AUTOSOURCARR_RESOLVER_DIAG_DEADLINE = 8,
    AUTOSOURCARR_RESOLVER_DIAG_CANCEL = 9,
    AUTOSOURCARR_RESOLVER_DIAG_WAIT = 10,
    AUTOSOURCARR_RESOLVER_DIAG_SIGNAL = 11,
    AUTOSOURCARR_RESOLVER_DIAG_CLOCK = 12,
    AUTOSOURCARR_RESOLVER_DIAG_AUTHORITY = 13
};
enum autosourcarr_resolver_child_stage {
    AUTOSOURCARR_RESOLVER_CHILD_DROP = 1,
    AUTOSOURCARR_RESOLVER_CHILD_PARENT = 2,
    AUTOSOURCARR_RESOLVER_CHILD_ASSETS = 3,
    AUTOSOURCARR_RESOLVER_CHILD_DESCRIPTORS = 4,
    AUTOSOURCARR_RESOLVER_CHILD_EXEC = 5
};
#define AUTOSOURCARR_RESOLVER_FACT_CHILD_VERIFIED 1U
#define AUTOSOURCARR_RESOLVER_FACT_EXEC_CONFIRMED 2U
#define AUTOSOURCARR_RESOLVER_FACT_FRAME_EOF 4U
#define AUTOSOURCARR_RESOLVER_FACT_STDERR_EOF 8U
#define AUTOSOURCARR_RESOLVER_FACT_ERROR_EOF 16U
#define AUTOSOURCARR_RESOLVER_FACT_REAPED 32U
#define AUTOSOURCARR_RESOLVER_FACT_PARENT_DROPPED 64U
#define AUTOSOURCARR_RESOLVER_CLEANUP_WAIT 1U
#define AUTOSOURCARR_RESOLVER_CLEANUP_SIGNAL 2U
#define AUTOSOURCARR_RESOLVER_CLEANUP_PIPE 4U
#define AUTOSOURCARR_RESOLVER_CLEANUP_CLOCK 8U
#define AUTOSOURCARR_RESOLVER_CLEANUP_AUTHORITY 16U
#define AUTOSOURCARR_RESOLVER_CLEANUP_MASK 32U

struct autosourcarr_startup_resolver_result {
    enum autosourcarr_resolver_status status;
    enum autosourcarr_resolver_diagnostic diagnostic;
    uint32_t facts;
    uint32_t cleanup_errors;
    int child_pid;
    int wait_status; /* Actual waitpid status, meaningful only with FACT_REAPED. */
    uint16_t child_error_stage;
    uint16_t child_error_errno; /* Value-safe native error facts, never stderr text. */
    int parent_drop_errno;
    uint64_t discarded_stderr_bytes;
    unsigned char nonce[64]; /* Lowercase hex from kernel random, private boot binding. */
    size_t frame_length;
    unsigned char frame[AUTOSOURCARR_STARTUP_MAXIMUM];
};

/* Fixed native startup transaction, not a general process API. Run on a stable
 * native creating thread in the single-threaded native startup process, before
 * other managed/source work. No SIGCHLD auto-reaper or competing waiter may reap
 * this expected child. Capture legacy caller real UID/GID BEFORE the caller's
 * temporary RUID568 change; this function never chooses the legacy identity.
 * Actual parent must be RUID568 with E/S0/0 or568/568.
 * It requires no CAP_KILL and never regains root. All argv/assets are fixed;
 * inherited CWD is preserved for the canonical .env/relative configuration loader.
 *
 * envp is an explicit NUL-terminated vector (non-NULL); it is snapshotted with
 * loader injection keys removed, order/case/value of settings otherwise retained.
 * Cancellation is optional sig_atomic_t written by this native thread's signal
 * handler. No arbitrary callback, executable, path or child factory is accepted.
 *
 * On ANY non-success the caller must stop bootstrap and immediate _exit(78), not
 * retry/reuse the frame or continue as a broker. Failure attempts full568 drop.
 * UNRESOLVED specifically means actual reap/EOF/authority could not be established
 * after30s + TERM2s + KILL/join2s. It is never a clean receipt. Even if the shared
 * drop itself was impossible, the caller MUST immediately exit the instance.
 * Static/otherwise pre-sanitized native caller is a prerequisite: filtering here
 * cannot undo a native loader hook that executed before this function began.
 */
enum autosourcarr_resolver_status autosourcarr_startup_resolve(
    const char *const envp[], const volatile sig_atomic_t *cancelled,
    struct autosourcarr_startup_resolver_result *result);

/* Produces borrowing views only from a successful complete result. Reparse after
 * copying/moving the result; never retain views into an earlier result address.
 * A valid requested identity is not approval for native UID/GID syscalls. */
int autosourcarr_startup_resolver_request(const struct autosourcarr_startup_resolver_result *result,
    struct autosourcarr_startup_request *request);

#endif
