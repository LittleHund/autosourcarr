#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <grp.h>
#include <limits.h>
#include <linux/capability.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <unistd.h>

/*
 * Source host sandbox (phase 13, SRC-5): starts the source host JVM (and its JCEF WebView) under Landlock.
 *
 *   source-host-sandbox [--uid U --gid G] --data DIR [--rw PATH]... [--ro PATH]... -- PROGRAM [ARGS...]
 *
 * Needs no capability and no host permission. Unlike repair-runner it never refuses: normal source browsing must work,
 * so a kernel without Landlock degrades to "unsandboxed" and says so in its first stderr line, which the app parses
 * and reports in health:
 *
 *   AUTOSOURCARR_SOURCE_SANDBOX_V1 {"uid":N,"separated":0|1,"landlock":ABI,"scoped":0|2,"nnp":1,"reason":"..."}
 *
 * Filesystem: read+execute /usr /lib /lib64 /bin /sbin /opt, read-only /etc /proc /sys and the --ro paths,
 * read-write the data folder, the --rw paths and a few devices (/dev/null, /dev/zero, /dev/random, /dev/urandom,
 * /dev/shm for Chromium). Everything else (the app's /config, /data, /app, the user's library) is unreachable.
 * Signals: Landlock ABI 6 signal scoping, so code in the JVM cannot signal the app. Abstract unix sockets are NOT
 * scoped: Xlib reaches the app's Xvfb through one. Network: not restricted here (sources browse the web directly).
 *
 * Separate user (optional): with --uid/--gid the sandbox tries to become that user. That needs CAP_SETUID/CAP_SETGID
 * (and CAP_KILL for the supervisor) as file capabilities on this binary, which only work in a container without
 * no-new-privileges and with those capabilities in its bounding set. When the switch is refused the host runs as the
 * app's user ("separated":0, reason "identity-unavailable"), still Landlock-sandboxed. When it succeeds, this process
 * stays as a small supervisor in the app's process group (the app can signal it; it cannot signal the other user):
 * it kills the host's process group when it is told to stop or when the host exits, and exits with the host's status.
 */

#define PROOF "AUTOSOURCARR_SOURCE_SANDBOX_V1"
#define MAX_PATHS 32

struct ll_ruleset { uint64_t fs, net, scoped; };
struct ll_path { uint64_t access; int32_t parent_fd; } __attribute__((packed));
#define LL_CREATE 444
#define LL_ADD 445
#define LL_RESTRICT 446
#define LL_RULE_PATH 1
#define LL_EXEC (1ULL << 0)
#define LL_WRITE (1ULL << 1)
#define LL_READ (1ULL << 2)
#define LL_DIR (1ULL << 3)
#define LL_TRUNCATE (1ULL << 14)
#define LL_IOCTL_DEV (1ULL << 15)
#define LL_FILE_RIGHTS (LL_EXEC | LL_WRITE | LL_READ | LL_TRUNCATE | LL_IOCTL_DEV)
#define LL_SCOPE_SIGNAL (1ULL << 1)

static uint64_t handled;
static pid_t host = -1;

static void usage(void)
{
    fprintf(stderr, "usage: source-host-sandbox [--uid U --gid G] --data DIR [--rw PATH]... [--ro PATH]... -- PROGRAM [ARGS...]\n");
    _exit(64);
}

static uint64_t fs_rights(int abi)
{
    uint64_t rights = (1ULL << 13) - 1; /* ABI 1: execute .. make_sym */
    if (abi >= 2) rights |= 1ULL << 13; /* refer */
    if (abi >= 3) rights |= LL_TRUNCATE;
    if (abi >= 5) rights |= LL_IOCTL_DEV;
    return rights;
}

/* 0 on success or an optional missing path; -1 when a rule could not be added. */
static int allow(int ruleset, const char *path, uint64_t access, int optional)
{
    int fd = open(path, O_PATH | O_CLOEXEC);
    if (fd < 0) return optional && errno == ENOENT ? 0 : -1;
    struct stat info;
    if (fstat(fd, &info) != 0) { close(fd); return -1; }
    if (!S_ISDIR(info.st_mode)) access &= LL_FILE_RIGHTS;
    struct ll_path rule = { .access = access & handled, .parent_fd = fd };
    int result = rule.access == 0 ? 0 : (int)syscall(LL_ADD, ruleset, LL_RULE_PATH, &rule, 0);
    close(fd);
    return result == 0 ? 0 : -1;
}

static int absolute(const char *path)
{
    return path && path[0] == '/' && strlen(path) < PATH_MAX && !strstr(path, "/../") && !strstr(path, "/./");
}

static int parse_id(const char *text, unsigned *value)
{
    char *end = NULL;
    errno = 0;
    unsigned long parsed = strtoul(text, &end, 10);
    if (errno || !end || *end || text[0] == '\0' || parsed == 0 || parsed > 0x7fffffffUL) return -1;
    *value = (unsigned)parsed;
    return 0;
}

static void stop_host(int signal_number)
{
    (void)signal_number;
    if (host > 1) { kill(-host, SIGKILL); kill(host, SIGKILL); }
    _exit(143);
}

/* Empty every capability set of this process (the separated host must hold none). */
static int clear_capabilities(void)
{
    struct __user_cap_header_struct header = { .version = _LINUX_CAPABILITY_VERSION_3, .pid = 0 };
    struct __user_cap_data_struct data[2];
    memset(data, 0, sizeof data);
    if (syscall(SYS_capset, &header, data) != 0) return -1;
    (void)prctl(PR_CAP_AMBIENT, PR_CAP_AMBIENT_CLEAR_ALL, 0, 0, 0);
    return 0;
}

/* Keep only CAP_KILL (effective and permitted) for the supervisor. */
static void keep_only_kill(void)
{
    struct __user_cap_header_struct header = { .version = _LINUX_CAPABILITY_VERSION_3, .pid = 0 };
    struct __user_cap_data_struct data[2];
    memset(data, 0, sizeof data);
    data[0].effective = data[0].permitted = 1U << CAP_KILL;
    if (syscall(SYS_capset, &header, data) != 0) (void)clear_capabilities();
}

int main(int argc, char **argv)
{
    const char *data = NULL, *rw[MAX_PATHS], *ro[MAX_PATHS];
    size_t rw_count = 0, ro_count = 0;
    unsigned uid = 0, gid = 0;
    int want_identity = 0, index = 1;
    for (; index < argc; ++index) {
        if (strcmp(argv[index], "--") == 0) { ++index; break; }
        if (index + 1 >= argc) usage();
        const char *value = argv[index + 1];
        if (strcmp(argv[index], "--data") == 0) data = value;
        else if (strcmp(argv[index], "--rw") == 0 && rw_count < MAX_PATHS) rw[rw_count++] = value;
        else if (strcmp(argv[index], "--ro") == 0 && ro_count < MAX_PATHS) ro[ro_count++] = value;
        else if (strcmp(argv[index], "--uid") == 0) { if (parse_id(value, &uid) != 0) usage(); want_identity = 1; }
        else if (strcmp(argv[index], "--gid") == 0) { if (parse_id(value, &gid) != 0) usage(); want_identity = 1; }
        else usage();
        ++index;
    }
    if (index >= argc || !absolute(data) || !absolute(argv[index]) || (want_identity && (uid == 0 || gid == 0))) usage();
    for (size_t i = 0; i < rw_count; ++i) if (!absolute(rw[i])) usage();
    for (size_t i = 0; i < ro_count; ++i) if (!absolute(ro[i])) usage();

    const char *reason = "";
    int separated = 0;
    if (want_identity && uid != geteuid()) {
        pid_t supervisor = getpid();
        int pipefd[2];
        if (pipe2(pipefd, O_CLOEXEC) != 0) { reason = "identity-unavailable"; goto sandbox; }
        pid_t child = fork();
        if (child < 0) { close(pipefd[0]); close(pipefd[1]); reason = "identity-unavailable"; goto sandbox; }
        if (child == 0) {
            close(pipefd[0]);
            /* The host's own process group, so the supervisor can stop the host and every helper it started. */
            if (setsid() < 0 || prctl(PR_SET_PDEATHSIG, SIGKILL, 0, 0, 0) != 0 || getppid() != supervisor) _exit(70);
            char ok = 0;
            if (setgroups(0, NULL) == 0 && setresgid(gid, gid, gid) == 0 && setresuid(uid, uid, uid) == 0
                && prctl(PR_SET_PDEATHSIG, SIGKILL, 0, 0, 0) == 0 && getppid() == supervisor && clear_capabilities() == 0)
                ok = 1;
            if (write(pipefd[1], &ok, 1) != 1) _exit(70);
            close(pipefd[1]);
            if (!ok) _exit(77); /* the supervisor goes on without a separate user */
            separated = 1;
            goto sandbox;
        }
        close(pipefd[1]);
        char ok = 0;
        ssize_t got = read(pipefd[0], &ok, 1);
        close(pipefd[0]);
        if (got == 1 && ok) {
            host = child;
            keep_only_kill();
            struct sigaction action;
            memset(&action, 0, sizeof action);
            action.sa_handler = stop_host;
            sigaction(SIGTERM, &action, NULL);
            sigaction(SIGINT, &action, NULL);
            sigaction(SIGHUP, &action, NULL);
            int status = 0;
            while (waitpid(child, &status, 0) < 0 && errno == EINTR) { }
            kill(-child, SIGKILL); /* helpers left in the host's group */
            if (WIFEXITED(status)) _exit(WEXITSTATUS(status));
            _exit(128 + (WIFSIGNALED(status) ? WTERMSIG(status) : 0));
        }
        int status = 0;
        while (waitpid(child, &status, 0) < 0 && errno == EINTR) { }
        reason = "identity-unavailable";
        (void)clear_capabilities(); /* nothing held is fine too */
    }

sandbox:
    if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0) { fprintf(stderr, "source-host-sandbox: no_new_privs failed\n"); _exit(71); }
    int abi = (int)syscall(LL_CREATE, NULL, 0, 1);
    uint64_t scoped = 0;
    if (abi >= 1) {
        handled = fs_rights(abi);
        scoped = abi >= 6 ? LL_SCOPE_SIGNAL : 0;
        struct ll_ruleset policy = { .fs = handled, .net = 0, .scoped = scoped };
        size_t size = abi >= 6 ? sizeof policy : abi >= 4 ? 2 * sizeof(uint64_t) : sizeof(uint64_t);
        int ruleset = (int)syscall(LL_CREATE, &policy, size, 0);
        int failed = ruleset < 0;
        const char *rx[] = { "/usr", "/lib", "/lib64", "/lib32", "/bin", "/sbin", "/opt", NULL };
        const char *readonly[] = { "/etc", "/proc", "/sys", NULL };
        const char *devices[] = { "/dev/null", "/dev/zero", "/dev/random", "/dev/urandom", "/dev/shm", "/dev/tty", NULL };
        for (size_t i = 0; !failed && rx[i]; ++i) failed = allow(ruleset, rx[i], LL_EXEC | LL_READ | LL_DIR, 1) != 0;
        for (size_t i = 0; !failed && readonly[i]; ++i) failed = allow(ruleset, readonly[i], LL_READ | LL_DIR, 1) != 0;
        for (size_t i = 0; !failed && devices[i]; ++i) failed = allow(ruleset, devices[i], handled, 1) != 0;
        for (size_t i = 0; !failed && i < ro_count; ++i) failed = allow(ruleset, ro[i], LL_READ | LL_DIR | LL_EXEC, 1) != 0;
        for (size_t i = 0; !failed && i < rw_count; ++i) failed = allow(ruleset, rw[i], handled, 1) != 0;
        if (!failed) failed = allow(ruleset, data, handled, 0) != 0;
        if (!failed) failed = syscall(LL_RESTRICT, ruleset, 0) != 0;
        if (ruleset >= 0) close(ruleset);
        if (failed) {
            /* A rule that cannot be added must not leave a half-built domain: run unsandboxed and say why. */
            fprintf(stderr, PROOF " {\"uid\":%u,\"separated\":%d,\"landlock\":0,\"scoped\":0,\"nnp\":1,\"reason\":\"landlock-rules-failed\"}\n",
                (unsigned)geteuid(), separated);
            goto run;
        }
    } else reason = reason[0] ? reason : "landlock-unavailable";
    fprintf(stderr, PROOF " {\"uid\":%u,\"separated\":%d,\"landlock\":%d,\"scoped\":%d,\"nnp\":1,\"reason\":\"%s\"}\n",
        (unsigned)geteuid(), separated, abi >= 1 ? abi : 0, (int)scoped, reason);
run:
    fflush(stderr);
    execv(argv[index], &argv[index]);
    fprintf(stderr, "source-host-sandbox: cannot start %s: %s\n", argv[index], strerror(errno));
    _exit(127);
}
