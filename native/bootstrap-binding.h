#ifndef AUTOSOURCARR_BOOTSTRAP_BINDING_H
#define AUTOSOURCARR_BOOTSTRAP_BINDING_H

#include "startup-frame.h"
#include "source-owner.h"
#include <stddef.h>
#include <stdint.h>

#define AUTOSOURCARR_BOOT_HEADER_BYTES 192U
#define AUTOSOURCARR_BOOT_PIPE_COUNT 5U
#define AUTOSOURCARR_BOOT_MAXIMUM (AUTOSOURCARR_BOOT_HEADER_BYTES + AUTOSOURCARR_STARTUP_MAXIMUM + AUTOSOURCARR_SOURCE_PROOF_BYTES)
#define AUTOSOURCARR_OWNER_REAP_BYTES 48U

struct autosourcarr_boot_pipe {
    uint64_t device;
    uint64_t inode;
};

/* Pure encoding inputs, NOT credential, fork, pipe or image authority. The native
 * PID1 must obtain these from its own checked syscalls after the final drop, and
 * retain actual child identities until waitpid. No arbitrary executable/path is
 * represented. Pipes correspond exactly to final-main descriptors3 through7. */
struct autosourcarr_boot_binding {
    uint32_t main_pid;
    uint32_t original_uid;
    uint32_t original_gid;
    uint32_t group_count;
    uint32_t kvm_gid;
    unsigned char nonce[64];
    const unsigned char *startup;
    size_t startup_length;
    const unsigned char *expected_owner_proof;
    size_t expected_owner_proof_length;
    struct autosourcarr_boot_pipe pipes[AUTOSOURCARR_BOOT_PIPE_COUNT];
};

/* ASBIND1\n,version1,u32 mainPID,u32 nativePID1,u32 originalUID/GID,reserved0;
 * nonce64 at32; request length/u32 at96; expected proof length/u32 at100;
 * actual group count/u32 at104 and KVM gid/u32 at108; five dev/ino u64 pairs
 * at112..192; then unchanged raw startup request and expected owner proof.
 * External mode has no owner proof and only descriptor6's pipe identity.
 * Output must not overlap the binding, startup or expected-proof buffers.
 * Output length is zero on refusal; output bytes are untouched until validation.
 * A valid frame is never a source-ready or successful native-launch receipt. */
int autosourcarr_boot_binding_encode(const struct autosourcarr_boot_binding *binding,
    unsigned char *output, size_t capacity, size_t *length);

/* ASREAP1\n,raw nonce32,exact ownerPID/u32,actual waitpid status/i32,then EOF.
 * Encoding does not establish reap. The native parent may call this only AFTER
 * waitpid returns its exact owned child, preserving the raw status. */
int autosourcarr_owner_reap_encode(const unsigned char nonce[32], uint32_t owner_pid,
    int32_t actual_wait_status, unsigned char output[AUTOSOURCARR_OWNER_REAP_BYTES]);

#endif
