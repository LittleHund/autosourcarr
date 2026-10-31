#define _GNU_SOURCE
#include "bootstrap-binding.h"
#include "process-identity.h"
#include "source-root.h"
#include "startup-resolver.h"
#include <elf.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <linux/magic.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/fsuid.h>
#include <sys/prctl.h>
#include <sys/stat.h>
#include <sys/statfs.h>
#include <sys/syscall.h>
#include <sys/sysmacros.h>
#include <sys/wait.h>
#include <sys/xattr.h>
#include <time.h>
#include <unistd.h>

#if !defined(__linux__) || !defined(__x86_64__)
#error "The fixed native PID1 requires Linux x86_64."
#endif
_Static_assert(sizeof(pid_t) == 4 && sizeof(int) == 4, "Exact Linux PID/wait ABI required");

/* This binary must actually be statically linked and be the immutable OCI
 * ENTRYPOINT in its own PID namespace. Runtime checks corroborate that premise;
 * they cannot undo a loader hook or identify arbitrary host/container boundaries.
 * No CLR, shell or app configuration parser runs in this native authority. */
#define MAIN_ASSEMBLY "/app/AutoSourcarr.Api.dll"
#define BOOT_MS 30000U
#define TERMINAL_MS 20000U
#define REAP_WRITE_MS 5000U
#define ENV_MAXIMUM (2U * 1024U * 1024U)
#define ENV_ENTRIES 8192U
#define ENV_ENTRY_MAXIMUM 131072U
#define MAIN_FACT_BYTES 28U
#define MAIN_ERROR_BYTES 16U
#define STARTUP_CLEANUP_MS 10000U

enum pipe_kind { CONTROL, RESPONSE, PROOF, BOOT, REAP, LAUNCH, PIPE_COUNT };
enum main_stage { MAIN_DROP = 1, MAIN_PARENT, MAIN_ASSETS, MAIN_DESCRIPTORS, MAIN_EXEC };
struct environment { char **items; size_t count, bytes; };
struct entry {
    int pipes[PIPE_COUNT][2], cwd;
    struct stat cwd_status;
    uint32_t original_uid, original_gid;
    struct autosourcarr_source_root root;
    struct autosourcarr_source_observation_root observation;
    struct autosourcarr_startup_resolver_result resolver;
    struct autosourcarr_source_owner_config owner_config;
    struct autosourcarr_boot_binding binding;
    struct environment environment;
    pid_t main_pid, owner_pid;
    int main_reaped, owner_reaped, main_lost, owner_lost;
    int main_status, owner_status, no_children, other_children;
    int failed, terminal, term_sent, boot_complete, launch_eof, launch_valid;
    uint64_t boot_deadline, terminal_deadline, reap_deadline;
    unsigned char launch[MAIN_FACT_BYTES + MAIN_ERROR_BYTES + 1U];
    size_t launch_length;
    unsigned char expected_proof[AUTOSOURCARR_SOURCE_PROOF_BYTES];
    unsigned char boot[AUTOSOURCARR_BOOT_MAXIMUM];
    size_t boot_length, boot_written;
    unsigned char reap[AUTOSOURCARR_OWNER_REAP_BYTES];
    size_t reap_written;
    int reap_pending, reap_complete;
};
static volatile sig_atomic_t stop_requested;
extern char **environ;

static uint32_t get32(const unsigned char *p)
{ return (uint32_t)p[0] | (uint32_t)p[1] << 8 | (uint32_t)p[2] << 16 | (uint32_t)p[3] << 24; }
static void put32(unsigned char *p, uint32_t n)
{ for (size_t i = 0; i < 4; i++) p[i] = (unsigned char)(n >> (8U * i)); }
static void put64(unsigned char *p, uint64_t n)
{ for (size_t i = 0; i < 8; i++) p[i] = (unsigned char)(n >> (8U * i)); }
static int now_ms(uint64_t *now)
{
    struct timespec time;
    if (clock_gettime(CLOCK_MONOTONIC, &time) != 0 || time.tv_sec < 0
        || (uint64_t)time.tv_sec > UINT64_MAX / 1000U) return -1;
    *now = (uint64_t)time.tv_sec * 1000U + (uint64_t)time.tv_nsec / 1000000U;
    return 0;
}
static void close_owned(struct entry *e, int *fd)
{
    if (*fd < 0) return;
    int old = *fd; *fd = -1;
    /* Never retry close on Linux: a failed close must not target a reused FD. */
    if (close(old) != 0) e->failed = 1;
}
static void close_pipes(struct entry *e)
{ for (size_t i = 0; i < PIPE_COUNT; i++) for (size_t j = 0; j < 2; j++) close_owned(e, &e->pipes[i][j]); }
static void on_stop(int number) { (void)number; stop_requested = 1; }
static int signal_policy(int parent)
{
    sigset_t empty, actual; sigemptyset(&empty);
    struct sigaction action; memset(&action, 0, sizeof(action)); sigemptyset(&action.sa_mask);
    action.sa_handler = SIG_DFL;
    const int defaults[] = {SIGCHLD, SIGTERM, SIGINT, SIGHUP, SIGQUIT, SIGPIPE, SIGUSR1, SIGUSR2};
    for (size_t i = 0; i < sizeof(defaults) / sizeof(defaults[0]); i++)
        if (sigaction(defaults[i], &action, NULL) != 0) return -1;
    if (parent) {
        action.sa_handler = on_stop;
        const int stopped[] = {SIGTERM, SIGINT, SIGHUP, SIGQUIT};
        for (size_t i = 0; i < sizeof(stopped) / sizeof(stopped[0]); i++)
            if (sigaction(stopped[i], &action, NULL) != 0) return -1;
        action.sa_handler = SIG_IGN;
        if (sigaction(SIGPIPE, &action, NULL) != 0) return -1;
    }
    if (sigprocmask(SIG_SETMASK, &empty, NULL) != 0 || sigprocmask(SIG_SETMASK, NULL, &actual) != 0
        || sigismember(&actual, SIGCHLD) != 0 || sigismember(&actual, SIGTERM) != 0
        || sigaction(SIGCHLD, NULL, &action) != 0 || action.sa_handler != SIG_DFL
        || (action.sa_flags & SA_NOCLDWAIT) != 0) return -1;
    if (parent && (sigaction(SIGPIPE, NULL, &action) != 0 || action.sa_handler != SIG_IGN)) return -1;
    return 0;
}
static int immutable_status(int fd)
{
    struct stat status = {0};
    if (fstat(fd, &status) != 0 || !S_ISREG(status.st_mode) || status.st_nlink != 1 || status.st_uid != 0
        || (status.st_mode & (0022 | 06000)) != 0) return -1;
    errno = 0;
    if (fgetxattr(fd, "security.capability", NULL, 0) != -1 || errno != ENODATA) return -1;
    return 0;
}
static int own_namespace_and_static_entry(void)
{
    if (getpid() != 1 || getppid() != 0) return -1;
    int proc = open("/proc", O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (proc < 0) return -1;
    struct statfs filesystem;
    int valid = fstatfs(proc, &filesystem) == 0 && filesystem.f_type == PROC_SUPER_MAGIC;
    (void)close(proc);
    int own = open("/proc/self/ns/pid", O_RDONLY | O_CLOEXEC);
    int init = open("/proc/1/ns/pid", O_RDONLY | O_CLOEXEC);
    int children = open("/proc/self/ns/pid_for_children", O_RDONLY | O_CLOEXEC);
    struct stat a = {0}, b = {0}, c = {0};
    valid = valid && own >= 0 && init >= 0 && children >= 0
        && fstat(own, &a) == 0 && fstat(init, &b) == 0 && fstat(children, &c) == 0
        && fstatfs(own, &filesystem) == 0 && filesystem.f_type == NSFS_MAGIC
        && a.st_dev == b.st_dev && a.st_ino == b.st_ino && a.st_dev == c.st_dev && a.st_ino == c.st_ino;
    if (own >= 0) (void)close(own);
    if (init >= 0) (void)close(init);
    if (children >= 0) (void)close(children);
    int executable = open("/proc/self/exe", O_RDONLY | O_NONBLOCK | O_CLOEXEC);
    Elf64_Ehdr elf = {0}; struct stat status = {0};
    valid = valid && executable >= 0 && immutable_status(executable) == 0 && fstat(executable, &status) == 0
        && (status.st_mode & 0111) != 0 && status.st_size >= (off_t)sizeof(elf)
        && pread(executable, &elf, sizeof(elf), 0) == (ssize_t)sizeof(elf)
        && memcmp(elf.e_ident, ELFMAG, SELFMAG) == 0 && elf.e_ident[EI_CLASS] == ELFCLASS64
        && elf.e_ident[EI_DATA] == ELFDATA2LSB && elf.e_machine == EM_X86_64
        && elf.e_phentsize == sizeof(Elf64_Phdr) && elf.e_phnum != 0
        && elf.e_phoff <= (uint64_t)status.st_size
        && (uint64_t)elf.e_phnum <= ((uint64_t)status.st_size - elf.e_phoff) / sizeof(Elf64_Phdr);
    if (valid) for (size_t i = 0; i < elf.e_phnum; i++) {
        Elf64_Phdr program;
        if (pread(executable, &program, sizeof(program), (off_t)(elf.e_phoff + i * sizeof(program))) != (ssize_t)sizeof(program)
            || program.p_type == PT_INTERP) { valid = 0; break; }
    }
    if (executable >= 0) (void)close(executable);
    return valid ? 0 : -1;
}
static int null_device(void)
{
    int fd = open("/dev/null", O_RDWR | O_NOFOLLOW | O_CLOEXEC);
    struct stat status;
    if (fd >= 0 && fstat(fd, &status) == 0 && S_ISCHR(status.st_mode) && major(status.st_rdev) == 1 && minor(status.st_rdev) == 3) return fd;
    if (fd >= 0) (void)close(fd);
    return -1;
}
static int prepare_stdio(void)
{
    int null = null_device(); if (null < 0) return -1;
    int duplicate = fcntl(null, F_DUPFD_CLOEXEC, 32); (void)close(null);
    if (duplicate < 0) return -1;
    int valid = dup2(duplicate, 0) >= 0;
    for (int fd = 1; valid && fd < 3; fd++) {
        int flags = fcntl(fd, F_GETFL);
        if (flags < 0 && errno == EBADF) valid = dup2(duplicate, fd) >= 0;
        else if (flags < 0 || (flags & O_ACCMODE) == O_RDONLY) valid = 0;
    }
    if (close(duplicate) != 0) valid = 0;
    return valid ? 0 : -1;
}
static int prefix(const char *key, size_t length, const char *text)
{
    size_t size = strlen(text); if (length < size) return 0;
    for (size_t i = 0; i < size; i++) {
        unsigned char a = (unsigned char)key[i], b = (unsigned char)text[i];
        if (a >= 'A' && a <= 'Z') a = (unsigned char)(a + ('a' - 'A'));
        if (b >= 'A' && b <= 'Z') b = (unsigned char)(b + ('a' - 'A'));
        if (a != b) return 0;
    }
    return 1;
}
static int exact_key(const char *key, size_t length, const char *text)
{ return length == strlen(text) && prefix(key, length, text); }
static int loader_key(const char *key, size_t length)
{
    return prefix(key, length, "LD_") || prefix(key, length, "DOTNET_") || prefix(key, length, "COMPlus_")
        || prefix(key, length, "CORECLR_") || prefix(key, length, "COREHOST_") || prefix(key, length, "COR_")
        || prefix(key, length, "MONO_") || prefix(key, length, "MALLOC_")
        || exact_key(key, length, "GLIBC_TUNABLES") || exact_key(key, length, "GCONV_PATH")
        || exact_key(key, length, "LOCPATH") || exact_key(key, length, "NLSPATH") || exact_key(key, length, "LIBPATH")
        || exact_key(key, length, "SHLIB_PATH") || exact_key(key, length, "_RLD_LIST")
        || exact_key(key, length, "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES") || exact_key(key, length, "ASPNETCORE_HOSTINGSTARTUPEXCLUDEASSEMBLIES")
        || exact_key(key, length, "ASPNETCORE_STARTUPASSEMBLY") || exact_key(key, length, "ASPNETCORE_APPLICATIONNAME");
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
static int environment_add(struct environment *environment, const char *text, size_t length)
{
    if (environment->count >= ENV_ENTRIES + 8U || length + 1U > ENV_MAXIMUM - environment->bytes) return -1;
    char *copy = malloc(length + 1U); if (copy == NULL) return -1;
    memcpy(copy, text, length); copy[length] = 0;
    environment->items[environment->count++] = copy; environment->items[environment->count] = NULL;
    environment->bytes += length + 1U; return 0;
}
static int environment_snapshot(struct environment *environment)
{
    environment->items = calloc(ENV_ENTRIES + 9U, sizeof(char *)); if (environment->items == NULL) return -1;
    for (size_t i = 0; i <= ENV_ENTRIES; i++) {
        if (stop_requested) return -1;
        if (environ == NULL || environ[i] == NULL) {
            static const char *const fixed[] = {"DOTNET_ROOT=/usr/share/dotnet", "DOTNET_MULTILEVEL_LOOKUP=0", "DOTNET_EnableDiagnostics=0",
                "DOTNET_DiagnosticPorts=", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"};
            for (size_t j = 0; j < sizeof(fixed) / sizeof(fixed[0]); j++)
                if (environment_add(environment, fixed[j], strlen(fixed[j])) != 0) return -1;
            return 0;
        }
        if (i == ENV_ENTRIES) return -1;
        size_t length = strnlen(environ[i], ENV_ENTRY_MAXIMUM + 1U);
        const char *separator = memchr(environ[i], '=', length);
        if (length == 0 || length > ENV_ENTRY_MAXIMUM || separator == NULL || separator == environ[i]) return -1;
        if (!loader_key(environ[i], (size_t)(separator - environ[i])) && environment_add(environment, environ[i], length) != 0) return -1;
    }
    return -1;
}
static int discard_parent_environment(void)
{
    if (environ != NULL) for (size_t i = 0; i < ENV_ENTRIES && environ[i] != NULL; i++) {
        size_t length = strnlen(environ[i], ENV_ENTRY_MAXIMUM + 1U);
        if (length <= ENV_ENTRY_MAXIMUM) explicit_bzero(environ[i], length);
    }
    return clearenv();
}
static int fixed_directory(const char *path)
{
    char copy[PATH_MAX]; size_t size = strlen(path);
    if (path[0] != '/' || size >= sizeof(copy)) return -1;
    memcpy(copy, path + 1, size);
    int current = open("/", O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC); if (current < 0) return -1;
    struct stat status;
    if (fstat(current, &status) != 0 || status.st_uid != 0 || (status.st_mode & 0022) != 0) { (void)close(current); return -1; }
    char *save = NULL;
    for (char *part = strtok_r(copy, "/", &save); part != NULL; part = strtok_r(NULL, "/", &save)) {
        int next = openat(current, part, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        if (next < 0 || fstat(next, &status) != 0 || !S_ISDIR(status.st_mode) || status.st_nlink == 0
            || status.st_uid != 0 || (status.st_mode & 0022) != 0) {
            if (next >= 0) (void)close(next);
            (void)close(current); return -1;
        }
        (void)close(current); current = next;
    }
    return current;
}
static int fixed_file(int parent, const char *leaf, int elf)
{
    int fd = openat(parent, leaf, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC); if (fd < 0) return -1;
    unsigned char header[20]; struct stat status = {0};
    int valid = immutable_status(fd) == 0;
    if (elf) valid = valid && fstat(fd, &status) == 0 && (status.st_mode & 0111) != 0
        && pread(fd, header, sizeof(header), 0) == (ssize_t)sizeof(header)
        && memcmp(header, "\177ELF\002\001", 6) == 0 && header[18] == 62 && header[19] == 0;
    if (!valid) { (void)close(fd); return -1; }
    return fd;
}
/* Optional hardware only. Failure/absence/unsafe nodes never grant a group or
 * block cached application startup. This is not a KVM usability/self-test proof. */
static size_t hardware_group(uint32_t groups[1])
{
    int fd = open("/dev/kvm", O_PATH | O_NOFOLLOW | O_CLOEXEC); if (fd < 0) return 0;
    struct stat status = {0};
    int valid = fstat(fd, &status) == 0 && S_ISCHR(status.st_mode) && major(status.st_rdev) == 10
        && minor(status.st_rdev) == 232 && status.st_nlink != 0 && (uint32_t)status.st_gid != UINT32_MAX;
    if (close(fd) != 0) return 0;
    /* The owner permission class wins even when its group/other bits differ;
     * adding a group cannot repair an unusable owner-class permission. */
    if (!valid || status.st_uid == 568
        || (status.st_mode & 0006) == 0006 || status.st_gid == 568 || (status.st_mode & 0060) != 0060) return 0;
    groups[0] = (uint32_t)status.st_gid; return 1;
}
static _Noreturn void main_fail(int fd, enum main_stage stage)
{
    int error = errno; unsigned char record[MAIN_ERROR_BYTES];
    memcpy(record, "ASMCER1\n", 8); put32(record + 8, (uint32_t)stage); put32(record + 12, (uint32_t)(error > 0 ? error : EINVAL));
    (void)write(fd, record, sizeof(record)); _exit(78);
}
static _Noreturn void main_child(struct entry *e, int integrated)
{
    int error = e->pipes[LAUNCH][1]; uint32_t wanted[1] = {0}; size_t count = hardware_group(wanted);
    if (autosourcarr_drop_identity(568, 568, wanted, count, 1) != 0
        || (uint32_t)setfsuid((uid_t)-1) != 568 || (uint32_t)setfsgid((gid_t)-1) != 568) main_fail(error, MAIN_DROP);
    int death = 0;
    if (signal_policy(0) != 0 || setsid() < 0 || prctl(PR_SET_PDEATHSIG, SIGKILL, 0L, 0L, 0L) != 0
        || prctl(PR_GET_PDEATHSIG, &death, 0L, 0L, 0L) != 0 || death != SIGKILL || getppid() != 1) main_fail(error, MAIN_PARENT);
    struct stat cwd;
    if (fstatat(AT_FDCWD, ".", &cwd, AT_SYMLINK_NOFOLLOW) != 0 || cwd.st_dev != e->cwd_status.st_dev || cwd.st_ino != e->cwd_status.st_ino) main_fail(error, MAIN_ASSETS);
    int runtime = fixed_directory("/usr/share/dotnet"), app = fixed_directory("/app");
    if (runtime < 0 || app < 0) main_fail(error, MAIN_ASSETS);
    static const char *const required[] = {"AutoSourcarr.Api.dll", "AutoSourcarr.Api.deps.json", "AutoSourcarr.Api.runtimeconfig.json"};
    for (size_t i = 0; i < sizeof(required) / sizeof(required[0]); i++) {
        int file = fixed_file(app, required[i], 0); if (file < 0) main_fail(error, MAIN_ASSETS); (void)close(file);
    }
    int executable = fixed_file(runtime, "dotnet", 1); if (executable < 0) main_fail(error, MAIN_ASSETS);
    int sources[10] = {0, 1, 2, integrated ? e->pipes[CONTROL][1] : -1, integrated ? e->pipes[RESPONSE][0] : -1,
        integrated ? e->pipes[PROOF][0] : -1, e->pipes[BOOT][0], integrated ? e->pipes[REAP][0] : -1, executable, error};
    int kept[10];
    for (size_t i = 0; i < 10; i++) {
        kept[i] = sources[i] < 0 ? -1 : fcntl(sources[i], F_DUPFD_CLOEXEC, 64);
        if (sources[i] >= 0 && kept[i] < 0) main_fail(error, MAIN_DESCRIPTORS);
    }
    error = kept[9];
    for (int i = 0; i < 8; i++) if (kept[i] >= 0 && dup2(kept[i], i) < 0) main_fail(error, MAIN_DESCRIPTORS);
    if (dup3(kept[8], 8, O_CLOEXEC) < 0 || dup3(kept[9], 9, O_CLOEXEC) < 0) main_fail(error, MAIN_DESCRIPTORS);
    error = 9;
    if ((!integrated && (syscall(SYS_close_range, 3U, 5U, 0) != 0 || syscall(SYS_close_range, 7U, 7U, 0) != 0))
        || syscall(SYS_close_range, 10U, ~0U, 0) != 0) main_fail(error, MAIN_DESCRIPTORS);
    gid_t actual_group = 0; int actual_count = getgroups(0, NULL);
    if (actual_count < 0 || actual_count > 1 || (actual_count == 1 && getgroups(1, &actual_group) != 1)
        || (size_t)actual_count != count || (count == 1 && (uint32_t)actual_group != wanted[0])) main_fail(error, MAIN_DROP);
    unsigned char facts[MAIN_FACT_BYTES]; memcpy(facts, "ASMCFG1\n", 8);
    put32(facts + 8, (uint32_t)getpid()); put32(facts + 12, (uint32_t)getuid()); put32(facts + 16, (uint32_t)getgid());
    put32(facts + 20, (uint32_t)actual_count); put32(facts + 24, actual_count ? (uint32_t)actual_group : 0);
    if (write(error, facts, sizeof(facts)) != (ssize_t)sizeof(facts)) main_fail(error, MAIN_DESCRIPTORS);
    char *const arguments[] = {AUTOSOURCARR_RESOLVER_DOTNET, MAIN_ASSEMBLY, NULL};
    (void)syscall(SYS_execveat, 8, "", arguments, e->environment.items, AT_EMPTY_PATH);
    main_fail(error, MAIN_EXEC);
}
static _Noreturn void owner_child(struct entry *e)
{
    struct autosourcarr_source_owner_config config = e->owner_config;
    if (signal_policy(0) != 0) _exit(78);
    /* Parent stdin is already the verified null character device. */
    int high = fcntl(0, F_DUPFD_CLOEXEC, 64); if (high < 0) _exit(78);
    for (int fd = 0; fd < 3; fd++) if (dup2(high, fd) < 0) _exit(78);
    /* The source owner validates and owns all five descriptors. The exchange
     * capability is never preserved by the source JVM's child/exec path. */
    int keep[5] = {config.root_fd, config.control_read_fd, config.response_write_fd, config.proof_write_fd,
        config.observation_root_fd};
    for (size_t i = 0; i < 5; i++) for (size_t j = i + 1; j < 5; j++)
        if (keep[j] < keep[i]) { int temporary = keep[i]; keep[i] = keep[j]; keep[j] = temporary; }
    unsigned int first = 3;
    for (size_t i = 0; i < 5; i++) {
        if (keep[i] < 3 || (i != 0 && keep[i] == keep[i - 1])) _exit(78);
        unsigned int next = (unsigned int)keep[i];
        if (first < next && syscall(SYS_close_range, first, next - 1U, 0) != 0) _exit(78);
        first = next + 1U;
    }
    if (syscall(SYS_close_range, first, ~0U, 0) != 0) _exit(78);
    /* The owner receives only its fixed descriptor/identity binding. Remove the
     * main's copied account/configuration environment before its native loop;
     * owner_run separately scrubs original environ after its verified own drop. */
    environment_free(&e->environment); explicit_bzero(e, sizeof(*e));
    _exit(autosourcarr_source_owner_run(&config));
}
static int pipe_identity(int read_fd, int write_fd, struct autosourcarr_boot_pipe *identity)
{
    struct stat a, b; int read_flags = fcntl(read_fd, F_GETFL), write_flags = fcntl(write_fd, F_GETFL);
    if (read_flags < 0 || write_flags < 0 || (read_flags & O_ACCMODE) != O_RDONLY || (write_flags & O_ACCMODE) != O_WRONLY
        || fstat(read_fd, &a) != 0 || fstat(write_fd, &b) != 0 || !S_ISFIFO(a.st_mode) || !S_ISFIFO(b.st_mode)
        || a.st_ino == 0 || a.st_dev != b.st_dev || a.st_ino != b.st_ino) return -1;
    identity->device = (uint64_t)a.st_dev; identity->inode = (uint64_t)a.st_ino; return 0;
}
static int prepare_pipes(struct entry *e, int integrated, int owner_phase)
{
    for (size_t i = 0; i < PIPE_COUNT; i++) {
        if (owner_phase ? (!integrated || i >= BOOT) : (i < BOOT || (!integrated && i == REAP))) continue;
        struct autosourcarr_boot_pipe identity;
        if (pipe2(e->pipes[i], O_CLOEXEC | O_NONBLOCK) != 0 || pipe_identity(e->pipes[i][0], e->pipes[i][1], &identity) != 0) return -1;
        for (size_t j = 0; j < i; j++) if (e->pipes[j][0] >= 0) {
            struct autosourcarr_boot_pipe previous;
            if (pipe_identity(e->pipes[j][0], e->pipes[j][1], &previous) != 0
                || (identity.device == previous.device && identity.inode == previous.inode)) return -1;
        }
        if (i < AUTOSOURCARR_BOOT_PIPE_COUNT) e->binding.pipes[i] = identity;
    }
    return 0;
}
static int nonce_bytes(const unsigned char hex[64], unsigned char nonce[32])
{
    for (size_t i = 0; i < 32; i++) {
        unsigned int n = 0;
        for (size_t j = 0; j < 2; j++) {
            unsigned char c = hex[i * 2 + j];
            unsigned int digit = c >= '0' && c <= '9' ? (unsigned int)(c - '0') : c >= 'a' && c <= 'f' ? (unsigned int)(c - 'a') + 10U : 16U;
            if (digit > 15) return -1;
            n = n * 16U + digit;
        }
        nonce[i] = (unsigned char)n;
    }
    return 0;
}
static void expected_owner_proof(struct entry *e)
{
    unsigned char *proof = e->expected_proof;
    memcpy(proof, "ASOPRF1\n", 8); memcpy(proof + 8, e->owner_config.boot_nonce, 32);
    put32(proof + 40, (uint32_t)e->owner_pid); put32(proof + 44, 1);
    put32(proof + 48, e->root.selected.uid); put32(proof + 52, e->root.selected.gid);
    put64(proof + 56, e->root.device); put64(proof + 64, e->root.inode); put32(proof + 72, 0);
    e->binding.expected_owner_proof = proof; e->binding.expected_owner_proof_length = sizeof(e->expected_proof);
}
static void terminal(struct entry *e, uint64_t now)
{
    if (!e->terminal) { e->terminal = 1; e->terminal_deadline = now + TERMINAL_MS; }
    if (!e->boot_complete) close_owned(e, &e->pipes[BOOT][1]);
    if (e->main_pid > 1 && !e->main_reaped && !e->main_lost && !e->term_sent) {
        e->term_sent = 1;
        if (kill(e->main_pid, SIGTERM) != 0 && errno != ESRCH) e->failed = 1;
    }
}
static void child_reaped(struct entry *e, pid_t child, int status, uint64_t now)
{
    if (child == e->main_pid && !e->main_reaped && !e->main_lost) { e->main_reaped = 1; e->main_status = status; }
    else if (child == e->owner_pid && !e->owner_reaped && !e->owner_lost) {
        e->owner_reaped = 1; e->owner_status = status;
        if (autosourcarr_owner_reap_encode(e->owner_config.boot_nonce, (uint32_t)child, (int32_t)status, e->reap) != 0) e->failed = 1;
        else { e->reap_pending = 1; e->reap_deadline = now + REAP_WRITE_MS; }
    } else { e->other_children = 1; e->failed = 1; }
    if (!WIFEXITED(status) || WEXITSTATUS(status) != 0) e->failed = 1;
    terminal(e, now);
}
static void wait_known(struct entry *e, pid_t child, int reaped, int *lost, uint64_t now)
{
    if (child <= 1 || reaped || *lost) return;
    int status; pid_t result = waitpid(child, &status, WNOHANG);
    if (result == child) child_reaped(e, result, status, now);
    else if (result < 0 && errno != EINTR) { *lost = 1; e->failed = 1; terminal(e, now); }
}
static void reap_children(struct entry *e, uint64_t now)
{
    wait_known(e, e->main_pid, e->main_reaped, &e->main_lost, now);
    wait_known(e, e->owner_pid, e->owner_reaped, &e->owner_lost, now);
    e->no_children = 0;
    for (size_t i = 0; i < 256; i++) {
        int status; pid_t child = waitpid(-1, &status, WNOHANG);
        if (child > 0) { child_reaped(e, child, status, now); continue; }
        if (child == 0 || (child < 0 && errno == EINTR)) return;
        if (errno == ECHILD) e->no_children = 1;
        else { e->failed = 1; terminal(e, now); }
        return;
    }
}
static void inspect_launch(struct entry *e)
{
    if (e->pipes[LAUNCH][0] < 0) return;
    for (;;) {
        ssize_t got = read(e->pipes[LAUNCH][0], e->launch + e->launch_length, sizeof(e->launch) - e->launch_length);
        if (got > 0) {
            e->launch_length += (size_t)got;
            if (e->launch_length > MAIN_FACT_BYTES) { e->failed = 1; return; }
            continue;
        }
        if (got == 0) { e->launch_eof = 1; close_owned(e, &e->pipes[LAUNCH][0]); break; }
        if (errno == EINTR) continue;
        if (errno != EAGAIN && errno != EWOULDBLOCK) e->failed = 1;
        return;
    }
    if (e->launch_length != MAIN_FACT_BYTES || memcmp(e->launch, "ASMCFG1\n", 8) != 0
        || get32(e->launch + 8) != (uint32_t)e->main_pid || get32(e->launch + 12) != 568 || get32(e->launch + 16) != 568
        || get32(e->launch + 20) > 1 || get32(e->launch + 24) == UINT32_MAX
        || (get32(e->launch + 20) == 0 && get32(e->launch + 24) != 0)) { e->failed = 1; return; }
    e->binding.group_count = get32(e->launch + 20); e->binding.kvm_gid = get32(e->launch + 24);
    e->launch_valid = 1;
}
static void boot_write(struct entry *e)
{
    if (stop_requested || e->failed || e->terminal || !e->launch_valid || !e->launch_eof || e->boot_complete || e->pipes[BOOT][1] < 0) return;
    if (e->boot_length == 0 && autosourcarr_boot_binding_encode(&e->binding, e->boot, sizeof(e->boot), &e->boot_length) != 0) { e->failed = 1; return; }
    ssize_t sent = write(e->pipes[BOOT][1], e->boot + e->boot_written, e->boot_length - e->boot_written);
    if (sent > 0) e->boot_written += (size_t)sent;
    else if (sent < 0 && errno != EINTR && errno != EAGAIN && errno != EWOULDBLOCK) e->failed = 1;
    else if (sent == 0) e->failed = 1;
    if (e->boot_written == e->boot_length) {
        close_owned(e, &e->pipes[BOOT][1]); e->boot_complete = 1;
        explicit_bzero(e->boot, sizeof(e->boot)); explicit_bzero(e->resolver.frame, sizeof(e->resolver.frame));
        explicit_bzero(e->resolver.nonce, sizeof(e->resolver.nonce)); explicit_bzero(e->binding.nonce, sizeof(e->binding.nonce));
    }
}
static void reap_write(struct entry *e, uint64_t now)
{
    if (!e->reap_pending || e->pipes[REAP][1] < 0) return;
    if (now >= e->reap_deadline) { e->failed = 1; close_owned(e, &e->pipes[REAP][1]); return; }
    ssize_t sent = write(e->pipes[REAP][1], e->reap + e->reap_written, sizeof(e->reap) - e->reap_written);
    if (sent > 0) e->reap_written += (size_t)sent;
    else if (sent < 0 && errno != EINTR && errno != EAGAIN && errno != EWOULDBLOCK) { e->failed = 1; close_owned(e, &e->pipes[REAP][1]); }
    else if (sent == 0) { e->failed = 1; close_owned(e, &e->pipes[REAP][1]); }
    if (e->reap_written == sizeof(e->reap)) { close_owned(e, &e->pipes[REAP][1]); e->reap_complete = 1; }
}
static int monitor(struct entry *e)
{
    for (;;) {
        uint64_t now; if (now_ms(&now) != 0) return 78;
        if (e->terminal && now >= e->terminal_deadline) return 78;
        reap_children(e, now);
        if (stop_requested || e->failed) terminal(e, now);
        if (!e->terminal) {
            if (!e->boot_complete && now >= e->boot_deadline) e->failed = 1;
            else { inspect_launch(e); boot_write(e); }
            if (e->failed) terminal(e, now);
        }
        reap_write(e, now);
        if ((e->main_pid == 0 || e->main_reaped) && (e->owner_pid == 0 || e->owner_reaped) && e->no_children
            && (!e->reap_pending || e->reap_complete || e->pipes[REAP][1] < 0)) {
            return !e->failed && e->boot_complete && e->launch_valid && !e->other_children
                && WIFEXITED(e->main_status) && WEXITSTATUS(e->main_status) == 0
                && (e->owner_pid == 0 || (WIFEXITED(e->owner_status) && WEXITSTATUS(e->owner_status) == 0 && e->reap_complete)) ? 0 : 78;
        }
        if (now_ms(&now) != 0) return 78;
        uint64_t next = now + 50U;
        if (e->terminal && e->terminal_deadline < next) next = e->terminal_deadline;
        else if (!e->terminal && !e->boot_complete && e->boot_deadline < next) next = e->boot_deadline;
        if (e->reap_pending && !e->reap_complete && e->pipes[REAP][1] >= 0 && e->reap_deadline < next) next = e->reap_deadline;
        int wait = next <= now ? 0 : (int)(next - now);
        struct pollfd waits[3] = {
            {.fd = e->terminal ? -1 : e->pipes[LAUNCH][0], .events = POLLIN},
            {.fd = !e->terminal && e->launch_valid && !e->boot_complete ? e->pipes[BOOT][1] : -1, .events = POLLOUT},
            {.fd = e->reap_pending && !e->reap_complete ? e->pipes[REAP][1] : -1, .events = POLLOUT}
        };
        if (poll(waits, 3, wait) < 0 && errno != EINTR) { e->failed = 1; terminal(e, now); }
    }
}
static _Noreturn void finish(struct entry *e, int status)
{
    close_pipes(e); close_owned(e, &e->root.descriptor); close_owned(e, &e->observation.descriptor); close_owned(e, &e->cwd);
    environment_free(&e->environment); if (discard_parent_environment() != 0) e->failed = 1;
    if (e->failed) status = 78;
    explicit_bzero(e, sizeof(*e));
    _exit(status);
}
static _Noreturn void failure(struct entry *e)
{
    e->failed = 1;
    /* Closing the only temporary parent command writer is mandatory even when
     * the final-main fork failed. No different-UID source signal is assumed. */
    for (size_t i = 0; i < PIPE_COUNT; i++) for (size_t j = 0; j < 2; j++)
        if (!(i == REAP && j == 1 && e->main_pid > 1 && e->owner_pid > 1)) close_owned(e, &e->pipes[i][j]);
    close_owned(e, &e->root.descriptor); close_owned(e, &e->observation.descriptor); close_owned(e, &e->cwd);
    environment_free(&e->environment); if (discard_parent_environment() != 0) e->failed = 1;
    if (autosourcarr_drop_identity(568, 568, NULL, 0, 1) != 0) finish(e, 78);
    if (e->main_pid > 1 || e->owner_pid > 1) {
        uint64_t now; if (now_ms(&now) == 0) { terminal(e, now); (void)monitor(e); }
    }
    finish(e, 78);
}

static _Noreturn void startup_cleanup_child(const struct entry *e,int output)
{
    /* No source account or extra CAP_KILL is required. Main568 can enumerate/
     * unlink the source-owned0770 directory via its primaryGID568 and bind0600
     * file metadata with O_PATH. It never reads source bytes. */
    if (autosourcarr_drop_identity(568,568,NULL,0,1)!=0 || signal_policy(0)!=0
        || prctl(PR_SET_PDEATHSIG,SIGKILL,0L,0L,0L)!=0 || getppid()!=1) _exit(125);
    int keep[2];keep[0]=fcntl(e->observation.descriptor,F_DUPFD_CLOEXEC,20);
    keep[1]=fcntl(output,F_DUPFD_CLOEXEC,20);
    if (keep[0]<0 || keep[1]<0 || dup2(STDIN_FILENO,STDOUT_FILENO)<0 || dup2(STDIN_FILENO,STDERR_FILENO)<0
        || dup3(keep[0],3,O_CLOEXEC)<0 || dup3(keep[1],4,O_CLOEXEC)<0
        || syscall(SYS_close_range,5U,~0U,0)!=0) _exit(125);
    struct autosourcarr_source_startup_cleanup_config config={.exchange_fd=3,.expected_parent=1,
        .source_uid=e->root.selected.uid,.source_gid=e->root.selected.gid,
        .exchange_device=e->observation.device,.exchange_inode=e->observation.inode,
        .ancestor_device=e->observation.ancestor_device,.ancestor_inode=e->observation.ancestor_inode};
    struct autosourcarr_source_startup_cleanup_result result;
    if (autosourcarr_source_observation_startup_cleanup(&config,&result)!=0) {
        memset(&result,0,sizeof(result));result.version=1;result.error=(uint32_t)(errno?errno:EIO);
    }
    ssize_t sent;do {sent=write(4,&result,sizeof(result));} while (sent<0 && errno==EINTR);
    int valid=sent==(ssize_t)sizeof(result);explicit_bzero(&result,sizeof(result));
    if (close(3)!=0) valid=0;if (close(4)!=0) valid=0;_exit(valid?0:125);
}
static int startup_cleanup_signal(pid_t child,int handle)
{
    /* Only this single native thread can reap the exact newly created child.
     * pidfd is preferred; before it is available the unreaped direct PID is
     * still reserved. Parent realUID568 matches the dropped child's realUID. */
    if (handle>=0) return syscall(SYS_pidfd_send_signal,handle,SIGKILL,NULL,0U)==0 || errno==ESRCH ? 0:-1;
    siginfo_t info;memset(&info,0,sizeof(info));
    if (waitid(P_PID,(id_t)child,&info,WEXITED|WNOHANG|WNOWAIT)!=0) return -1;
    if (info.si_pid!=0) return 0;
    return kill(child,SIGKILL)==0 || errno==ESRCH ? 0:-1;
}
static int startup_cleanup(struct entry *e)
{
    if (getpid()!=1 || getppid()!=0 || getuid()!=568 || e->main_pid!=0 || e->owner_pid!=0 || stop_requested) return -1;
    int unused_status;errno=0;
    if (waitpid(-1,&unused_status,WNOHANG)!=-1 || errno!=ECHILD) return -1;
    uint64_t now;if (now_ms(&now)!=0 || now>UINT64_MAX-STARTUP_CLEANUP_MS-TERMINAL_MS) return -1;
    int pipes[2]={-1,-1},handle=-1,result=-1;pid_t child=0;
    if (pipe2(pipes,O_CLOEXEC)!=0) goto done;
    int flags=fcntl(pipes[0],F_GETFL);
    if (flags<0 || fcntl(pipes[0],F_SETFL,flags|O_NONBLOCK)!=0) goto done;
    child=fork();if (child<0) {child=0;goto done;}
    if (child==0) startup_cleanup_child(e,pipes[1]);
    close_owned(e,&pipes[1]);
    handle=(int)syscall(SYS_pidfd_open,child,0U);
    int failed=e->failed || handle<0,reaped=0,eof=0,status=0,killed=0;
    uint64_t deadline=now+STARTUP_CLEANUP_MS,terminal=deadline+TERMINAL_MS;
    unsigned char bytes[sizeof(struct autosourcarr_source_startup_cleanup_result)+1U];size_t used=0;
    memset(bytes,0,sizeof(bytes));
    for (;;) {
        for (size_t round=0;!eof && round<4;round++) {
            unsigned char discard[64];void *target=used<sizeof(bytes)?bytes+used:discard;
            size_t room=used<sizeof(bytes)?sizeof(bytes)-used:sizeof(discard);
            ssize_t count=read(pipes[0],target,room);
            if (count>0) {if(used<sizeof(bytes))used+=(size_t)count;else failed=1;if(used>sizeof(struct autosourcarr_source_startup_cleanup_result))failed=1;continue;}
            if (count==0) {eof=1;break;}
            if (errno==EINTR) {round--;continue;}
            if (errno!=EAGAIN && errno!=EWOULDBLOCK) failed=1;
            break;
        }
        if (!reaped) {
            pid_t waited=waitpid(child,&status,WNOHANG);
            if (waited==child) reaped=1;
            else if (waited<0 && errno!=EINTR) {failed=1;break;}
        }
        if (now_ms(&now)!=0) {failed=1;if(!reaped)(void)startup_cleanup_signal(child,handle);break;}
        if (stop_requested || now>=deadline) failed=1;
        if (failed && !reaped && !killed) {if(startup_cleanup_signal(child,handle)!=0)break;killed=1;}
        if (reaped && eof) break;
        if (now>=terminal) {failed=1;break;}
        struct pollfd wait={.fd=pipes[0],.events=POLLIN};
        if (poll(&wait,1,50)<0 && errno!=EINTR) failed=1;
    }
    if (!failed && reaped && eof && used==sizeof(struct autosourcarr_source_startup_cleanup_result) && WIFEXITED(status) && WEXITSTATUS(status)==0) {
        struct autosourcarr_source_startup_cleanup_result report;memcpy(&report,bytes,sizeof(report));
        if (report.version==1 && report.error==0 && report.complete==1 && report.reserved==0
            && report.scanned<=AUTOSOURCARR_SOURCE_STARTUP_TICKETS_MAXIMUM && report.removed==report.scanned
            && report.exchange_device==e->observation.device && report.exchange_inode==e->observation.inode) {
            errno=0;if(waitpid(-1,&unused_status,WNOHANG)==-1 && errno==ECHILD) result=0;
        }
        explicit_bzero(&report,sizeof(report));
    }
    explicit_bzero(bytes,sizeof(bytes));
done:
    close_owned(e,&pipes[0]);close_owned(e,&pipes[1]);close_owned(e,&handle);
    /* A failed/unreaped child never admits Main/source. The caller's PID1 failure
     * path exits the namespace, whose kernel teardown kills remaining children. */
    return e->failed?-1:result;
}

int main(void)
{
    struct entry e; memset(&e, 0, sizeof(e)); e.cwd = -1; e.root.descriptor = -1; e.observation.descriptor = -1;
    for (size_t i = 0; i < PIPE_COUNT; i++) e.pipes[i][0] = e.pipes[i][1] = -1;
    uid_t real, effective, saved; gid_t gid, effective_gid, saved_gid;
    if (getresuid(&real, &effective, &saved) != 0 || getresgid(&gid, &effective_gid, &saved_gid) != 0
        || (uint32_t)real == UINT32_MAX || (uint32_t)gid == UINT32_MAX) failure(&e);
    e.original_uid = (uint32_t)real; e.original_gid = (uint32_t)gid;
    if (own_namespace_and_static_entry() != 0 || syscall(SYS_close_range, 3U, ~0U, 0) != 0
        || prepare_stdio() != 0 || signal_policy(1) != 0) failure(&e);
    int status; errno = 0;
    if (waitpid(-1, &status, WNOHANG) != -1 || errno != ECHILD) failure(&e);
    e.cwd = open(".", O_PATH | O_DIRECTORY | O_CLOEXEC);
    if (e.cwd < 0 || fstat(e.cwd, &e.cwd_status) != 0 || !S_ISDIR(e.cwd_status.st_mode) || environment_snapshot(&e.environment) != 0) failure(&e);
    if (real == 0 && effective == 0 && saved == 0) {
        if (setresuid(568, 0, 0) != 0) failure(&e);
    } else if (real != 568 || effective != 568 || saved != 568 || gid != effective_gid || gid != saved_gid) failure(&e);
    if (autosourcarr_startup_resolve((const char *const *)e.environment.items, &stop_requested, &e.resolver) != AUTOSOURCARR_RESOLVER_SUCCESS) {
        /* Resolver owns its exact child/reap/EOF and terminal failure drop. Its
         * non-success contract forbids any further bootstrap/reconcile attempt. */
        finish(&e, 78);
    }
    struct autosourcarr_startup_request request;
    if (stop_requested || autosourcarr_startup_resolver_request(&e.resolver, &request) != 0) failure(&e);
    int integrated = request.integrated;
    if (integrated && autosourcarr_source_root_prepare(&request, e.original_uid, e.original_gid, &e.root) != AUTOSOURCARR_SOURCE_ROOT_OK) failure(&e);
    if (integrated && autosourcarr_source_observation_root_prepare(e.original_uid, e.original_gid, &e.root, &e.observation) != AUTOSOURCARR_SOURCE_ROOT_OK) failure(&e);
    if (integrated && startup_cleanup(&e)!=0) failure(&e);
    if (integrated && prepare_pipes(&e, integrated, 1) != 0) failure(&e);
    e.binding.original_uid = e.original_uid; e.binding.original_gid = e.original_gid;
    memcpy(e.binding.nonce, e.resolver.nonce, sizeof(e.binding.nonce));
    e.binding.startup = e.resolver.frame; e.binding.startup_length = e.resolver.frame_length;
    if (integrated) {
        e.owner_config.root_fd = e.root.descriptor; e.owner_config.control_read_fd = e.pipes[CONTROL][0];
        e.owner_config.response_write_fd = e.pipes[RESPONSE][1]; e.owner_config.proof_write_fd = e.pipes[PROOF][1];
        e.owner_config.expected_parent = 1; e.owner_config.uid = e.root.selected.uid; e.owner_config.gid = e.root.selected.gid;
        e.owner_config.root_device = e.root.device; e.owner_config.root_inode = e.root.inode;
        /* Internal immutable binding only; not a caller path or wire command. */
        e.owner_config.observation_root_fd = e.observation.descriptor;
        e.owner_config.observation_root_device = e.observation.device;
        e.owner_config.observation_root_inode = e.observation.inode;
        if (nonce_bytes(e.resolver.nonce, e.owner_config.boot_nonce) != 0) failure(&e);
        e.owner_pid = fork();
        if (e.owner_pid < 0) { e.owner_pid = 0; failure(&e); }
        if (e.owner_pid == 0) owner_child(&e);
        expected_owner_proof(&e);
        /* The owner has its inherited copies. Close both native-only root
         * capabilities before Main is forked, so Main never inherits them. */
        close_owned(&e, &e.root.descriptor); close_owned(&e, &e.observation.descriptor);
        if (e.failed) failure(&e);
    }
    /* The owner never inherits boot/reap/launch endpoints: only native PID1 can
     * publish boot/reap, and main launch CLOEXEC EOF cannot be held by a sibling. */
    if (prepare_pipes(&e, integrated, 0) != 0) failure(&e);
    uint64_t now; if (now_ms(&now) != 0 || stop_requested) failure(&e);
    e.boot_deadline = now + BOOT_MS;
    e.main_pid = fork();
    if (e.main_pid < 0) { e.main_pid = 0; failure(&e); }
    if (e.main_pid == 0) main_child(&e, integrated);
    e.binding.main_pid = (uint32_t)e.main_pid;
    /* Only the main child retains the owner-command writer. No root descriptor
     * or managed/source environment remains in the long-lived native parent. */
    for (size_t i = 0; i < PIPE_COUNT; i++) for (size_t j = 0; j < 2; j++)
        if (!((i == BOOT || i == REAP) && j == 1) && !(i == LAUNCH && j == 0)) close_owned(&e, &e.pipes[i][j]);
    close_owned(&e, &e.root.descriptor); close_owned(&e, &e.observation.descriptor); close_owned(&e, &e.cwd);
    environment_free(&e.environment); if (discard_parent_environment() != 0) e.failed = 1;
    if (autosourcarr_drop_identity(568, 568, NULL, 0, 1) != 0) finish(&e, 78);
    if ((uint32_t)setfsuid((uid_t)-1) != 568 || (uint32_t)setfsgid((gid_t)-1) != 568) failure(&e);
    int result = monitor(&e);
    finish(&e, result);
}
