#ifndef AUTOSOURCARR_STARTUP_FRAME_H
#define AUTOSOURCARR_STARTUP_FRAME_H

#include <stddef.h>
#include <stdint.h>

#define AUTOSOURCARR_STARTUP_MAXIMUM 16384U

/* Views borrow the validated caller-owned frame. No environment/path/command is
 * accepted here. Parsing a requested identity never authorizes a Linux syscall. */
struct autosourcarr_startup_request {
    int integrated;
    const unsigned char *uid;
    size_t uid_length;
    const unsigned char *gid;
    size_t gid_length;
};

enum autosourcarr_source_identity_kind {
    AUTOSOURCARR_SOURCE_EXTERNAL = 0,
    AUTOSOURCARR_SOURCE_INHERIT = 1,
    AUTOSOURCARR_SOURCE_SELECTED = 2,
    AUTOSOURCARR_SOURCE_REFUSED = 3
};

struct autosourcarr_source_identity {
    enum autosourcarr_source_identity_kind kind;
    uint32_t uid;
    uint32_t gid;
    int clear_groups;
    int chown_empty_root;
};

int autosourcarr_startup_parse(const unsigned char *frame, size_t length,
    const unsigned char expected_nonce[64], struct autosourcarr_startup_request *request);

/* Pure selection only. The native owner must still verify its actual real /
 * effective/saved IDs, authority, held directory and every syscall result.
 * UINT32_MAX is refused rather than passed as the Linux "unchanged" sentinel.
 * An inherited nonroot identity does not imply groups could be safely cleared. */
int autosourcarr_source_identity_select(const struct autosourcarr_startup_request *request,
    uint32_t real_caller_uid, uint32_t real_caller_gid,
    uint32_t directory_uid, uint32_t directory_gid, int empty_directory,
    struct autosourcarr_source_identity *identity);

#endif
