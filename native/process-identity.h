#ifndef AUTOSOURCARR_PROCESS_IDENTITY_H
#define AUTOSOURCARR_PROCESS_IDENTITY_H

#include <stddef.h>
#include <stdint.h>

/* Fixed native startup only, before a CLR/shell/configuration parser executes.
 * Source owners use no supplementary groups; main may retain one actual KVM GID.
 * This clears authority. It never grants capabilities or changes their ceiling. */
int autosourcarr_drop_identity(uint32_t uid, uint32_t gid,
    const uint32_t *groups, size_t count, int nondumpable);

#endif
