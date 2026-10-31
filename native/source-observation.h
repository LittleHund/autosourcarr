#ifndef AUTOSOURCARR_SOURCE_OBSERVATION_H
#define AUTOSOURCARR_SOURCE_OBSERVATION_H

#include <stdint.h>

/* Fork-only capture component. Native dispatch/lifetime integration is pending.
 * Called only in a trusted native fork child which NEVER execs. The caller
 * retains real PID/reap/EOF and wire ownership, not a success callback. */
#define AUTOSOURCARR_SOURCE_OBSERVATION_JAR_MAXIMUM (32U * 1024U * 1024U)
#define AUTOSOURCARR_SOURCE_OBSERVATION_UA_MAXIMUM (64U * 1024U)
#define AUTOSOURCARR_SOURCE_OBSERVATION_LEAF_MAXIMUM 1024U
#define AUTOSOURCARR_SOURCE_OBSERVATION_TICKET_BYTES 32U
#define AUTOSOURCARR_SOURCE_OBSERVATION_RESULT_BYTES 176U
#define AUTOSOURCARR_SOURCE_STARTUP_TICKETS_MAXIMUM 64U

enum autosourcarr_source_observation_kind { AUTOSOURCARR_OBSERVE_JAR=1, AUTOSOURCARR_OBSERVE_UA=2 };
enum autosourcarr_source_observation_outcome {
    AUTOSOURCARR_OBSERVATION_COMPLETE=1, AUTOSOURCARR_OBSERVATION_MISSING=2,
    AUTOSOURCARR_OBSERVATION_CHANGED=3, AUTOSOURCARR_OBSERVATION_FAILED=4
};
#define AUTOSOURCARR_OBSERVATION_STABLE 1U
#define AUTOSOURCARR_OBSERVATION_ROOT_CURRENT 2U
#define AUTOSOURCARR_OBSERVATION_EXCHANGE_HELD 4U
#define AUTOSOURCARR_OBSERVATION_PUBLISHED 8U

/* Exact little-endian x64 helper result, not a public authenticated event. Parent
 * must establish actual exit/reap/resultEOF independently before any publication.
 * EXCHANGE_HELD attests the bound descriptor, not the path below main's0700ancestor;
 * main must independently match its current named directory to that identity. */
struct autosourcarr_source_observation_result {
    uint32_t outcome,kind,facts,error;
    uint64_t length;
    unsigned char sha256[32];
    uint64_t root_device,root_inode,exchange_device,exchange_inode;
    uint64_t output_device,output_inode,source_device,source_inode,directory_device,directory_inode;
    uint32_t output_uid,output_gid,output_mode,output_links;
    uint32_t source_uid,source_gid,source_mode,source_links;
    uint32_t cleanup,reserved;
};

struct autosourcarr_source_observation_config {
    int root_fd,exchange_fd,expected_parent;
    uint32_t uid,gid,kind;
    uint64_t root_device,root_inode,exchange_device,exchange_inode;
    const char *leaf; /* Validated lowercase.jar leaf; ignored/empty for fixed UA. */
    const char *ticket; /* Exactly32 lowercase hex, parent-generated128bit random. */
};

/* Cleanup has no source-byte/readiness facts. Only the trusted parent's retained
 * created-inode identity may authorize removal; unknown named files remain. */
struct autosourcarr_source_observation_cleanup_result {
    uint32_t outcome,cleanup,error,reserved;
    uint64_t output_device,output_inode;
};

/* Startup epoch only, before Main/source owner exist and after global ECHILD.
 * The dropped Main568 child has metadata/unlink access, never source bytes. */
struct autosourcarr_source_startup_cleanup_config {
    int exchange_fd,expected_parent;
    uint32_t source_uid,source_gid;
    uint64_t exchange_device,exchange_inode,ancestor_device,ancestor_inode;
};
struct autosourcarr_source_startup_cleanup_result {
    uint32_t version,scanned,removed,error;
    uint64_t exchange_device,exchange_inode;
    uint32_t complete,reserved;
};

/* Child owns all read/hash/write/fsync/chmod publication work. It creates the
 * random ticket O_EXCL0600 and closes every writable description before return.
 * No arbitrary caller path, source UID==file owner assertion, CLR, shell, network
 * or source code execution. The controller must physically join cancellation and
 * bound reconciliation of issued tickets, including interrupted partial output.
 * Compilation and modeled I/O faults are checked. Actual parent FD/fork/reap/
 * protocol integration and Linux execution remain prerequisites to app use. */
int autosourcarr_source_observation_capture(const struct autosourcarr_source_observation_config *config,
    struct autosourcarr_source_observation_result *result);

int autosourcarr_source_observation_remove(const struct autosourcarr_source_observation_config *config,
    uint64_t output_device,uint64_t output_inode,
    struct autosourcarr_source_observation_cleanup_result *result);

int autosourcarr_source_observation_startup_cleanup(const struct autosourcarr_source_startup_cleanup_config *config,
    struct autosourcarr_source_startup_cleanup_result *result);

#endif
