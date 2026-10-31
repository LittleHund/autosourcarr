#define _GNU_SOURCE
#include "source-root.h"
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <linux/capability.h>
#include <stddef.h>
#include <string.h>
#include <sys/fsuid.h>
#include <sys/prctl.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/types.h>
#include <unistd.h>

/* No managed runtime, config parser, arbitrary path or recursive ownership work. */
static const char *const components[] = {"home", "suwayomi", ".local", "share", "Tachidesk"};

static int open_fixed_root(void)
{
    int current = open("/", O_PATH | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (current < 0) return -1;
    for (size_t index = 0; index < sizeof(components) / sizeof(components[0]); index++) {
        int next = openat(current, components[index], O_PATH | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        int saved = errno;
        close(current);
        if (next < 0) { errno = saved; return -1; }
        current = next;
    }
    return current;
}

static int same_node(const struct stat *left, const struct stat *right)
{
    return S_ISDIR(left->st_mode) && S_ISDIR(right->st_mode)
        && left->st_dev == right->st_dev && left->st_ino == right->st_ino;
}

static int same_metadata(const struct stat *left, const struct stat *right)
{
    return same_node(left, right) && left->st_uid == right->st_uid && left->st_gid == right->st_gid
        && left->st_mode == right->st_mode && left->st_nlink == right->st_nlink
        && left->st_mtim.tv_sec == right->st_mtim.tv_sec && left->st_mtim.tv_nsec == right->st_mtim.tv_nsec
        && left->st_ctim.tv_sec == right->st_ctim.tv_sec && left->st_ctim.tv_nsec == right->st_ctim.tv_nsec;
}

static int still_named(const struct stat *expected)
{
    struct stat status;
    int descriptor = open_fixed_root();
    if (descriptor < 0) return 0;
    int valid = fstat(descriptor, &status) == 0 && same_node(expected, &status);
    close(descriptor);
    return valid;
}

/* Root-owned initialization is the only operation which needs an empty scan.
 * Other owners are not enumerated using bootstrap authority. */
static int scan_empty(int held, const struct stat *expected, int *empty, int *readable)
{
    *empty = 0;
    *readable = -1;
    int descriptor = openat(held, ".", O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (descriptor < 0) return AUTOSOURCARR_SOURCE_ROOT_ACCESS;
    struct stat before, after;
    if (fstat(descriptor, &before) != 0 || !same_metadata(expected, &before)) {
        close(descriptor); return AUTOSOURCARR_SOURCE_ROOT_CHANGED;
    }
    /* Keep a separate real directory descriptor for fchown/fchmod; O_PATH does
     * not permit those operations. fdopendir owns only its duplicate. */
    int duplicate = fcntl(descriptor, F_DUPFD_CLOEXEC, 3);
    if (duplicate < 0) { close(descriptor); return AUTOSOURCARR_SOURCE_ROOT_ACCESS; }
    DIR *directory = fdopendir(duplicate);
    if (directory == NULL) { close(duplicate); close(descriptor); return AUTOSOURCARR_SOURCE_ROOT_ACCESS; }
    int found = 0, error = 0;
    for (;;) {
        errno = 0;
        struct dirent *entry = readdir(directory);
        if (entry == NULL) { error = errno; break; }
        if (strcmp(entry->d_name, ".") != 0 && strcmp(entry->d_name, "..") != 0) { found = 1; break; }
    }
    if (closedir(directory) != 0) error = 1;
    if (error != 0 || fstat(descriptor, &after) != 0) {
        close(descriptor); return AUTOSOURCARR_SOURCE_ROOT_ACCESS;
    }
    if (!same_metadata(expected, &after) || !still_named(expected)) {
        close(descriptor); return AUTOSOURCARR_SOURCE_ROOT_CHANGED;
    }
    *empty = !found;
    *readable = descriptor;
    return AUTOSOURCARR_SOURCE_ROOT_OK;
}

static int actual_bootstrap_identity(uint32_t original_uid, uint32_t original_gid)
{
    uid_t real, effective, saved;
    gid_t greal, geffective, gsaved;
    if (getresuid(&real, &effective, &saved) != 0 || getresgid(&greal, &geffective, &gsaved) != 0) return 0;
    if (original_uid == 0)
        return effective == 0 && saved == 0 && (real == 0 || real == 568);
    return real == original_uid && effective == original_uid && saved == original_uid
        && greal == original_gid && geffective == original_gid && gsaved == original_gid;
}

static int can_initialize_owner(void)
{
    struct __user_cap_header_struct header = { .version = _LINUX_CAPABILITY_VERSION_3, .pid = 0 };
    struct __user_cap_data_struct data[2] = {{0}, {0}};
    return syscall(SYS_capget, &header, data) == 0 && (data[0].effective & (1U << CAP_CHOWN)) != 0;
}

void autosourcarr_source_root_close(struct autosourcarr_source_root *root)
{
    if (root == NULL) return;
    if (root->descriptor >= 0) close(root->descriptor);
    memset(root, 0, sizeof(*root));
    root->descriptor = -1;
}

int autosourcarr_source_root_prepare(const struct autosourcarr_startup_request *request,
    uint32_t original_uid, uint32_t original_gid, struct autosourcarr_source_root *root)
{
    if (root == NULL) return AUTOSOURCARR_SOURCE_ROOT_IDENTITY;
    memset(root, 0, sizeof(*root));
    root->descriptor = -1;
    if (request == NULL) return AUTOSOURCARR_SOURCE_ROOT_IDENTITY;
    struct autosourcarr_source_identity preliminary;
    if (autosourcarr_source_identity_select(request, original_uid, original_gid, 0, 0, 0, &preliminary) != 0
        || preliminary.kind == AUTOSOURCARR_SOURCE_REFUSED)
        return AUTOSOURCARR_SOURCE_ROOT_IDENTITY;
    /* External mode never opens or modifies source paths, even with unused
     * negative/wide requested IDs. Parsing is the separate startup-frame owner. */
    if (!request->integrated) {
        root->selected = preliminary;
        return AUTOSOURCARR_SOURCE_ROOT_EXTERNAL;
    }
    if (!actual_bootstrap_identity(original_uid, original_gid)) return AUTOSOURCARR_SOURCE_ROOT_IDENTITY;
    int held = open_fixed_root();
    if (held < 0) return AUTOSOURCARR_SOURCE_ROOT_ACCESS;
    struct stat before, after;
    int readable = -1, empty = 0, result = AUTOSOURCARR_SOURCE_ROOT_ACCESS;
    if (fstat(held, &before) != 0 || !S_ISDIR(before.st_mode) || before.st_nlink == 0) goto fail;
    if (original_uid == 0 && before.st_uid == 0) {
        result = scan_empty(held, &before, &empty, &readable);
        if (result != AUTOSOURCARR_SOURCE_ROOT_OK) goto fail;
    }
    if (autosourcarr_source_identity_select(request, original_uid, original_gid,
            before.st_uid, before.st_gid, empty, &root->selected) != 0
        || root->selected.kind == AUTOSOURCARR_SOURCE_REFUSED) {
        result = AUTOSOURCARR_SOURCE_ROOT_IDENTITY; goto fail;
    }
    if (root->selected.chown_empty_root) {
        /* No write-permission widening, recursion or nonempty ownership change.
         * A root-owned group/world-writable directory cannot establish an empty
         * initialization boundary against an unprivileged concurrent writer. */
        if (readable < 0 || !empty || before.st_uid != 0 || (before.st_mode & 0022) != 0 || !can_initialize_owner()) {
            result = AUTOSOURCARR_SOURCE_ROOT_INITIALIZATION; goto fail;
        }
        if (fstat(readable, &after) != 0 || !same_metadata(&before, &after) || !still_named(&before)) {
            result = AUTOSOURCARR_SOURCE_ROOT_CHANGED; goto fail;
        }
        /* Restrict the still-root-owned empty directory first. After chown the
         * finite parent has no CAP_FOWNER and must not need one for chmod. */
        if (fchmod(readable, 0700) != 0) { result = AUTOSOURCARR_SOURCE_ROOT_INITIALIZATION; goto fail; }
        root->restricted_empty_root = 1;
        if (fchown(readable, root->selected.uid, root->selected.gid) != 0) {
            result = AUTOSOURCARR_SOURCE_ROOT_INITIALIZATION; goto fail;
        }
        root->initialized_empty_root = 1;
        if (fsync(readable) != 0) { result = AUTOSOURCARR_SOURCE_ROOT_INITIALIZATION; goto fail; }
        if (fstat(readable, &after) != 0 || !same_node(&before, &after)
            || after.st_uid != root->selected.uid || after.st_gid != root->selected.gid
            || (after.st_mode & 07777) != 0700 || !still_named(&before)) {
            result = AUTOSOURCARR_SOURCE_ROOT_CHANGED; goto fail;
        }
    } else if (fstat(held, &after) != 0 || !same_metadata(&before, &after) || !still_named(&before)) {
        result = AUTOSOURCARR_SOURCE_ROOT_CHANGED; goto fail;
    }
    if (readable >= 0) close(readable);
    root->descriptor = held;
    root->device = (uint64_t)before.st_dev;
    root->inode = (uint64_t)before.st_ino;
    return AUTOSOURCARR_SOURCE_ROOT_OK;
fail:
    if (readable >= 0) close(readable);
    close(held);
    /* An interrupted empty-root initialization is reported, never rolled back
     * over potentially newly created source data or relabelled a clean root. */
    return result;
}

/* The runtime exchange is deliberately separate from persistent source-root
 * initialization. Existing source-root behavior above is unchanged. */
static const char observation_ancestor[] = "autosourcarr-private";
static const char observation_leaf[] = "source-observations";

struct observation_fs_identity {
    uid_t uid[3];
    gid_t gid[3];
    uint32_t fsuid, fsgid;
    struct __user_cap_data_struct capabilities[2];
    int dumpable, active, changed;
};

static int observation_capabilities(struct __user_cap_data_struct data[2], int write)
{
    struct __user_cap_header_struct header = { .version = _LINUX_CAPABILITY_VERSION_3, .pid = 0 };
    return syscall(write ? SYS_capset : SYS_capget, &header, data) == 0 ? 0 : -1;
}

static int observation_identity_matches(const struct observation_fs_identity *identity)
{
    uid_t uid[3]; gid_t gid[3];
    struct __user_cap_data_struct capabilities[2] = {{0}, {0}};
    return getresuid(&uid[0], &uid[1], &uid[2]) == 0 && getresgid(&gid[0], &gid[1], &gid[2]) == 0
        && memcmp(uid, identity->uid, sizeof(uid)) == 0 && memcmp(gid, identity->gid, sizeof(gid)) == 0
        && (uint32_t)setfsuid((uid_t)-1) == identity->fsuid
        && (uint32_t)setfsgid((gid_t)-1) == identity->fsgid
        && observation_capabilities(capabilities, 0) == 0
        && memcmp(capabilities, identity->capabilities, sizeof(capabilities)) == 0
        && prctl(PR_GET_DUMPABLE, 0L, 0L, 0L, 0L) == identity->dumpable;
}

static int observation_fs_restore(struct observation_fs_identity *identity)
{
    if (!identity->active) return 0;
    int valid = 1;
    if (identity->changed) {
        /* Changing FSUID can remove/re-enable filesystem capability bits. Restore
         * the captured capability mask explicitly rather than assuming equality. */
        (void)setfsuid((uid_t)identity->fsuid);
        if ((uint32_t)setfsuid((uid_t)-1) != identity->fsuid) valid = 0;
        (void)setfsgid((gid_t)identity->fsgid);
        if ((uint32_t)setfsgid((gid_t)-1) != identity->fsgid) valid = 0;
        if (observation_capabilities(identity->capabilities, 1) != 0) valid = 0;
        if (prctl(PR_SET_DUMPABLE, (long)identity->dumpable, 0L, 0L, 0L) != 0) valid = 0;
    }
    if (!observation_identity_matches(identity)) valid = 0;
    identity->active = 0;
    return valid ? 0 : -1;
}

static int observation_fs_main(struct observation_fs_identity *identity)
{
    memset(identity, 0, sizeof(*identity));
    if (getresuid(&identity->uid[0], &identity->uid[1], &identity->uid[2]) != 0
        || getresgid(&identity->gid[0], &identity->gid[1], &identity->gid[2]) != 0
        || observation_capabilities(identity->capabilities, 0) != 0) return -1;
    identity->fsuid = (uint32_t)setfsuid((uid_t)-1);
    identity->fsgid = (uint32_t)setfsgid((gid_t)-1);
    identity->dumpable = prctl(PR_GET_DUMPABLE, 0L, 0L, 0L, 0L);
    /* PR_SET_DUMPABLE can restore only 0/1. Refuse an unrepresentable original
     * value before changing filesystem identity or dumpability. */
    if (identity->fsuid != (uint32_t)identity->uid[1] || identity->fsgid != (uint32_t)identity->gid[1]
        || (identity->dumpable != 0 && identity->dumpable != 1)) return -1;
    identity->active = 1;
    if (identity->fsuid == 568 && identity->fsgid == 568) return 0;
    identity->changed = 1;
    if (prctl(PR_SET_DUMPABLE, 0L, 0L, 0L, 0L) != 0) return -1;
    (void)setfsgid(568);
    if ((uint32_t)setfsgid((gid_t)-1) != 568) return -1;
    (void)setfsuid(568);
    return (uint32_t)setfsuid((uid_t)-1) == 568
        && prctl(PR_SET_DUMPABLE, 0L, 0L, 0L, 0L) == 0
        && prctl(PR_GET_DUMPABLE, 0L, 0L, 0L, 0L) == 0 ? 0 : -1;
}

static int observation_mkdir(int parent, const char *leaf)
{
    /* Native finite PID1 is single-threaded here. Do not inherit an arbitrary
     * container umask which would make a fresh 0700 node impossible to reopen. */
    mode_t previous = umask(0077);
    int result = mkdirat(parent, leaf, 0700), saved = errno;
    if (umask(previous) != 0077) { errno = EPERM; return -1; }
    errno = saved;
    return result;
}

static int observation_parent(const struct stat *status)
{
    return S_ISDIR(status->st_mode) && status->st_nlink != 0 && status->st_uid == 0
        && (status->st_mode & (0022 | 07000)) == 0;
}

static int observation_exact(const struct stat *status, uint32_t uid, uint32_t gid, mode_t mode)
{
    return S_ISDIR(status->st_mode) && status->st_nlink != 0
        && status->st_uid == uid && status->st_gid == gid && (status->st_mode & 07777) == mode;
}

static int observation_same_mode(const struct stat *left, const struct stat *right)
{
    return same_node(left, right) && left->st_uid == right->st_uid
        && left->st_gid == right->st_gid && left->st_mode == right->st_mode;
}

/* All directory traversal checks below use filesystem UID/GID568. The root
 * bootstrap cannot bypass a 568-owned 0700 ancestor without DAC_OVERRIDE. */
static int observation_named(int filesystem_root, int run, int ancestor, int exchange,
    const struct stat *filesystem_status, const struct stat *run_status,
    const struct stat *ancestor_status, uint32_t source_uid)
{
    struct stat held, named;
    if (fstat(filesystem_root, &held) != 0 || !observation_parent(&held)
        || !observation_same_mode(filesystem_status, &held)
        || fstat(run, &held) != 0 || !observation_parent(&held) || !observation_same_mode(run_status, &held)
        || fstatat(filesystem_root, "run", &named, AT_SYMLINK_NOFOLLOW) != 0 || !observation_same_mode(&held, &named)
        || fstat(ancestor, &held) != 0 || !observation_exact(&held, 568, 568, 0700)
        || !same_node(ancestor_status, &held)
        || fstatat(run, observation_ancestor, &named, AT_SYMLINK_NOFOLLOW) != 0 || !observation_same_mode(&held, &named)
        || fstat(exchange, &held) != 0 || !observation_exact(&held, source_uid, 568, 0770)
        || fstatat(ancestor, observation_leaf, &named, AT_SYMLINK_NOFOLLOW) != 0 || !observation_same_mode(&held, &named)) return 0;
    return 1;
}

void autosourcarr_source_observation_root_close(struct autosourcarr_source_observation_root *root)
{
    if (root == NULL) return;
    if (root->descriptor >= 0) close(root->descriptor);
    memset(root, 0, sizeof(*root)); root->descriptor = -1;
}

int autosourcarr_source_observation_root_prepare(uint32_t original_uid, uint32_t original_gid,
    const struct autosourcarr_source_root *source, struct autosourcarr_source_observation_root *root)
{
    if (root == NULL) return AUTOSOURCARR_SOURCE_ROOT_IDENTITY;
    memset(root, 0, sizeof(*root)); root->descriptor = -1;
    struct stat source_status;
    if (source == NULL || source->descriptor < 3
        || (source->selected.kind != AUTOSOURCARR_SOURCE_INHERIT && source->selected.kind != AUTOSOURCARR_SOURCE_SELECTED)
        || source->selected.uid == 0 || source->selected.uid == UINT32_MAX
        || source->selected.gid == 0 || source->selected.gid == UINT32_MAX
        || !actual_bootstrap_identity(original_uid, original_gid)
        || (original_uid != 0 && (original_uid != 568 || original_gid != 568 || source->selected.uid != 568))
        || fstat(source->descriptor, &source_status) != 0 || !S_ISDIR(source_status.st_mode) || source_status.st_nlink == 0
        || (uint64_t)source_status.st_dev != source->device || (uint64_t)source_status.st_ino != source->inode)
        return AUTOSOURCARR_SOURCE_ROOT_IDENTITY;

    int filesystem_root = -1, run = -1, ancestor = -1, readable_ancestor = -1, exchange = -1;
    int result = AUTOSOURCARR_SOURCE_ROOT_ACCESS;
    struct stat filesystem_status, run_status, ancestor_status, exchange_status;
    struct observation_fs_identity identity; memset(&identity, 0, sizeof(identity));
    filesystem_root = open("/", O_PATH | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (filesystem_root < 0 || fstat(filesystem_root, &filesystem_status) != 0 || !observation_parent(&filesystem_status)) goto fail;
    run = openat(filesystem_root, "run", O_PATH | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (run < 0 || fstat(run, &run_status) != 0 || !observation_parent(&run_status)) goto fail;

    ancestor = openat(run, observation_ancestor, O_PATH | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (ancestor < 0) {
        if (errno != ENOENT) goto fail;
        /* Rootless568 has no authority to make /run writable. The immutable
         * image supplies this empty private ancestor; no host step is added. */
        result = AUTOSOURCARR_SOURCE_ROOT_INITIALIZATION;
        if (original_uid != 0 || !can_initialize_owner()
            || observation_mkdir(run, observation_ancestor) != 0) goto fail;
        root->created_ancestor = 1;
        ancestor = openat(run, observation_ancestor, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        if (ancestor < 0 || fstat(ancestor, &ancestor_status) != 0
            || !observation_exact(&ancestor_status, 0, (uint32_t)setfsgid((gid_t)-1), 0700)
            || fchmod(ancestor, 0700) != 0 || fchown(ancestor, 568, 568) != 0 || fsync(ancestor) != 0) goto fail;
    }
    result = AUTOSOURCARR_SOURCE_ROOT_CHANGED;
    if (fstat(ancestor, &ancestor_status) != 0 || !observation_exact(&ancestor_status, 568, 568, 0700)) goto fail;

    if (observation_fs_main(&identity) != 0) { result = AUTOSOURCARR_SOURCE_ROOT_IDENTITY; goto fail; }
    readable_ancestor = openat(ancestor, ".", O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (readable_ancestor < 0) { result = AUTOSOURCARR_SOURCE_ROOT_ACCESS; goto fail; }
    struct stat readable_status;
    if (fstat(readable_ancestor, &readable_status) != 0 || !observation_same_mode(&ancestor_status, &readable_status)) goto fail;
    exchange = openat(ancestor, observation_leaf, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (exchange < 0) {
        if (errno != ENOENT) { result = AUTOSOURCARR_SOURCE_ROOT_ACCESS; goto fail; }
        result = AUTOSOURCARR_SOURCE_ROOT_INITIALIZATION;
        if ((source->selected.uid != 568 && (original_uid != 0
                || (identity.capabilities[0].effective & (1U << CAP_CHOWN)) == 0))
            || observation_mkdir(ancestor, observation_leaf) != 0) goto fail;
        root->created_exchange = 1;
        exchange = openat(ancestor, observation_leaf, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        if (exchange < 0 || fstat(exchange, &exchange_status) != 0 || !observation_exact(&exchange_status, 568, 568, 0700)
            || fchmod(exchange, 0770) != 0) goto fail;
    } else if (fstat(exchange, &exchange_status) != 0
        || !observation_exact(&exchange_status, source->selected.uid, 568, 0770)) goto fail;
    if (observation_fs_restore(&identity) != 0) { result = AUTOSOURCARR_SOURCE_ROOT_IDENTITY; goto fail; }

    if (root->created_exchange && source->selected.uid != 568) {
        result = AUTOSOURCARR_SOURCE_ROOT_INITIALIZATION;
        /* The freshly created directory is already 0770. Only its owner is
         * changed now, using captured bootstrap CHOWN authority on the held FD. */
        if (original_uid != 0 || !can_initialize_owner() || fchown(exchange, source->selected.uid, 568) != 0) goto fail;
    }
    if (root->created_exchange && (fsync(exchange) != 0 || fsync(readable_ancestor) != 0)) {
        result = AUTOSOURCARR_SOURCE_ROOT_INITIALIZATION; goto fail;
    }
    if (observation_fs_main(&identity) != 0) { result = AUTOSOURCARR_SOURCE_ROOT_IDENTITY; goto fail; }
    result = AUTOSOURCARR_SOURCE_ROOT_CHANGED;
    int descriptor_flags = fcntl(exchange, F_GETFD);
    if (!observation_named(filesystem_root, run, ancestor, exchange,
            &filesystem_status, &run_status, &ancestor_status, source->selected.uid)
        || fstat(exchange, &exchange_status) != 0
        || (fcntl(exchange, F_GETFL) & (O_ACCMODE | O_DIRECTORY)) != (O_RDONLY | O_DIRECTORY)
        || descriptor_flags < 0 || (descriptor_flags & FD_CLOEXEC) == 0) goto fail;
    if (observation_fs_restore(&identity) != 0) { result = AUTOSOURCARR_SOURCE_ROOT_IDENTITY; goto fail; }

    root->descriptor = exchange; exchange = -1;
    root->device = (uint64_t)exchange_status.st_dev; root->inode = (uint64_t)exchange_status.st_ino;
    root->ancestor_device = (uint64_t)ancestor_status.st_dev; root->ancestor_inode = (uint64_t)ancestor_status.st_ino;
    root->source_uid = source->selected.uid;
    close(readable_ancestor); close(ancestor); close(run); close(filesystem_root);
    return AUTOSOURCARR_SOURCE_ROOT_OK;
fail:
    /* Restoration is attempted on every path, including partially failed
     * setfsgid/setfsuid. Never return a directory after uncertain restoration. */
    if (observation_fs_restore(&identity) != 0) result = AUTOSOURCARR_SOURCE_ROOT_IDENTITY;
    if (exchange >= 0) close(exchange);
    if (readable_ancestor >= 0) close(readable_ancestor);
    if (ancestor >= 0) close(ancestor);
    if (run >= 0) close(run);
    if (filesystem_root >= 0) close(filesystem_root);
    return result;
}
