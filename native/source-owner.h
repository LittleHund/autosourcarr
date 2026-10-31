#ifndef AUTOSOURCARR_SOURCE_OWNER_H
#define AUTOSOURCARR_SOURCE_OWNER_H

#include <stdint.h>
#include "source-observation.h"

/* This callable fork-child entry owns the supplied descriptors after validation.
 * Its caller must _exit(return_value), keep the creating native thread alive,
 * and bind/reap this exact child. It never accepts a path/executable/identity
 * change over the runtime channel. No native/image qualification is implied. */
struct autosourcarr_source_owner_config {
    int root_fd;
    int control_read_fd;
    int response_write_fd;
    int proof_write_fd;
    /* Production-only held runtime exchange. Physical-test entry ignores these
     * fields and cannot dispatch an observation or start a source engine. */
    int observation_root_fd;
    uint64_t observation_root_device;
    uint64_t observation_root_inode;
    int expected_parent;
    uint32_t uid;
    uint32_t gid;
    uint64_t root_device;
    uint64_t root_inode;
    unsigned char boot_nonce[32];
};

#define AUTOSOURCARR_SOURCE_ROOT "/home/suwayomi/.local/share/Tachidesk"
#define AUTOSOURCARR_SOURCE_LAUNCHER "/opt/autosourcarr-source/bin/source-launcher"
#define AUTOSOURCARR_SOURCE_COMMAND_BYTES 56U
#define AUTOSOURCARR_SOURCE_PAYLOAD_MAXIMUM 65536U
#define AUTOSOURCARR_SOURCE_PROOF_BYTES 76U
#define AUTOSOURCARR_SOURCE_RESPONSE_BYTES 120U
#define AUTOSOURCARR_SOURCE_LOG_HEADER_BYTES 64U
#define AUTOSOURCARR_SOURCE_LOG_PAYLOAD_MAXIMUM 448U
#define AUTOSOURCARR_SOURCE_LOG_FRAME_MAXIMUM 512U
#define AUTOSOURCARR_SOURCE_LOG_GAP 1U
#define AUTOSOURCARR_SOURCE_OBSERVATION_EVENT_BYTES 280U
#define AUTOSOURCARR_SOURCE_OBSERVER_REAPED 1U
#define AUTOSOURCARR_SOURCE_OBSERVER_EOF 2U
#define AUTOSOURCARR_SOURCE_OBSERVER_EXIT_ZERO 4U
#define AUTOSOURCARR_SOURCE_OBSERVER_TIMEOUT 8U
#define AUTOSOURCARR_SOURCE_OBSERVER_INVALID 16U

/* All wire integers are unsigned little-endian unless noted. Command header:
 * [0..8) ASOCMD1\n; [8..40) nonce; [40..48) consecutive request ID starting1;
 * [48..50) opcode; [50..52) zero; [52..56) payload byte count.
 * START payload: u16 env_count (<=256), then u16 key length/u32 value length,
 * key/value bytes; no NULs. Empty payload means no configured source overrides.
 * Fixed HOME/PATH/tool selection belongs to the immutable image launcher.
 * Observe payload: generation/u64,kind/u32,ASCII leaf length/u32,then leaf.
 * Fixed-UA kind2 uses an empty leaf. The native owner creates its random ticket.
 * ReleaseSnapshot payload: the exact issued32lowerhex ticket, no caller path. */
enum autosourcarr_source_opcode {
    AUTOSOURCARR_SOURCE_START = 1,
    AUTOSOURCARR_SOURCE_STOP = 2,
    AUTOSOURCARR_SOURCE_STATUS = 3,
    AUTOSOURCARR_SOURCE_SHUTDOWN = 4,
    AUTOSOURCARR_SOURCE_PHYSICAL_SELFTEST = 5,
    AUTOSOURCARR_SOURCE_OBSERVE = 6,
    AUTOSOURCARR_SOURCE_RELEASE_SNAPSHOT = 7
};
enum autosourcarr_source_state {
    AUTOSOURCARR_SOURCE_IDLE = 0,
    AUTOSOURCARR_SOURCE_STARTING = 1,
    AUTOSOURCARR_SOURCE_RUNNING = 2,
    AUTOSOURCARR_SOURCE_STOPPING = 3,
    AUTOSOURCARR_SOURCE_FAULTED = 4
};
enum autosourcarr_source_result {
    AUTOSOURCARR_SOURCE_OK = 0,
    AUTOSOURCARR_SOURCE_UNAVAILABLE = 1,
    AUTOSOURCARR_SOURCE_BUSY = 2,
    AUTOSOURCARR_SOURCE_PROTOCOL_ERROR = 3,
    AUTOSOURCARR_SOURCE_FAILED = 4
};
enum autosourcarr_source_diagnostic {
    AUTOSOURCARR_SOURCE_DIAG_NONE = 0,
    AUTOSOURCARR_SOURCE_DIAG_ASSET = 1,
    AUTOSOURCARR_SOURCE_DIAG_ROOT = 2,
    AUTOSOURCARR_SOURCE_DIAG_PROTOCOL = 3,
    AUTOSOURCARR_SOURCE_DIAG_EXEC = 4,
    AUTOSOURCARR_SOURCE_DIAG_EXIT = 5,
    AUTOSOURCARR_SOURCE_DIAG_SIGNAL = 6,
    AUTOSOURCARR_SOURCE_DIAG_IO = 7,
    AUTOSOURCARR_SOURCE_DIAG_WAIT = 8
};
#define AUTOSOURCARR_SOURCE_FACT_EXIT 1U
#define AUTOSOURCARR_SOURCE_FACT_NO_CHILDREN 2U
#define AUTOSOURCARR_SOURCE_FACT_STREAMS_DRAINED 4U
#define AUTOSOURCARR_SOURCE_FACT_PHYSICAL_TEST 8U
#define AUTOSOURCARR_SOURCE_FACT_FAILED 16U
#define AUTOSOURCARR_SOURCE_FACT_SIGKILL_REAPED 32U

/* Proof: ASOPRF1\n, nonce32, pid/u32, ppid/u32, uid/u32, gid/u32,
 * root_dev/u64, root_inode/u64, profile/u32. profile0=production owner;
 * profile1=physical-test-only owner, which ALWAYS refuses SOURCE_START.
 * Proof is emitted only after real drop/NNP/non-dumpable/subreaper checks.
 * It is not engine/observer/guest readiness or working-source proof.
 *
 * Response header: ASOREP1\n, nonce32, request/u64, opcode/u16, result/u16,
 * payload_length/u32 (=56), generation/u64. Fixed payload56:
 * state/u32, leader_pid/u32, pgid/u32, exit_status/i32, facts/u32,
 * reaped/u32, adopted_reaped/u32, diagnostic/u32, discarded_log_bytes/u64,
 * root_dev/u64, root_inode/u64. A successful STOP is queued only after actual
 * waitpid(-1)==ECHILD and physical stream EOF. Facts remain separate from OK.
 *
 * Asynchronous source output: ASOLOG1\n,nonce32,generation/u64,raw_offset/u64,
 * payload_length/u32(1..448),flags/u32(only GAP=1),then exact payload bytes.
 * Whole frame<=512 is one nonblocking atomic pipe write. Payload is untrusted
 * diagnostic data, never another control frame or readiness evidence. A lost
 * fragment advances raw_offset/discarded_log_bytes and taints that generation;
 * the next delivered fragment carries GAP. Control replies take priority.
 *
 * Observation event280: ASOOBS1\n,nonce32,request/u64,generation/u64,
 * helper_pid/u32,parent_facts/u32,actual_wait_status/i32,zero/u32,ticket32,
 * exact child result176. Reaped/EOF/zero-exit facts are independent native parent
 * observations. This event precedes the normal Observe response. Neither a ticket
 * nor an exit0 alone proves captured bytes or a current named exchange directory.
 */
int autosourcarr_source_owner_run(const struct autosourcarr_source_owner_config *config);

/* Actual bounded native fork/group/adoption probe, never an engine/ready shim.
 * Uses the supplied synthetic held root without the production absolute path;
 * no external executable, data write or observation operation is available. */
int autosourcarr_source_owner_physical_test(const struct autosourcarr_source_owner_config *config);

#endif
