#define _GNU_SOURCE
#include "process-identity.h"
#include <errno.h>
#include <grp.h>
#include <linux/capability.h>
#include <sys/prctl.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <unistd.h>

_Static_assert(sizeof(uid_t) == sizeof(uint32_t), "Linux source UID ABI must be exact uint32");
_Static_assert(sizeof(gid_t) == sizeof(uint32_t), "Linux source GID ABI must be exact uint32");

static int refused(void)
{
    errno = EPERM;
    return -1;
}

static int groups_match(const uint32_t *wanted, size_t count)
{
    int actual_count = getgroups(0, NULL);
    if (actual_count < 0) return -1;
    if ((size_t)actual_count != count) return 0;
    if (count == 0) return 1;
    gid_t actual;
    if (getgroups(1, &actual) != 1) return -1;
    return actual == (gid_t)wanted[0];
}

int autosourcarr_drop_identity(uint32_t uid, uint32_t gid,
    const uint32_t *groups, size_t count, int nondumpable)
{
    if (uid == 0 || gid == 0 || uid == UINT32_MAX || gid == UINT32_MAX
        || count > 1 || (count != 0 && (groups == NULL || groups[0] == UINT32_MAX))) {
        errno = EINVAL;
        return -1;
    }
    int matching = groups_match(groups, count);
    if (matching < 0) return -1;
    if (!matching) {
        gid_t desired = count ? (gid_t)groups[0] : 0;
        if (setgroups(count, count ? &desired : NULL) != 0) return -1;
    }
    if (setresgid((gid_t)gid, (gid_t)gid, (gid_t)gid) != 0
        || setresuid((uid_t)uid, (uid_t)uid, (uid_t)uid) != 0) return -1;

    struct __user_cap_header_struct header = { .version = _LINUX_CAPABILITY_VERSION_3, .pid = 0 };
    struct __user_cap_data_struct capabilities[2] = {{0}, {0}};
    if (syscall(SYS_capset, &header, capabilities) != 0) return -1;
    if (prctl(PR_CAP_AMBIENT, PR_CAP_AMBIENT_CLEAR_ALL, 0L, 0L, 0L) != 0) return -1;
    if (prctl(PR_SET_NO_NEW_PRIVS, 1L, 0L, 0L, 0L) != 0) return -1;
    if (nondumpable && prctl(PR_SET_DUMPABLE, 0L, 0L, 0L, 0L) != 0) return -1;
    umask(0077);

    uid_t real_uid, effective_uid, saved_uid;
    gid_t real_gid, effective_gid, saved_gid;
    if (getresuid(&real_uid, &effective_uid, &saved_uid) != 0
        || getresgid(&real_gid, &effective_gid, &saved_gid) != 0) return -1;
    if (real_uid != uid || effective_uid != uid || saved_uid != uid
        || real_gid != gid || effective_gid != gid || saved_gid != gid) return refused();
    matching = groups_match(groups, count);
    if (matching < 0) return -1;
    if (!matching) return refused();
    if (syscall(SYS_capget, &header, capabilities) != 0) return -1;
    for (size_t index = 0; index < 2; index++)
        if (capabilities[index].effective != 0 || capabilities[index].permitted != 0 || capabilities[index].inheritable != 0) return refused();
    if (prctl(PR_GET_NO_NEW_PRIVS, 0L, 0L, 0L, 0L) != 1) return refused();
    if (nondumpable && prctl(PR_GET_DUMPABLE, 0L, 0L, 0L, 0L) != 0) return refused();
    return 0;
}
