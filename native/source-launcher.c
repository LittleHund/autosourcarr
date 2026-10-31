#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif
#include "process-identity.h"
#include <elf.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <linux/capability.h>
#include <linux/fs.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/fsuid.h>
#include <sys/prctl.h>
#include <sys/random.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/xattr.h>
#include <unistd.h>

#if !defined(__linux__) || !defined(__x86_64__)
#error "The fixed source launcher requires Linux x86_64."
#endif
_Static_assert(sizeof(uid_t) == 4 && sizeof(gid_t) == 4, "Exact Linux identity ABI required");

/* PRIVATE CANDIDATE: parent alone compiles/runs. Must be statically linked and
 * installed as the immutable owner-selected ELF. There is no arbitrary launcher,
 * configuration interpreter, source observation mode or readiness receipt here.
 * Its first exec acknowledgement already occurred when this ELF was loaded.
 * Native source owner remains the sole descendant/STOP/reap authority. */
#define SOURCE_ROOT "/home/suwayomi/.local/share/Tachidesk"
#define SOURCE_SHELL "/opt/autosourcarr-source/bin/source-shell"
#define STARTUP_SCRIPT "/home/suwayomi/startup_script.sh"
#define CREATE_SCRIPT "/home/suwayomi/create_server_conf.sh"
#define SOURCE_JAR "/home/suwayomi/startup/tachidesk_latest.jar"
#define REFERENCE "/opt/autosourcarr-source/assets/server-reference.conf"
#define JAVA_BINARY "/opt/java/openjdk/bin/java"
#define AGENT_JAR "/app/source-browser-agent.jar"
#define AGENT_OPTION "-javaagent:" AGENT_JAR
#define PROPERTY_PREFIX "-Dsuwayomi.tachidesk.config.server."
#define FIXED_PROPERTIES \
    PROPERTY_PREFIX "rootDir=" SOURCE_ROOT " " \
    PROPERTY_PREFIX "ip=127.0.0.1 " \
    PROPERTY_PREFIX "port=4567 " \
    PROPERTY_PREFIX "authMode=NONE " \
    PROPERTY_PREFIX "webUIEnabled=false " \
    PROPERTY_PREFIX "initialOpenInBrowserEnabled=false " \
    PROPERTY_PREFIX "systemTrayEnabled=false " \
    PROPERTY_PREFIX "kcefEnabled=true " \
    PROPERTY_PREFIX "flareSolverrEnabled=false"
#define REFERENCE_BYTES 8015U
#define ENV_ENTRIES 280U
#define ENV_BYTES (128U * 1024U)
#define ENV_ENTRY_BYTES (128U + 1U + 8192U)

enum stage { STAGE_NONE, STAGE_ARGUMENTS, STAGE_BINDING, STAGE_IDENTITY, STAGE_SIGNAL,
    STAGE_ENVIRONMENT, STAGE_ASSETS, STAGE_CONFIGURATION, STAGE_CLEANUP, STAGE_EXEC };
enum cleanup_fact { CLOSE_FAILED = 1U, TEMP_CHANGED = 2U, UNLINK_FAILED = 4U,
    PUBLICATION_UNCERTAIN = 8U, PUBLISHED_UNSYNCED = 16U };
struct launcher {
    int root, directory, shell, script, create_script, jar, reference, java, agent, temporary;
    pid_t parent, leader;
    uint32_t uid, gid;
    struct stat root_status, reference_status, temporary_status;
    char temporary_name[64];
    int temporary_named, published, with_agent;
    enum stage primary_stage;
    int primary_errno;
    unsigned cleanup;
    char *environment[ENV_ENTRIES + 1U];
    size_t environment_count;
};
static volatile sig_atomic_t stop_requested;
extern char **environ;

static int refuse(struct launcher *l, enum stage stage, int error)
{
    if (l->primary_stage == STAGE_NONE) {
        l->primary_stage = stage; l->primary_errno = error > 0 ? error : EINVAL;
    }
    errno = error > 0 ? error : EINVAL;
    return -1;
}
static int cancelled(struct launcher *l)
{ return stop_requested ? refuse(l, STAGE_SIGNAL, ECANCELED) : 0; }
static int close_owned(struct launcher *l, int *descriptor)
{
    if (*descriptor < 0) return 0;
    int old = *descriptor; *descriptor = -1;
    /* Linux close is never retried against a possibly reused descriptor. */
    if (close(old) == 0) return 0;
    l->cleanup |= CLOSE_FAILED;
    return refuse(l, STAGE_CLEANUP, errno);
}
static int directory_node(const struct stat *s)
{ return S_ISDIR(s->st_mode) && s->st_nlink != 0; }
static int same_node(const struct stat *a, const struct stat *b)
{ return directory_node(a) && directory_node(b) && a->st_dev == b->st_dev && a->st_ino == b->st_ino; }
static int same_file(const struct stat *a, const struct stat *b)
{
    return S_ISREG(a->st_mode) && S_ISREG(b->st_mode) && a->st_dev == b->st_dev && a->st_ino == b->st_ino
        && a->st_nlink == b->st_nlink && a->st_mode == b->st_mode && a->st_uid == b->st_uid && a->st_gid == b->st_gid
        && a->st_size == b->st_size && a->st_mtim.tv_sec == b->st_mtim.tv_sec && a->st_mtim.tv_nsec == b->st_mtim.tv_nsec
        && a->st_ctim.tv_sec == b->st_ctim.tv_sec && a->st_ctim.tv_nsec == b->st_ctim.tv_nsec;
}
static void on_stop(int number) { (void)number; stop_requested = 1; }
static int signal_policy(struct launcher *l, int for_exec)
{
    struct sigaction action; memset(&action, 0, sizeof(action)); sigemptyset(&action.sa_mask);
    const int signals[] = {SIGCHLD, SIGTERM, SIGINT, SIGHUP, SIGQUIT, SIGPIPE, SIGUSR1, SIGUSR2};
    for (size_t i = 0; i < sizeof(signals) / sizeof(signals[0]); i++) {
        int number = signals[i]; action.sa_handler = SIG_DFL;
        if (!for_exec && (number == SIGTERM || number == SIGINT || number == SIGHUP || number == SIGQUIT)) action.sa_handler = on_stop;
        if (!for_exec && number == SIGPIPE) action.sa_handler = SIG_IGN;
        if (sigaction(number, &action, NULL) != 0) return refuse(l, STAGE_SIGNAL, errno);
    }
    sigset_t empty, actual; sigemptyset(&empty);
    if (sigprocmask(SIG_SETMASK, &empty, NULL) != 0 || sigprocmask(SIG_SETMASK, NULL, &actual) != 0
        || sigaction(SIGCHLD, NULL, &action) != 0) return refuse(l, STAGE_SIGNAL, errno);
    if (sigismember(&actual, SIGCHLD) != 0 || action.sa_handler != SIG_DFL || (action.sa_flags & SA_NOCLDWAIT) != 0)
        return refuse(l, STAGE_SIGNAL, EPERM);
    return cancelled(l);
}

/* Constant paths only. Mutable source-root ancestors are no-follow but are not
 * required to be root-owned; immutable image asset ancestors are root-owned. */
static int fixed_directory(struct launcher *l, const char *path, int immutable, enum stage stage)
{
    char copy[PATH_MAX]; size_t length = strlen(path);
    if (path[0] != '/' || length >= sizeof(copy)) return refuse(l, stage, EINVAL);
    memcpy(copy, path + 1, length);
    int current = open("/", O_PATH | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (current < 0) return refuse(l, stage, errno);
    struct stat status;
    if (fstat(current, &status) != 0 || !directory_node(&status)
        || (immutable && (status.st_uid != 0 || (status.st_mode & 0022) != 0))) {
        (void)refuse(l, stage, errno); (void)close_owned(l, &current); return -1;
    }
    char *save = NULL;
    for (char *part = strtok_r(copy, "/", &save); part != NULL; part = strtok_r(NULL, "/", &save)) {
        int next = openat(current, part, O_PATH | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        if (next < 0) { (void)refuse(l, stage, errno); (void)close_owned(l, &current); return -1; }
        if (fstat(next, &status) != 0 || !directory_node(&status)
            || (immutable && (status.st_uid != 0 || (status.st_mode & 0022) != 0))) {
            (void)refuse(l, stage, errno); (void)close_owned(l, &next); (void)close_owned(l, &current); return -1;
        }
        if (close_owned(l, &current) != 0) { (void)close_owned(l, &next); return -1; }
        current = next;
    }
    return current;
}
static int root_current(struct launcher *l)
{
    struct stat held, cwd, named;
    if (fstat(l->root, &held) != 0 || fstatat(AT_FDCWD, ".", &cwd, AT_SYMLINK_NOFOLLOW) != 0
        || !same_node(&held, &l->root_status) || !same_node(&held, &cwd)) return refuse(l, STAGE_BINDING, ESTALE);
    int fixed = fixed_directory(l, SOURCE_ROOT, 0, STAGE_BINDING);
    if (fixed < 0) return -1;
    int valid = fstat(fixed, &named) == 0 && same_node(&held, &named);
    if (!valid) (void)refuse(l, STAGE_BINDING, ESTALE);
    if (close_owned(l, &fixed) != 0) return -1;
    return valid ? 0 : -1;
}
static int validate_source_binding(struct launcher *l)
{
    uid_t real, effective, saved; gid_t gid, effective_gid, saved_gid;
    l->parent = getppid(); l->leader = getpid();
    int death = 0;
    if (l->parent <= 1 || l->leader <= 1 || getsid(0) != l->leader || getpgrp() != l->leader
        || prctl(PR_GET_PDEATHSIG, &death, 0L, 0L, 0L) != 0 || death != SIGKILL
        || getresuid(&real, &effective, &saved) != 0 || getresgid(&gid, &effective_gid, &saved_gid) != 0
        || real == 0 || real == (uid_t)-1 || gid == 0 || gid == (gid_t)-1
        || real != effective || real != saved || gid != effective_gid || gid != saved_gid
        || getgroups(0, NULL) != 0 || (uid_t)setfsuid((uid_t)-1) != real || (gid_t)setfsgid((gid_t)-1) != gid
        || prctl(PR_GET_NO_NEW_PRIVS, 0L, 0L, 0L, 0L) != 1) return refuse(l, STAGE_IDENTITY, EPERM);
    struct __user_cap_header_struct header = {.version = _LINUX_CAPABILITY_VERSION_3, .pid = 0};
    struct __user_cap_data_struct caps[2] = {{0}, {0}};
    if (syscall(SYS_capget, &header, caps) != 0) return refuse(l, STAGE_IDENTITY, errno);
    for (size_t i = 0; i < 2; i++)
        if (caps[i].effective != 0 || caps[i].permitted != 0 || caps[i].inheritable != 0) return refuse(l, STAGE_IDENTITY, EPERM);
    l->uid = (uint32_t)real; l->gid = (uint32_t)gid;
    /* Ordinary ELF exec may reset dumpability. Establish it here, not as a false
     * inherited precondition, then rearm PDEATHSIG after same-ID credential calls. */
    if (autosourcarr_drop_identity(l->uid, l->gid, NULL, 0, 1) != 0
        || prctl(PR_SET_PDEATHSIG, SIGKILL, 0L, 0L, 0L) != 0
        || prctl(PR_GET_PDEATHSIG, &death, 0L, 0L, 0L) != 0 || death != SIGKILL || getppid() != l->parent
        || (uint32_t)setfsuid((uid_t)-1) != l->uid || (uint32_t)setfsgid((gid_t)-1) != l->gid)
        return refuse(l, STAGE_IDENTITY, errno);
    if (fstat(l->root, &l->root_status) != 0 || !directory_node(&l->root_status)) return refuse(l, STAGE_BINDING, EBADF);
    if (syscall(SYS_close_range, 3U, 4U, 0) != 0 || syscall(SYS_close_range, 6U, ~0U, 0) != 0)
        return refuse(l, STAGE_BINDING, errno);
    for (int fd = 0; fd < 3; fd++) {
        int flags = fcntl(fd, F_GETFL);
        if (flags < 0 || (fd == 0 ? (flags & O_ACCMODE) == O_WRONLY : (flags & O_ACCMODE) == O_RDONLY))
            return refuse(l, STAGE_BINDING, EBADF);
    }
    return root_current(l);
}

/* kind0 regular input,1 executable ELF,2 executable pinned script. */
static int fixed_asset(struct launcher *l, const char *path, off_t expected, off_t maximum, int kind)
{
    char directory[PATH_MAX]; const char *leaf = strrchr(path, '/');
    if (leaf == NULL || leaf == path || leaf[1] == 0 || (size_t)(leaf - path) >= sizeof(directory)) return refuse(l, STAGE_ASSETS, EINVAL);
    memcpy(directory, path, (size_t)(leaf - path)); directory[leaf - path] = 0;
    int parent = fixed_directory(l, directory, 1, STAGE_ASSETS);
    if (parent < 0) return -1;
    int fd = openat(parent, leaf + 1, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC);
    if (fd < 0) { (void)refuse(l, STAGE_ASSETS, errno); (void)close_owned(l, &parent); return -1; }
    struct stat status; unsigned char bytes[20];
    int valid = fstat(fd, &status) == 0 && S_ISREG(status.st_mode) && status.st_nlink == 1
        && status.st_uid == 0 && (status.st_mode & (0022 | 06000)) == 0 && status.st_size > 0
        && status.st_size <= maximum && (expected == 0 || status.st_size == expected);
    if (kind != 0) valid = valid && (status.st_mode & 0111) != 0;
    if (kind == 1) valid = valid && pread(fd, bytes, sizeof(bytes), 0) == (ssize_t)sizeof(bytes)
        && memcmp(bytes, "\177ELF\002\001", 6) == 0 && bytes[18] == 62 && bytes[19] == 0;
    errno = 0; ssize_t capability = fgetxattr(fd, "security.capability", NULL, 0);
    valid = valid && capability == -1 && errno == ENODATA;
    if (!valid) (void)refuse(l, STAGE_ASSETS, EPERM);
    if (close_owned(l, &parent) != 0 || !valid) { (void)close_owned(l, &fd); return -1; }
    return fd;
}
static int verify_java_mapping(struct launcher *l)
{
    int directory = fixed_directory(l, "/usr/bin", 1, STAGE_ASSETS);
    if (directory < 0) return -1;
    struct stat link; char target[PATH_MAX];
    ssize_t length = readlinkat(directory, "java", target, sizeof(target));
    int valid = length == (ssize_t)strlen(JAVA_BINARY) && memcmp(target, JAVA_BINARY, strlen(JAVA_BINARY)) == 0
        && fstatat(directory, "java", &link, AT_SYMLINK_NOFOLLOW) == 0 && S_ISLNK(link.st_mode)
        && link.st_uid == 0 && link.st_nlink == 1;
    if (!valid) (void)refuse(l, STAGE_ASSETS, EPERM);
    if (close_owned(l, &directory) != 0) return -1;
    return valid ? 0 : -1;
}
static int equals(const char *key, size_t length, const char *wanted)
{ return strlen(wanted) == length && memcmp(key, wanted, length) == 0; }
static int starts(const char *key, size_t length, const char *wanted)
{ return length >= strlen(wanted) && memcmp(key, wanted, strlen(wanted)) == 0; }
static int prepare_environment(struct launcher *l)
{
    static char *const fixed[] = {"HOME=/home/suwayomi", "PATH=/usr/bin:/bin", "JAVA_HOME=/opt/java/openjdk", "LANG=C.UTF-8"};
    static char no_agent[] = "JAVA_TOOL_OPTIONS=" FIXED_PROPERTIES;
    static char with_agent[] = "JAVA_TOOL_OPTIONS=" AGENT_OPTION " " FIXED_PROPERTIES;
    const char *keys[ENV_ENTRIES]; size_t lengths[ENV_ENTRIES];
    size_t entries = 0, bytes = 0;
    if (environ == NULL) return refuse(l, STAGE_ENVIRONMENT, EINVAL);
    for (size_t i = 0; i <= ENV_ENTRIES; i++) {
        if (environ[i] == NULL) break;
        if (i == ENV_ENTRIES) return refuse(l, STAGE_ENVIRONMENT, E2BIG);
        size_t length = strnlen(environ[i], ENV_ENTRY_BYTES + 1U);
        const char *separator = memchr(environ[i], '=', length);
        if (length == 0 || length > ENV_ENTRY_BYTES || separator == NULL || separator == environ[i]
            || length + 1U > ENV_BYTES - bytes) return refuse(l, STAGE_ENVIRONMENT, E2BIG);
        bytes += length + 1U;
        size_t size = (size_t)(separator - environ[i]); const char *key = environ[i], *value = separator + 1;
        if (size > 128 || length - size - 1U > 8192U) return refuse(l, STAGE_ENVIRONMENT, EINVAL);
        for (size_t j = 0; j < size; j++)
            if (!((key[j] >= 'A' && key[j] <= 'Z') || (key[j] >= 'a' && key[j] <= 'z') || key[j] == '_'
                || (j != 0 && key[j] >= '0' && key[j] <= '9'))) return refuse(l, STAGE_ENVIRONMENT, EINVAL);
        for (size_t j = 0; j < entries; j++) if (lengths[j] == size && memcmp(keys[j], key, size) == 0) return refuse(l, STAGE_ENVIRONMENT, EINVAL);
        keys[entries] = key; lengths[entries++] = size;
        if (equals(key, size, "JAVA_TOOL_OPTIONS")) {
            if (strcmp(value, AGENT_OPTION) != 0) return refuse(l, STAGE_ENVIRONMENT, EINVAL);
            l->with_agent = 1; continue;
        }
        if (equals(key, size, "AUTOSOURCARR_SOURCE_DATA_FD")) {
            if (strcmp(value, "5") != 0) return refuse(l, STAGE_ENVIRONMENT, EINVAL);
            continue;
        }
        int system = 0;
        for (size_t j = 0; j < sizeof(fixed) / sizeof(fixed[0]); j++) {
            const char *end = strchr(fixed[j], '='); size_t wanted = (size_t)(end - fixed[j]);
            if (wanted == size && memcmp(key, fixed[j], size) == 0) {
                if (strcmp(value, end + 1) != 0) return refuse(l, STAGE_ENVIRONMENT, EINVAL);
                system = 1; break;
            }
        }
        if (system) continue;
        if (starts(key, size, "LD_") || starts(key, size, "PYTHON") || equals(key, size, "_JAVA_OPTIONS")
            || equals(key, size, "JDK_JAVA_OPTIONS") || equals(key, size, "CLASSPATH") || equals(key, size, "BASH_ENV")
            || equals(key, size, "ENV") || equals(key, size, "SHELLOPTS")) return refuse(l, STAGE_ENVIRONMENT, EINVAL);
        if (l->environment_count >= ENV_ENTRIES - 5U) return refuse(l, STAGE_ENVIRONMENT, E2BIG);
        l->environment[l->environment_count++] = environ[i];
    }
    for (size_t j = 0; j < sizeof(fixed) / sizeof(fixed[0]); j++) l->environment[l->environment_count++] = fixed[j];
    l->environment[l->environment_count++] = l->with_agent ? with_agent : no_agent;
    l->environment[l->environment_count] = NULL;
    bytes = 0;
    for (size_t j = 0; j < l->environment_count; j++) {
        size_t length = strlen(l->environment[j]);
        if (length + 1U > ENV_BYTES - bytes) return refuse(l, STAGE_ENVIRONMENT, E2BIG);
        bytes += length + 1U;
    }
    return 0;
}

/* Returns1 for a linked regular winner,0 only for absent, and-1 for errors. It
 * neither opens/follows a FIFO/link nor reads/truncates an existing configuration. */
static int existing_configuration(struct launcher *l)
{
    struct stat status;
    if (fstatat(l->root, "server.conf", &status, AT_SYMLINK_NOFOLLOW) == 0)
        return S_ISREG(status.st_mode) && status.st_nlink != 0 ? 1 : refuse(l, STAGE_CONFIGURATION, EINVAL);
    return errno == ENOENT ? 0 : refuse(l, STAGE_CONFIGURATION, errno);
}
static int discard_temporary(struct launcher *l)
{
    if (!l->temporary_named) return 0;
    struct stat status;
    if (fstatat(l->root, l->temporary_name, &status, AT_SYMLINK_NOFOLLOW) != 0) {
        if (errno == ENOENT) { l->temporary_named = 0; return 0; }
        l->cleanup |= UNLINK_FAILED; return refuse(l, STAGE_CLEANUP, errno);
    }
    if (!S_ISREG(status.st_mode) || status.st_dev != l->temporary_status.st_dev || status.st_ino != l->temporary_status.st_ino) {
        l->cleanup |= TEMP_CHANGED; return refuse(l, STAGE_CLEANUP, ESTALE);
    }
    if (unlinkat(l->root, l->temporary_name, 0) != 0) { l->cleanup |= UNLINK_FAILED; return refuse(l, STAGE_CLEANUP, errno); }
    l->temporary_named = 0; return 0;
}
static int new_temporary(struct launcher *l)
{
    static const char digits[] = "0123456789abcdef";
    for (size_t attempt = 0; attempt < 8; attempt++) {
        unsigned char random[12]; size_t at = 0;
        while (at < sizeof(random)) {
            if (cancelled(l) != 0) { explicit_bzero(random, sizeof(random)); return -1; }
            ssize_t count = getrandom(random + at, sizeof(random) - at, GRND_NONBLOCK);
            if (count > 0) { at += (size_t)count; continue; }
            if (count < 0 && errno == EINTR) continue;
            explicit_bzero(random, sizeof(random)); return refuse(l, STAGE_CONFIGURATION, count == 0 ? EIO : errno);
        }
        static const char prefix[] = ".autosourcarr-source-conf-";
        memcpy(l->temporary_name, prefix, sizeof(prefix) - 1);
        for (size_t i = 0; i < sizeof(random); i++) {
            l->temporary_name[sizeof(prefix) - 1 + 2 * i] = digits[random[i] >> 4];
            l->temporary_name[sizeof(prefix) + 2 * i] = digits[random[i] & 15U];
        }
        l->temporary_name[sizeof(prefix) - 1 + 2 * sizeof(random)] = 0;
        explicit_bzero(random, sizeof(random));
        l->temporary = openat(l->root, l->temporary_name, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0600);
        if (l->temporary < 0) { if (errno == EEXIST) continue; return refuse(l, STAGE_CONFIGURATION, errno); }
        l->temporary_named = 1;
        if (fstat(l->temporary, &l->temporary_status) != 0) return refuse(l, STAGE_CONFIGURATION, errno);
        if (!S_ISREG(l->temporary_status.st_mode) || l->temporary_status.st_nlink != 1 || l->temporary_status.st_uid != l->uid
            || (l->temporary_status.st_mode & 07777) != 0600) return refuse(l, STAGE_CONFIGURATION, EPERM);
        return 0;
    }
    return refuse(l, STAGE_CONFIGURATION, EEXIST);
}
static int sync_owned(struct launcher *l, int fd)
{
    for (;;) {
        if (cancelled(l) != 0) return -1;
        if (fsync(fd) == 0) return 0;
        if (errno != EINTR) return refuse(l, STAGE_CONFIGURATION, errno);
    }
}
static int copy_reference(struct launcher *l)
{
    unsigned char bytes[4096]; size_t copied = 0;
    while (copied < REFERENCE_BYTES) {
        if (cancelled(l) != 0) { explicit_bzero(bytes, sizeof(bytes)); return -1; }
        size_t wanted = REFERENCE_BYTES - copied; if (wanted > sizeof(bytes)) wanted = sizeof(bytes);
        ssize_t count = read(l->reference, bytes, wanted);
        if (count < 0 && errno == EINTR) continue;
        if (count <= 0) { explicit_bzero(bytes, sizeof(bytes)); return refuse(l, STAGE_CONFIGURATION, count == 0 ? EIO : errno); }
        size_t wrote = 0;
        while (wrote < (size_t)count) {
            if (cancelled(l) != 0) { explicit_bzero(bytes, sizeof(bytes)); return -1; }
            ssize_t output = write(l->temporary, bytes + wrote, (size_t)count - wrote);
            if (output > 0) { wrote += (size_t)output; continue; }
            if (output < 0 && errno == EINTR) continue;
            explicit_bzero(bytes, sizeof(bytes)); return refuse(l, STAGE_CONFIGURATION, output == 0 ? EIO : errno);
        }
        copied += (size_t)count;
    }
    explicit_bzero(bytes, sizeof(bytes));
    struct stat after; unsigned char extra; ssize_t end;
    do { end = read(l->reference, &extra, 1); } while (end < 0 && errno == EINTR && !stop_requested);
    if (cancelled(l) != 0) return -1;
    if (end < 0) return refuse(l, STAGE_CONFIGURATION, errno);
    if (end != 0) return refuse(l, STAGE_CONFIGURATION, ESTALE);
    if (fstat(l->reference, &after) != 0) return refuse(l, STAGE_CONFIGURATION, errno);
    if (!same_file(&l->reference_status, &after)) return refuse(l, STAGE_CONFIGURATION, ESTALE);
    return sync_owned(l, l->temporary);
}
static int ensure_server_conf(struct launcher *l)
{
    int exists = existing_configuration(l);
    if (exists != 0) return exists > 0 ? 0 : -1;
    l->directory = openat(l->root, ".", O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    struct stat status;
    if (l->directory < 0 || fstat(l->directory, &status) != 0) return refuse(l, STAGE_CONFIGURATION, errno);
    if (!same_node(&status, &l->root_status)) return refuse(l, STAGE_CONFIGURATION, ESTALE);
    if (new_temporary(l) != 0 || copy_reference(l) != 0 || root_current(l) != 0 || cancelled(l) != 0) return -1;
    if (syscall(SYS_renameat2, l->root, l->temporary_name, l->root, "server.conf", RENAME_NOREPLACE) != 0) {
        int saved = errno;
        if (saved == EEXIST) {
            if (existing_configuration(l) <= 0) return refuse(l, STAGE_CONFIGURATION, EEXIST);
            if (discard_temporary(l) != 0 || close_owned(l, &l->temporary) != 0) return -1;
            return sync_owned(l, l->directory);
        }
        /* No retry of an uncertain publication; never unlink/roll back server.conf. */
        if (saved == EINTR || saved == EIO) l->cleanup |= PUBLICATION_UNCERTAIN;
        return refuse(l, STAGE_CONFIGURATION, saved);
    }
    l->published = 1; l->temporary_named = 0;
    if (sync_owned(l, l->directory) != 0) { l->cleanup |= PUBLISHED_UNSYNCED; return -1; }
    return close_owned(l, &l->temporary);
}

static void cleanup(struct launcher *l)
{
    (void)discard_temporary(l);
    (void)close_owned(l, &l->temporary); (void)close_owned(l, &l->directory);
    (void)close_owned(l, &l->agent); (void)close_owned(l, &l->java); (void)close_owned(l, &l->reference);
    (void)close_owned(l, &l->jar); (void)close_owned(l, &l->create_script); (void)close_owned(l, &l->script);
    (void)close_owned(l, &l->shell); (void)close_owned(l, &l->root);
}
static _Noreturn void finish(struct launcher *l)
{
    cleanup(l);
    struct sigaction ignored; memset(&ignored, 0, sizeof(ignored)); ignored.sa_handler = SIG_IGN;
    int can_write = sigaction(SIGPIPE, &ignored, NULL) == 0;
    char diagnostic[128];
    int size = snprintf(diagnostic, sizeof(diagnostic), "AutoSourcarr source launcher refused: stage=%u errno=%u cleanup=%u published=%u\n",
        (unsigned)l->primary_stage, (unsigned)l->primary_errno, l->cleanup, (unsigned)l->published);
    if (can_write && size > 0 && (size_t)size < sizeof(diagnostic)) {
        size_t at = 0;
        while (at < (size_t)size) {
            ssize_t count = write(2, diagnostic + at, (size_t)size - at);
            if (count > 0) { at += (size_t)count; continue; }
            if (count < 0 && errno == EINTR) continue;
            break;
        }
    }
    explicit_bzero(l, sizeof(*l)); _exit(78);
}

int main(int argc, char **argv)
{
    struct launcher l; memset(&l, 0, sizeof(l));
    l.root = 5;
    l.directory = l.shell = l.script = l.create_script = l.jar = l.reference = l.java = l.agent = l.temporary = -1;
    if (argc != 2 || argv == NULL || argv[1] == NULL || strcmp(argv[1], "--native-source-root-fd=5") != 0) {
        (void)refuse(&l, STAGE_ARGUMENTS, EINVAL); finish(&l);
    }
    if (signal_policy(&l, 0) != 0 || validate_source_binding(&l) != 0 || prepare_environment(&l) != 0) finish(&l);
    l.shell = fixed_asset(&l, SOURCE_SHELL, 0, 16 * 1024 * 1024, 1);
    if (l.shell < 0) finish(&l);
    l.script = fixed_asset(&l, STARTUP_SCRIPT, 18204, 18204, 2);
    if (l.script < 0) finish(&l);
    l.create_script = fixed_asset(&l, CREATE_SCRIPT, 482, 482, 2);
    if (l.create_script < 0) finish(&l);
    l.jar = fixed_asset(&l, SOURCE_JAR, 174128768, 174128768, 0);
    if (l.jar < 0) finish(&l);
    l.reference = fixed_asset(&l, REFERENCE, REFERENCE_BYTES, REFERENCE_BYTES, 0);
    if (l.reference < 0 || fstat(l.reference, &l.reference_status) != 0) { (void)refuse(&l, STAGE_ASSETS, errno); finish(&l); }
    l.java = fixed_asset(&l, JAVA_BINARY, 0, 16 * 1024 * 1024, 1);
    if (l.java < 0 || verify_java_mapping(&l) != 0) finish(&l);
    if (l.with_agent) { l.agent = fixed_asset(&l, AGENT_JAR, 0, 16 * 1024 * 1024, 0); if (l.agent < 0) finish(&l); }
    if (cancelled(&l) != 0 || ensure_server_conf(&l) != 0 || root_current(&l) != 0 || cancelled(&l) != 0) finish(&l);
    (void)close_owned(&l, &l.directory); (void)close_owned(&l, &l.reference); (void)close_owned(&l, &l.jar);
    (void)close_owned(&l, &l.create_script); (void)close_owned(&l, &l.script); (void)close_owned(&l, &l.java);
    (void)close_owned(&l, &l.agent); (void)close_owned(&l, &l.root);
    if (l.primary_stage != STAGE_NONE || cancelled(&l) != 0) finish(&l);
    if ((l.shell > 3 && syscall(SYS_close_range, 3U, (unsigned)l.shell - 1U, 0) != 0)
        || syscall(SYS_close_range, (unsigned)l.shell + 1U, ~0U, 0) != 0) {
        (void)refuse(&l, STAGE_CLEANUP, errno); finish(&l);
    }
    int death = 0;
    if (signal_policy(&l, 1) != 0 || getppid() != l.parent || getsid(0) != l.leader || getpgrp() != l.leader
        || prctl(PR_GET_PDEATHSIG, &death, 0L, 0L, 0L) != 0 || death != SIGKILL || cancelled(&l) != 0) {
        (void)refuse(&l, STAGE_SIGNAL, EPERM); finish(&l);
    }
    char *const arguments[] = {SOURCE_SHELL, STARTUP_SCRIPT, NULL};
    (void)syscall(SYS_execveat, l.shell, "", arguments, l.environment, AT_EMPTY_PATH);
    (void)refuse(&l, STAGE_EXEC, errno); finish(&l);
}
