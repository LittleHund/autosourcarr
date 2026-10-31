#define _GNU_SOURCE
#include "startup-resolver.h"
#include "process-identity.h"
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <poll.h>
#include <stdlib.h>
#include <string.h>
#include <sys/fsuid.h>
#include <sys/prctl.h>
#include <sys/random.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/sysmacros.h>
#include <sys/wait.h>
#include <sys/xattr.h>
#include <time.h>
#include <unistd.h>

#if !defined(__linux__) || !defined(__x86_64__)
#error "The fixed startup resolver requires the Linux x86_64 ABI."
#endif

#define ENVIRONMENT_MAXIMUM (2U * 1024U * 1024U)
#define ENVIRONMENT_ENTRIES 8192U
#define ENVIRONMENT_ENTRY_MAXIMUM 131072U
#define ASSET_ENTRIES_MAXIMUM 65536U
#define ASSET_DEPTH_MAXIMUM 32U

static const unsigned char record_magic[8] = {'A','S','R','C','H','K','1','\n'};
struct environment { char **items; size_t count, bytes; };
struct transaction {
    int challenge[2], frame[2], errors[2], stderr_pipe[2];
    pid_t child;
    int observed_exit, reaped, eof_frame, eof_error, eof_stderr, write_complete;
    int frame_bad, errors_bad, clock_failed, signal_failed, cleaning;
    size_t challenge_written, error_length;
    unsigned char error_bytes[33];
    unsigned char challenge_bytes[65];
    uint64_t deadline;
    struct autosourcarr_startup_resolver_result *result;
};

static uint32_t read32(const unsigned char *p)
{ return (uint32_t)p[0] | (uint32_t)p[1] << 8 | (uint32_t)p[2] << 16 | (uint32_t)p[3] << 24; }
static void write32(unsigned char *p, uint32_t value)
{ for (size_t i = 0; i < 4; i++) p[i] = (unsigned char)(value >> (8U * i)); }
static int now_ms(uint64_t *now)
{
    struct timespec time;
    if (clock_gettime(CLOCK_MONOTONIC, &time) != 0 || time.tv_sec < 0) return -1;
    *now = (uint64_t)time.tv_sec * 1000U + (uint64_t)time.tv_nsec / 1000000U;
    return 0;
}
static int interrupted(const volatile sig_atomic_t *cancelled) { return cancelled != NULL && *cancelled != 0; }
static void diagnose(struct transaction *t, enum autosourcarr_resolver_diagnostic diagnostic)
{
    if (t->result->diagnostic == AUTOSOURCARR_RESOLVER_DIAG_NONE) t->result->diagnostic = diagnostic;
    if (!t->cleaning) return;
    if (diagnostic == AUTOSOURCARR_RESOLVER_DIAG_WAIT) t->result->cleanup_errors |= AUTOSOURCARR_RESOLVER_CLEANUP_WAIT;
    else if (diagnostic == AUTOSOURCARR_RESOLVER_DIAG_SIGNAL) t->result->cleanup_errors |= AUTOSOURCARR_RESOLVER_CLEANUP_SIGNAL;
    else if (diagnostic == AUTOSOURCARR_RESOLVER_DIAG_CLOCK) t->result->cleanup_errors |= AUTOSOURCARR_RESOLVER_CLEANUP_CLOCK;
    else if (diagnostic == AUTOSOURCARR_RESOLVER_DIAG_PIPE || diagnostic == AUTOSOURCARR_RESOLVER_DIAG_PROTOCOL) t->result->cleanup_errors |= AUTOSOURCARR_RESOLVER_CLEANUP_PIPE;
}
static int budget(const volatile sig_atomic_t *cancelled, uint64_t deadline)
{
    uint64_t now;
    if (interrupted(cancelled)) { errno = ECANCELED; return -1; }
    if (now_ms(&now) != 0) return -1;
    if (now >= deadline) { errno = ETIMEDOUT; return -1; }
    return 0;
}
static void close_fd(int *fd) { if (*fd >= 0) { int old = *fd; *fd = -1; (void)close(old); } }
static void close_all(struct transaction *t)
{
    for (size_t i = 0; i < 2; i++) { close_fd(&t->challenge[i]); close_fd(&t->frame[i]); close_fd(&t->errors[i]); close_fd(&t->stderr_pipe[i]); }
}
static int make_nonblocking(int fd)
{ int flags = fcntl(fd, F_GETFL); return flags < 0 ? -1 : fcntl(fd, F_SETFL, flags | O_NONBLOCK); }
static int prefix(const char *key, size_t size, const char *wanted)
{ size_t length = strlen(wanted); return size >= length && memcmp(key, wanted, length) == 0; }
static int key_is(const char *key, size_t size, const char *wanted)
{ return size == strlen(wanted) && memcmp(key, wanted, size) == 0; }
static int loader_key(const char *key, size_t size)
{
    return prefix(key, size, "LD_") || prefix(key, size, "DOTNET_") || prefix(key, size, "COMPlus_")
        || prefix(key, size, "CORECLR_") || prefix(key, size, "COREHOST_") || prefix(key, size, "COR_")
        || prefix(key, size, "MONO_") || prefix(key, size, "MALLOC_")
        || key_is(key, size, "GLIBC_TUNABLES") || key_is(key, size, "GCONV_PATH")
        || key_is(key, size, "LOCPATH") || key_is(key, size, "NLSPATH")
        || key_is(key, size, "LIBPATH") || key_is(key, size, "SHLIB_PATH") || key_is(key, size, "_RLD_LIST")
        || key_is(key, size, "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES") || key_is(key, size, "ASPNETCORE_HOSTINGSTARTUPEXCLUDEASSEMBLIES");
}
static void environment_free(struct environment *environment)
{
    if (environment->items != NULL) {
        for (size_t i = 0; i < environment->count; i++) {
            explicit_bzero(environment->items[i], strlen(environment->items[i])); free(environment->items[i]);
        }
        free(environment->items);
    }
    memset(environment, 0, sizeof(*environment));
}
static int environment_add(struct environment *environment, const char *entry, size_t length)
{
    if (environment->count >= ENVIRONMENT_ENTRIES + 8U || length + 1 > ENVIRONMENT_MAXIMUM - environment->bytes) { errno = E2BIG; return -1; }
    char *copy = malloc(length + 1); if (copy == NULL) return -1;
    memcpy(copy, entry, length); copy[length] = 0;
    environment->items[environment->count++] = copy; environment->items[environment->count] = NULL;
    environment->bytes += length + 1; return 0;
}
static int environment_snapshot(const char *const input[], struct environment *output,
    const volatile sig_atomic_t *cancelled, uint64_t deadline)
{
    if (input == NULL) { errno = EINVAL; return -1; }
    output->items = calloc(ENVIRONMENT_ENTRIES + 9U, sizeof(char *)); if (output->items == NULL) return -1;
    for (size_t i = 0; i <= ENVIRONMENT_ENTRIES; i++) {
        if (budget(cancelled, deadline) != 0) return -1;
        if (input[i] == NULL) {
            static const char *const fixed[] = {
                "DOTNET_ROOT=/usr/share/dotnet", "DOTNET_MULTILEVEL_LOOKUP=0", "DOTNET_EnableDiagnostics=0",
                "DOTNET_DiagnosticPorts=", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
            };
            for (size_t j = 0; j < sizeof(fixed) / sizeof(fixed[0]); j++) if (environment_add(output, fixed[j], strlen(fixed[j])) != 0) return -1;
            return 0;
        }
        if (i == ENVIRONMENT_ENTRIES) { errno = E2BIG; return -1; }
        size_t length = strnlen(input[i], ENVIRONMENT_ENTRY_MAXIMUM + 1U);
        const char *separator = memchr(input[i], '=', length);
        if (length == 0 || length > ENVIRONMENT_ENTRY_MAXIMUM || separator == NULL || separator == input[i]) { errno = EINVAL; return -1; }
        size_t key_length = (size_t)(separator - input[i]);
        if (loader_key(input[i], key_length)) continue;
        if (environment_add(output, input[i], length) != 0) return -1;
    }
    errno = E2BIG; return -1;
}

/* Fixed no-follow image roots only; no input path is accepted by the public API. */
static int fixed_directory(const char *path)
{
    char copy[PATH_MAX];
    if (path[0] != '/' || strlen(path) >= sizeof(copy)) { errno = EINVAL; return -1; }
    memcpy(copy, path + 1, strlen(path));
    int current = open("/", O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (current < 0) return -1;
    struct stat status;
    if (fstat(current, &status) != 0 || status.st_uid != 0 || (status.st_mode & 0022) != 0) { (void)close(current); errno = EPERM; return -1; }
    char *save = NULL, *part = strtok_r(copy, "/", &save);
    while (part != NULL) {
        int next = openat(current, part, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        if (next < 0 || fstat(next, &status) != 0 || !S_ISDIR(status.st_mode) || status.st_nlink == 0
            || status.st_uid != 0 || (status.st_mode & 0022) != 0) {
            int saved = errno == 0 ? EPERM : errno; if (next >= 0) (void)close(next); (void)close(current); errno = saved; return -1;
        }
        (void)close(current); current = next; part = strtok_r(NULL, "/", &save);
    }
    return current;
}
static int immutable_file(int parent, const char *leaf, int elf)
{
    int fd = openat(parent, leaf, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC);
    if (fd < 0) return -1;
    struct stat status; unsigned char bytes[20];
    int valid = fstat(fd, &status) == 0 && S_ISREG(status.st_mode) && status.st_nlink == 1
        && status.st_uid == 0 && (status.st_mode & 0022) == 0 && (status.st_mode & 06000) == 0;
    if (elf) valid = valid && (status.st_mode & 0111) != 0 && pread(fd, bytes, sizeof(bytes), 0) == (ssize_t)sizeof(bytes)
        && memcmp(bytes, "\177ELF\002\001", 6) == 0 && bytes[18] == 62 && bytes[19] == 0;
    errno = 0; ssize_t capability = fgetxattr(fd, "security.capability", NULL, 0);
    valid = valid && capability == -1 && errno == ENODATA;
    if (!valid) { (void)close(fd); errno = EPERM; return -1; }
    return fd;
}
static int suffix(const char *name, const char *ending)
{ size_t size = strlen(name), tail = strlen(ending); return size >= tail && memcmp(name + size - tail, ending, tail) == 0; }
static int immutable_tree(int directory, unsigned depth, size_t *entries, int app_flat)
{
    if (depth > ASSET_DEPTH_MAXIMUM) { errno = E2BIG; return -1; }
    int duplicate = fcntl(directory, F_DUPFD_CLOEXEC, 32); if (duplicate < 0) return -1;
    DIR *listing = fdopendir(duplicate); if (listing == NULL) { (void)close(duplicate); return -1; }
    int valid = 1; struct dirent *entry;
    for (;;) {
        errno = 0; entry = readdir(listing);
        if (entry == NULL) { if (errno != 0) valid = 0; break; }
        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) continue;
        if (++*entries > ASSET_ENTRIES_MAXIMUM) { errno = E2BIG; valid = 0; break; }
        int runtime = app_flat && strcmp(entry->d_name, "runtimes") == 0;
        if (app_flat && !runtime && !suffix(entry->d_name, ".dll") && !suffix(entry->d_name, ".so")) continue;
        struct stat status;
        if (fstatat(directory, entry->d_name, &status, AT_SYMLINK_NOFOLLOW) != 0) { valid = 0; break; }
        if (S_ISDIR(status.st_mode)) {
            if (status.st_uid != 0 || (status.st_mode & 0022) != 0 || (app_flat && !runtime)) { valid = 0; break; }
            int child = openat(directory, entry->d_name, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
            if (child < 0) { valid = 0; break; }
            int checked = immutable_tree(child, depth + 1U, entries, 0); (void)close(child);
            if (checked != 0) { valid = 0; break; }
        } else {
            int file = immutable_file(directory, entry->d_name, 0);
            if (file < 0) { valid = 0; break; }
            (void)close(file);
        }
    }
    int saved = errno;
    if (closedir(listing) != 0) valid = 0;
    if (!valid) { errno = saved == 0 ? EPERM : saved; return -1; }
    return 0;
}
static void child_record(int fd, uint32_t kind, uint32_t value)
{
    unsigned char record[16]; memcpy(record, record_magic, 8); write32(record + 8, kind); write32(record + 12, value);
    size_t at = 0;
    while (at < sizeof(record)) {
        ssize_t wrote = write(fd, record + at, sizeof(record) - at);
        if (wrote > 0) { at += (size_t)wrote; continue; }
        if (wrote < 0 && errno == EINTR) continue;
        break;
    }
}
static _Noreturn void child_fail(int fd, enum autosourcarr_resolver_child_stage stage)
{
    int error = errno; child_record(fd, 2, ((uint32_t)stage << 16) | ((uint32_t)error & 0xffffU)); _exit(126);
}
static _Noreturn void child_run(struct transaction *t, struct environment *environment, pid_t expected_parent)
{
    int error_fd = t->errors[1];
    if (autosourcarr_drop_identity(568, 568, NULL, 0, 1) != 0
        || (uint32_t)setfsuid((uid_t)-1) != 568 || (uint32_t)setfsgid((gid_t)-1) != 568) child_fail(error_fd, AUTOSOURCARR_RESOLVER_CHILD_DROP);
    sigset_t child_mask; sigemptyset(&child_mask);
    struct sigaction defaults; memset(&defaults, 0, sizeof(defaults)); defaults.sa_handler = SIG_DFL;
    if (sigprocmask(SIG_SETMASK, &child_mask, NULL) != 0 || sigaction(SIGTERM, &defaults, NULL) != 0
        || sigaction(SIGINT, &defaults, NULL) != 0 || sigaction(SIGPIPE, &defaults, NULL) != 0
        || sigaction(SIGCHLD, &defaults, NULL) != 0 || sigaction(SIGHUP, &defaults, NULL) != 0)
        child_fail(error_fd, AUTOSOURCARR_RESOLVER_CHILD_PARENT);
    int death_signal = 0;
    if (setsid() < 0 || prctl(PR_SET_PDEATHSIG, SIGKILL, 0L, 0L, 0L) != 0
        || prctl(PR_GET_PDEATHSIG, &death_signal, 0L, 0L, 0L) != 0 || death_signal != SIGKILL
        || getppid() != expected_parent) child_fail(error_fd, AUTOSOURCARR_RESOLVER_CHILD_PARENT);
    int runtime = fixed_directory("/usr/share/dotnet"), app = fixed_directory(AUTOSOURCARR_RESOLVER_ASSET_ROOT);
    if (runtime < 0 || app < 0) child_fail(error_fd, AUTOSOURCARR_RESOLVER_CHILD_ASSETS);
    size_t entries = 0;
    if (immutable_tree(runtime, 0, &entries, 0) != 0 || immutable_tree(app, 0, &entries, 1) != 0) child_fail(error_fd, AUTOSOURCARR_RESOLVER_CHILD_ASSETS);
    static const char *const required[] = {
        "AutoSourcarr.Bootstrap.dll", "AutoSourcarr.Bootstrap.deps.json", "AutoSourcarr.Bootstrap.runtimeconfig.json",
        "AutoSourcarr.Core.dll", "AutoSourcarr.Infrastructure.dll", "AutoSourcarr.Repair.dll"
    };
    for (size_t i = 0; i < sizeof(required) / sizeof(required[0]); i++) {
        int file = immutable_file(app, required[i], 0); if (file < 0) child_fail(error_fd, AUTOSOURCARR_RESOLVER_CHILD_ASSETS); (void)close(file);
    }
    int executable = immutable_file(runtime, "dotnet", 1);
    int null_input = open("/dev/null", O_RDWR | O_NOFOLLOW | O_CLOEXEC);
    struct stat null_status;
    if (executable < 0 || null_input < 0 || fstat(null_input, &null_status) != 0 || !S_ISCHR(null_status.st_mode)
        || major(null_status.st_rdev) != 1 || minor(null_status.st_rdev) != 3) child_fail(error_fd, AUTOSOURCARR_RESOLVER_CHILD_ASSETS);
    /* Normalize ALL sources before touching fd0..6, avoiding low-fd/remapping
     * collisions even when the native caller initially had closed stdio. */
    int sources[6] = {t->challenge[0], t->frame[1], t->stderr_pipe[1], t->errors[1], executable, null_input};
    int kept[6];
    for (size_t i = 0; i < 6; i++) {
        kept[i] = fcntl(sources[i], F_DUPFD_CLOEXEC, 64);
        if (kept[i] < 0) child_fail(error_fd, AUTOSOURCARR_RESOLVER_CHILD_DESCRIPTORS);
    }
    error_fd = kept[3];
    if (dup2(kept[5], 0) < 0 || dup2(kept[5], 1) < 0 || dup2(kept[2], 2) < 0
        || dup2(kept[0], 3) < 0 || dup2(kept[1], 4) < 0
        || dup3(kept[4], 5, O_CLOEXEC) < 0 || dup3(kept[3], 6, O_CLOEXEC) < 0) child_fail(error_fd, AUTOSOURCARR_RESOLVER_CHILD_DESCRIPTORS);
    error_fd = 6;
    if (syscall(SYS_close_range, 7U, ~0U, 0) != 0) child_fail(error_fd, AUTOSOURCARR_RESOLVER_CHILD_DESCRIPTORS);
    child_record(error_fd, 1, 0x0fU); /* Real shared drop + parent/session/assets/fd checks. */
    char *const arguments[] = {AUTOSOURCARR_RESOLVER_DOTNET, AUTOSOURCARR_RESOLVER_ASSEMBLY, "--resolve-native-startup", NULL};
    (void)syscall(SYS_execveat, 5, "", arguments, environment->items, AT_EMPTY_PATH);
    child_fail(error_fd, AUTOSOURCARR_RESOLVER_CHILD_EXEC);
}

static int kernel_nonce(unsigned char hex[64], const volatile sig_atomic_t *cancelled, uint64_t deadline)
{
    static const unsigned char digits[] = "0123456789abcdef";
    unsigned char random[32]; size_t at = 0;
    while (at < sizeof(random)) {
        if (budget(cancelled, deadline) != 0) { explicit_bzero(random, sizeof(random)); return -1; }
        ssize_t count = getrandom(random + at, sizeof(random) - at, GRND_NONBLOCK);
        if (count > 0) { at += (size_t)count; continue; }
        if (count < 0 && errno == EINTR) continue;
        explicit_bzero(random, sizeof(random)); return -1;
    }
    for (size_t i = 0; i < sizeof(random); i++) { hex[2 * i] = digits[random[i] >> 4]; hex[2 * i + 1] = digits[random[i] & 15U]; }
    explicit_bzero(random, sizeof(random)); return 0;
}
static int pipe_setup(struct transaction *t)
{
    return pipe2(t->challenge, O_CLOEXEC) != 0 || pipe2(t->frame, O_CLOEXEC) != 0
        || pipe2(t->errors, O_CLOEXEC) != 0 || pipe2(t->stderr_pipe, O_CLOEXEC) != 0
        || make_nonblocking(t->challenge[1]) != 0 || make_nonblocking(t->frame[0]) != 0
        || make_nonblocking(t->errors[0]) != 0 || make_nonblocking(t->stderr_pipe[0]) != 0 ? -1 : 0;
}
static void inspect_exit(struct transaction *t)
{
    if (t->observed_exit || t->reaped) return;
    siginfo_t info; memset(&info, 0, sizeof(info));
    if (waitid(P_PID, (id_t)t->child, &info, WEXITED | WNOHANG | WNOWAIT) != 0) {
        if (errno != EINTR) diagnose(t, AUTOSOURCARR_RESOLVER_DIAG_WAIT);
    } else if (info.si_pid == t->child) t->observed_exit = 1;
}
static void reap_if_complete(struct transaction *t)
{
    if (!t->observed_exit || t->reaped || !t->eof_frame || !t->eof_stderr || !t->eof_error) return;
    int status; pid_t child;
    do { child = waitpid(t->child, &status, WNOHANG); } while (child < 0 && errno == EINTR);
    if (child == t->child) { t->reaped = 1; t->result->wait_status = status; t->result->facts |= AUTOSOURCARR_RESOLVER_FACT_REAPED; }
    else if (child < 0) diagnose(t, AUTOSOURCARR_RESOLVER_DIAG_WAIT);
}
static void drain_frame(struct transaction *t)
{
    unsigned char bytes[4096];
    for (size_t round = 0; t->frame[0] >= 0 && round < 8; round++) {
        ssize_t count = read(t->frame[0], bytes, sizeof(bytes));
        if (count > 0) {
            size_t size = (size_t)count;
            if (size > AUTOSOURCARR_STARTUP_MAXIMUM - t->result->frame_length) t->frame_bad = 1;
            else if (!t->frame_bad) { memcpy(t->result->frame + t->result->frame_length, bytes, size); t->result->frame_length += size; }
            explicit_bzero(bytes, sizeof(bytes)); continue;
        }
        if (count == 0) { t->eof_frame = 1; t->result->facts |= AUTOSOURCARR_RESOLVER_FACT_FRAME_EOF; close_fd(&t->frame[0]); return; }
        if (errno == EINTR) continue;
        if (errno != EAGAIN && errno != EWOULDBLOCK) { t->frame_bad = 1; diagnose(t, AUTOSOURCARR_RESOLVER_DIAG_PIPE); close_fd(&t->frame[0]); }
        return;
    }
}
static void drain_stderr(struct transaction *t)
{
    unsigned char bytes[4096];
    for (size_t round = 0; t->stderr_pipe[0] >= 0 && round < 8; round++) {
        ssize_t count = read(t->stderr_pipe[0], bytes, sizeof(bytes));
        if (count > 0) {
            if ((uint64_t)count <= UINT64_MAX - t->result->discarded_stderr_bytes) t->result->discarded_stderr_bytes += (uint64_t)count;
            explicit_bzero(bytes, sizeof(bytes)); continue;
        }
        if (count == 0) { t->eof_stderr = 1; t->result->facts |= AUTOSOURCARR_RESOLVER_FACT_STDERR_EOF; close_fd(&t->stderr_pipe[0]); return; }
        if (errno == EINTR) continue;
        if (errno != EAGAIN && errno != EWOULDBLOCK) { diagnose(t, AUTOSOURCARR_RESOLVER_DIAG_PIPE); close_fd(&t->stderr_pipe[0]); }
        return;
    }
}
static void drain_errors(struct transaction *t)
{
    unsigned char bytes[33];
    for (size_t round = 0; t->errors[0] >= 0 && round < 4; round++) {
        ssize_t count = read(t->errors[0], bytes, sizeof(bytes));
        if (count > 0) {
            if ((size_t)count > 32U - t->error_length) t->errors_bad = 1;
            else if (!t->errors_bad) { memcpy(t->error_bytes + t->error_length, bytes, (size_t)count); t->error_length += (size_t)count; }
            continue;
        }
        if (count == 0) { t->eof_error = 1; t->result->facts |= AUTOSOURCARR_RESOLVER_FACT_ERROR_EOF; close_fd(&t->errors[0]); return; }
        if (errno == EINTR) continue;
        if (errno != EAGAIN && errno != EWOULDBLOCK) { t->errors_bad = 1; diagnose(t, AUTOSOURCARR_RESOLVER_DIAG_PIPE); close_fd(&t->errors[0]); }
        return;
    }
}
static void inspect_records(struct transaction *t)
{
    for (size_t at = 0; at + 16U <= t->error_length; at += 16U) {
        const unsigned char *record = t->error_bytes + at;
        if (memcmp(record, record_magic, 8) != 0) { t->errors_bad = 1; continue; }
        uint32_t kind = read32(record + 8), value = read32(record + 12);
        if (at == 0 && kind == 1 && value == 0x0fU) t->result->facts |= AUTOSOURCARR_RESOLVER_FACT_CHILD_VERIFIED;
        else if (kind == 2 && (value >> 16) >= AUTOSOURCARR_RESOLVER_CHILD_DROP && (value >> 16) <= AUTOSOURCARR_RESOLVER_CHILD_EXEC) {
            t->result->child_error_stage = (uint16_t)(value >> 16); t->result->child_error_errno = (uint16_t)value;
            diagnose(t, AUTOSOURCARR_RESOLVER_DIAG_CHILD);
        } else t->errors_bad = 1;
    }
    if (t->eof_error && t->error_length % 16U != 0) t->errors_bad = 1;
}
static void pump(struct transaction *t, int send_challenge)
{
    if (send_challenge && t->challenge[1] >= 0) {
        ssize_t wrote = write(t->challenge[1], t->challenge_bytes + t->challenge_written, sizeof(t->challenge_bytes) - t->challenge_written);
        if (wrote > 0) {
            t->challenge_written += (size_t)wrote;
            if (t->challenge_written == sizeof(t->challenge_bytes)) { t->write_complete = 1; close_fd(&t->challenge[1]); }
        } else if (wrote == 0 || (errno != EINTR && errno != EAGAIN && errno != EWOULDBLOCK)) {
            diagnose(t, AUTOSOURCARR_RESOLVER_DIAG_PIPE); close_fd(&t->challenge[1]);
        }
    }
    drain_frame(t); drain_stderr(t); drain_errors(t); inspect_records(t); inspect_exit(t); reap_if_complete(t);
}
static void wait_activity(struct transaction *t)
{
    struct pollfd fds[4] = {
        {.fd = t->challenge[1], .events = POLLOUT}, {.fd = t->frame[0], .events = POLLIN},
        {.fd = t->stderr_pipe[0], .events = POLLIN}, {.fd = t->errors[0], .events = POLLIN}
    };
    if (poll(fds, 4, 25) < 0 && errno != EINTR) diagnose(t, AUTOSOURCARR_RESOLVER_DIAG_PIPE);
}
static void signal_owned(struct transaction *t, int number)
{
    if (t->child <= 0 || t->reaped) return;
    /* Its actual child PID remains reserved until EOF/reap. No cached group
     * signal is sent after reaping, and RUID568 supplies real kill permission. */
    if (kill(t->child, number) != 0 && errno != ESRCH) { t->signal_failed = 1; diagnose(t, AUTOSOURCARR_RESOLVER_DIAG_SIGNAL); }
    if (kill(-t->child, number) != 0 && errno != ESRCH) { t->signal_failed = 1; diagnose(t, AUTOSOURCARR_RESOLVER_DIAG_SIGNAL); }
}
static void final_failure_reap(struct transaction *t)
{
    /* Cleanup may have lost EOF due to a real pipe error. Still reap an exited
     * direct child if possible, while retaining the unresolved EOF result. No
     * more PID/group signals occur after this final best-effort reap. */
    if (t->reaped || t->child <= 0) return;
    int status; pid_t child;
    do { child = waitpid(t->child, &status, WNOHANG); } while (child < 0 && errno == EINTR);
    if (child == t->child) { t->reaped = 1; t->result->wait_status = status; t->result->facts |= AUTOSOURCARR_RESOLVER_FACT_REAPED; }
    else if (child < 0) diagnose(t, AUTOSOURCARR_RESOLVER_DIAG_WAIT);
}
static int reconcile(struct transaction *t)
{
    close_fd(&t->challenge[1]);
    uint64_t now, term_until;
    if (now_ms(&now) != 0) { t->clock_failed = 1; diagnose(t, AUTOSOURCARR_RESOLVER_DIAG_CLOCK); signal_owned(t, SIGKILL); pump(t, 0); final_failure_reap(t); return -1; }
    term_until = now + AUTOSOURCARR_RESOLVER_TERM_MS; signal_owned(t, SIGTERM);
    while (!t->reaped) {
        pump(t, 0); if (t->reaped) break;
        if (now_ms(&now) != 0) { t->clock_failed = 1; diagnose(t, AUTOSOURCARR_RESOLVER_DIAG_CLOCK); break; }
        if (now >= term_until) break;
        wait_activity(t);
    }
    if (t->reaped) return t->eof_frame && t->eof_stderr && t->eof_error && !t->signal_failed && !t->clock_failed ? 0 : -1;
    signal_owned(t, SIGKILL);
    if (now_ms(&now) != 0) { t->clock_failed = 1; diagnose(t, AUTOSOURCARR_RESOLVER_DIAG_CLOCK); pump(t, 0); final_failure_reap(t); return -1; }
    uint64_t kill_until = now + AUTOSOURCARR_RESOLVER_KILL_JOIN_MS;
    while (!t->reaped) {
        pump(t, 0); if (t->reaped) break;
        if (now_ms(&now) != 0) { t->clock_failed = 1; diagnose(t, AUTOSOURCARR_RESOLVER_DIAG_CLOCK); break; }
        if (now >= kill_until) break;
        wait_activity(t);
    }
    final_failure_reap(t);
    return t->reaped && t->eof_frame && t->eof_stderr && t->eof_error && !t->signal_failed && !t->clock_failed ? 0 : -1;
}
static int drop_parent(struct autosourcarr_startup_resolver_result *result)
{
    if (autosourcarr_drop_identity(568, 568, NULL, 0, 1) == 0) { result->facts |= AUTOSOURCARR_RESOLVER_FACT_PARENT_DROPPED; return 0; }
    result->parent_drop_errno = errno; result->cleanup_errors |= AUTOSOURCARR_RESOLVER_CLEANUP_AUTHORITY; return -1;
}

enum autosourcarr_resolver_status autosourcarr_startup_resolve(const char *const envp[],
    const volatile sig_atomic_t *cancelled, struct autosourcarr_startup_resolver_result *result)
{
    if (result == NULL) {
        return autosourcarr_drop_identity(568, 568, NULL, 0, 1) == 0
            ? AUTOSOURCARR_RESOLVER_REFUSED : AUTOSOURCARR_RESOLVER_UNRESOLVED;
    }
    memset(result, 0, sizeof(*result)); result->status = AUTOSOURCARR_RESOLVER_REFUSED;
    struct transaction t; memset(&t, 0, sizeof(t));
    for (size_t i = 0; i < 2; i++) t.challenge[i] = t.frame[i] = t.errors[i] = t.stderr_pipe[i] = -1;
    t.result = result;
    struct environment environment; memset(&environment, 0, sizeof(environment));
    sigset_t blocked, previous, pending; int signal_masked = 0, prior_sigpipe = 0;
    uint64_t now;
    uid_t real, effective, saved;
    if (getresuid(&real, &effective, &saved) != 0 || real != 568
        || !((effective == 0 && saved == 0) || (effective == 568 && saved == 568))) {
        result->diagnostic = AUTOSOURCARR_RESOLVER_DIAG_PARENT; goto failed;
    }
    struct sigaction child_action;
    if (sigaction(SIGCHLD, NULL, &child_action) != 0 || child_action.sa_handler == SIG_IGN || (child_action.sa_flags & SA_NOCLDWAIT) != 0) {
        result->diagnostic = AUTOSOURCARR_RESOLVER_DIAG_WAIT; goto failed;
    }
    if (now_ms(&now) != 0) { result->diagnostic = AUTOSOURCARR_RESOLVER_DIAG_CLOCK; goto failed; }
    t.deadline = now + AUTOSOURCARR_RESOLVER_BUDGET_MS;
    if (environment_snapshot(envp, &environment, cancelled, t.deadline) != 0) {
        result->diagnostic = errno == ECANCELED ? AUTOSOURCARR_RESOLVER_DIAG_CANCEL : errno == ETIMEDOUT ? AUTOSOURCARR_RESOLVER_DIAG_DEADLINE : AUTOSOURCARR_RESOLVER_DIAG_ENVIRONMENT; goto failed;
    }
    if (kernel_nonce(result->nonce, cancelled, t.deadline) != 0) {
        result->diagnostic = errno == ECANCELED ? AUTOSOURCARR_RESOLVER_DIAG_CANCEL : errno == ETIMEDOUT ? AUTOSOURCARR_RESOLVER_DIAG_DEADLINE : AUTOSOURCARR_RESOLVER_DIAG_NONCE; goto failed;
    }
    memcpy(t.challenge_bytes, result->nonce, 64); t.challenge_bytes[64] = '\n';
    if (pipe_setup(&t) != 0) { result->diagnostic = AUTOSOURCARR_RESOLVER_DIAG_PIPE; goto failed; }
    sigemptyset(&blocked); sigaddset(&blocked, SIGPIPE);
    if (sigpending(&pending) != 0 || sigprocmask(SIG_BLOCK, &blocked, &previous) != 0) { result->diagnostic = AUTOSOURCARR_RESOLVER_DIAG_SIGNAL; goto failed; }
    signal_masked = 1; prior_sigpipe = sigismember(&pending, SIGPIPE);
    if (budget(cancelled, t.deadline) != 0) { result->diagnostic = errno == ECANCELED ? AUTOSOURCARR_RESOLVER_DIAG_CANCEL : AUTOSOURCARR_RESOLVER_DIAG_DEADLINE; goto failed; }
    pid_t parent = getpid(); t.child = fork();
    if (t.child < 0) { t.child = 0; result->diagnostic = AUTOSOURCARR_RESOLVER_DIAG_FORK; goto failed; }
    if (t.child == 0) child_run(&t, &environment, parent);
    result->child_pid = t.child;
    close_fd(&t.challenge[0]); close_fd(&t.frame[1]); close_fd(&t.errors[1]); close_fd(&t.stderr_pipe[1]);
    result->status = AUTOSOURCARR_RESOLVER_FAILED;
    for (;;) {
        pump(&t, 1);
        if (interrupted(cancelled)) { result->diagnostic = AUTOSOURCARR_RESOLVER_DIAG_CANCEL; break; }
        if (now_ms(&now) != 0) { t.clock_failed = 1; result->diagnostic = AUTOSOURCARR_RESOLVER_DIAG_CLOCK; break; }
        if (now >= t.deadline) { result->diagnostic = AUTOSOURCARR_RESOLVER_DIAG_DEADLINE; break; }
        if (t.frame_bad || t.errors_bad) { result->diagnostic = AUTOSOURCARR_RESOLVER_DIAG_PROTOCOL; break; }
        if (result->diagnostic != AUTOSOURCARR_RESOLVER_DIAG_NONE) break;
        if (t.reaped) {
            struct autosourcarr_startup_request requested;
            if (!WIFEXITED(result->wait_status) || WEXITSTATUS(result->wait_status) != 0) {
                result->diagnostic = AUTOSOURCARR_RESOLVER_DIAG_CHILD; break;
            }
            if (t.eof_error && t.error_length == 16U && WIFEXITED(result->wait_status) && WEXITSTATUS(result->wait_status) == 0
                && (result->facts & AUTOSOURCARR_RESOLVER_FACT_CHILD_VERIFIED) != 0) result->facts |= AUTOSOURCARR_RESOLVER_FACT_EXEC_CONFIRMED;
            uint32_t required = AUTOSOURCARR_RESOLVER_FACT_CHILD_VERIFIED | AUTOSOURCARR_RESOLVER_FACT_EXEC_CONFIRMED
                | AUTOSOURCARR_RESOLVER_FACT_FRAME_EOF | AUTOSOURCARR_RESOLVER_FACT_STDERR_EOF
                | AUTOSOURCARR_RESOLVER_FACT_ERROR_EOF | AUTOSOURCARR_RESOLVER_FACT_REAPED;
            if (!t.write_complete || !WIFEXITED(result->wait_status) || WEXITSTATUS(result->wait_status) != 0
                || (result->facts & required) != required
                || autosourcarr_startup_parse(result->frame, result->frame_length, result->nonce, &requested) != 0) {
                result->diagnostic = AUTOSOURCARR_RESOLVER_DIAG_PROTOCOL; break;
            }
            result->status = AUTOSOURCARR_RESOLVER_SUCCESS; goto finished;
        }
        wait_activity(&t);
    }
failed:
    t.cleaning = 1;
    if (result->diagnostic == AUTOSOURCARR_RESOLVER_DIAG_CANCEL) result->status = AUTOSOURCARR_RESOLVER_CANCELLED;
    else if (t.child > 0) result->status = AUTOSOURCARR_RESOLVER_FAILED;
    /* A terminal failure cannot keep bootstrap authority while waiting on an
     * unresponsive kernel child. This never grants missing capabilities. */
    if (drop_parent(result) != 0) { result->status = AUTOSOURCARR_RESOLVER_UNRESOLVED; diagnose(&t, AUTOSOURCARR_RESOLVER_DIAG_AUTHORITY); }
    if (t.child > 0 && !t.reaped && reconcile(&t) != 0) result->status = AUTOSOURCARR_RESOLVER_UNRESOLVED;
    explicit_bzero(result->frame, sizeof(result->frame)); result->frame_length = 0; explicit_bzero(result->nonce, sizeof(result->nonce));
finished:
    close_all(&t); environment_free(&environment);
    explicit_bzero(t.challenge_bytes, sizeof(t.challenge_bytes)); explicit_bzero(t.error_bytes, sizeof(t.error_bytes));
    if (signal_masked) {
        if (!prior_sigpipe) {
            struct timespec zero = {0, 0};
            while (sigtimedwait(&blocked, NULL, &zero) == SIGPIPE) { }
        }
        if (sigprocmask(SIG_SETMASK, &previous, NULL) != 0) {
            t.cleaning = 1;
            result->status = AUTOSOURCARR_RESOLVER_UNRESOLVED; diagnose(&t, AUTOSOURCARR_RESOLVER_DIAG_SIGNAL);
            result->cleanup_errors |= AUTOSOURCARR_RESOLVER_CLEANUP_MASK;
            explicit_bzero(result->frame, sizeof(result->frame)); result->frame_length = 0; explicit_bzero(result->nonce, sizeof(result->nonce));
            (void)drop_parent(result);
        }
    }
    return result->status;
}

int autosourcarr_startup_resolver_request(const struct autosourcarr_startup_resolver_result *result,
    struct autosourcarr_startup_request *request)
{
    uint32_t required = AUTOSOURCARR_RESOLVER_FACT_CHILD_VERIFIED | AUTOSOURCARR_RESOLVER_FACT_EXEC_CONFIRMED
        | AUTOSOURCARR_RESOLVER_FACT_FRAME_EOF | AUTOSOURCARR_RESOLVER_FACT_STDERR_EOF
        | AUTOSOURCARR_RESOLVER_FACT_ERROR_EOF | AUTOSOURCARR_RESOLVER_FACT_REAPED;
    if (result == NULL || result->status != AUTOSOURCARR_RESOLVER_SUCCESS || result->frame_length == 0
        || (result->facts & required) != required || result->cleanup_errors != 0 || result->child_pid <= 0
        || !WIFEXITED(result->wait_status) || WEXITSTATUS(result->wait_status) != 0) { errno = EINVAL; return -1; }
    return autosourcarr_startup_parse(result->frame, result->frame_length, result->nonce, request);
}
