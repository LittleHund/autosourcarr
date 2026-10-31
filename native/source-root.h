#ifndef AUTOSOURCARR_SOURCE_ROOT_H
#define AUTOSOURCARR_SOURCE_ROOT_H

#include "startup-frame.h"
#include <stdint.h>

/* Native finite-bootstrap component only; never accepts a caller-selected path.
 * Captured original IDs precede the parent's temporary resolver RUID568.
 * A held directory/selected identity is not a source-ready or child-exit proof. */
struct autosourcarr_source_root {
    int descriptor;
    uint64_t device;
    uint64_t inode;
    struct autosourcarr_source_identity selected;
    int restricted_empty_root;
    int initialized_empty_root;
};

enum autosourcarr_source_root_result {
    AUTOSOURCARR_SOURCE_ROOT_OK = 0,
    AUTOSOURCARR_SOURCE_ROOT_EXTERNAL = 1,
    AUTOSOURCARR_SOURCE_ROOT_IDENTITY = 2,
    AUTOSOURCARR_SOURCE_ROOT_ACCESS = 3,
    AUTOSOURCARR_SOURCE_ROOT_CHANGED = 4,
    AUTOSOURCARR_SOURCE_ROOT_INITIALIZATION = 5
};

/* Output must be fresh/unowned; close a previous successful result before reuse.
 * Failure may retain the two explicit empty-root mutation facts, but never a
 * usable descriptor. Host-admin/cooperating-mount ownership is a prerequisite;
 * stat checks do not provide an exclusive lock against a hostile host writer. */
int autosourcarr_source_root_prepare(const struct autosourcarr_startup_request *request,
    uint32_t original_uid, uint32_t original_gid, struct autosourcarr_source_root *root);
void autosourcarr_source_root_close(struct autosourcarr_source_root *root);

/* Fixed per-container runtime exchange. This descriptor belongs only to the
 * native owner; neither Main nor the source JVM may inherit it. The main-owned
 * 0700 ancestor prevents the different source UID from traversing this path.
 * Mode 0770 on the child permits main GID568 to consume/unlink issued tickets.
 * The file observation protocol and actual child work are separate owners. */
#define AUTOSOURCARR_SOURCE_OBSERVATION_ROOT "/run/autosourcarr-private/source-observations"
struct autosourcarr_source_observation_root {
    int descriptor;
    uint64_t device;
    uint64_t inode;
    uint64_t ancestor_device;
    uint64_t ancestor_inode;
    uint32_t source_uid;
    int created_ancestor;
    int created_exchange;
};

/* Requires the current successful held source root/selected identity. Prepares
 * only the exact runtime directories above; no caller path or recursive work.
 * Existing owner/mode mismatches fail closed and are never reowned. A rootless
 * 568:568 image must already contain the main-owned 0700 ancestor because /run
 * itself is not writable by 568. No credential change is attempted in that case.
 * Root setup needs CAP_CHOWN only for freshly created nodes and normal finite
 * bootstrap SETUID/SETGID authority, never CAP_FOWNER or CAP_DAC_OVERRIDE.
 *
 * A root bootstrap temporarily uses filesystem UID/GID568 solely to traverse
 * the main-owned ancestor. Before return it restores and checks filesystem IDs,
 * real/effective/saved IDs, capabilities and original dumpability (0 or 1).
 * Failed/interrupted creation facts may remain, but never a usable descriptor.
 * Fresh output required; close a previous result before reuse. */
int autosourcarr_source_observation_root_prepare(uint32_t original_uid, uint32_t original_gid,
    const struct autosourcarr_source_root *source, struct autosourcarr_source_observation_root *root);
void autosourcarr_source_observation_root_close(struct autosourcarr_source_observation_root *root);

#endif
