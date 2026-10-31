#define _GNU_SOURCE
#include <ctype.h>
#include <errno.h>
#include <fcntl.h>
#include <linux/audit.h>
#include <linux/capability.h>
#include <linux/filter.h>
#include <linux/seccomp.h>
#include <limits.h>
#include <signal.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/resource.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/xattr.h>
#include <unistd.h>

/* Stable Landlock ABI6 layout; no dependency on newer host UAPI headers. */
struct as_ruleset { uint64_t fs, net, scoped; };
struct as_path { uint64_t access; int32_t parent_fd; } __attribute__((packed));
#define AS_LL_CREATE 444
#define AS_LL_ADD 445
#define AS_LL_RESTRICT 446
#define AS_EXEC (1ULL << 0)
#define AS_WRITE (1ULL << 1)
#define AS_READ (1ULL << 2)
#define AS_DIR (1ULL << 3)
#define AS_REMOVE_FILE (1ULL << 5)
#define AS_TRUNCATE (1ULL << 14)
#define AS_IOCTL (1ULL << 15)
#define AS_FS_ALL ((1ULL << 16) - 1)
#define AS_QEMU "/usr/bin/qemu-system-x86_64"
#define AS_GUEST "/opt/autosourcarr-repair-guest"

static void fail(const char *stage)
{
    perror(stage);
    _exit(78);
}

static void allow_path(int ruleset, const char *path, uint64_t access, int optional)
{
    int fd = open(path, O_PATH | O_CLOEXEC);
    if (fd < 0) {
        if (optional && errno == ENOENT)
            return;
        fail("Cannot open fixed sandbox path");
    }
    struct stat info;
    if (fstat(fd, &info) != 0)
        fail("Cannot inspect fixed sandbox path");
    if (!S_ISDIR(info.st_mode))
        access &= ~(AS_DIR | AS_REMOVE_FILE);
    struct as_path rule = { .access = access, .parent_fd = fd };
    if (syscall(AS_LL_ADD, ruleset, 1, &rule, 0) != 0)
        fail("Cannot add sandbox path rule");
    close(fd);
}

#define AS_DENY(nr) \
    BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, (nr), 0, 1), \
    BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | EPERM)

static void restrict_syscalls(void)
{
#if !defined(__x86_64__)
#error Repair launcher currently requires Linux x86_64.
#endif
    const struct sock_filter instructions[] = {
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, arch)),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, AUDIT_ARCH_X86_64, 1, 0),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_KILL_PROCESS),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, nr)),
        BPF_JUMP(BPF_JMP | BPF_JSET | BPF_K, 0x40000000, 0, 1),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_KILL_PROCESS),
        AS_DENY(__NR_socket), AS_DENY(__NR_connect), AS_DENY(__NR_bind),
        AS_DENY(__NR_listen), AS_DENY(__NR_accept), AS_DENY(__NR_accept4),
        AS_DENY(__NR_ptrace), AS_DENY(__NR_process_vm_readv), AS_DENY(__NR_process_vm_writev),
        AS_DENY(__NR_pidfd_getfd), AS_DENY(__NR_kcmp),
        AS_DENY(__NR_io_uring_setup), AS_DENY(__NR_io_uring_enter), AS_DENY(__NR_io_uring_register),
        AS_DENY(__NR_bpf), AS_DENY(__NR_perf_event_open),
        AS_DENY(__NR_mount), AS_DENY(__NR_umount2), AS_DENY(__NR_pivot_root),
        AS_DENY(__NR_unshare), AS_DENY(__NR_setns),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ALLOW)
    };
    struct sock_fprog program = {
        .len = (unsigned short)(sizeof instructions / sizeof instructions[0]),
        .filter = (struct sock_filter *)instructions
    };
    if (prctl(PR_SET_SECCOMP, SECCOMP_MODE_FILTER, &program) != 0)
        fail("Cannot enforce repair syscall restrictions");
}

static int valid_hex(const char *value, size_t length)
{
    if (strlen(value) != length)
        return 0;
    for (size_t i = 0; i < length; ++i)
        if (!(value[i] >= '0' && value[i] <= '9') && !(value[i] >= 'a' && value[i] <= 'f'))
            return 0;
    return 1;
}

static pid_t expected_parent(const char *value)
{
    if (!value || value[0] < '1' || value[0] > '9' || strlen(value) > 10) {
        errno = EINVAL;
        fail("Invalid expected repair parent");
    }
    for (const char *cursor = value; *cursor; ++cursor) {
        if (*cursor < '0' || *cursor > '9') {
            errno = EINVAL;
            fail("Invalid expected repair parent");
        }
    }
    errno = 0;
    char *end = NULL;
    unsigned long parsed = strtoul(value, &end, 10);
    if (errno || !end || *end || parsed < 1 || parsed > INT_MAX) {
        errno = EINVAL;
        fail("Invalid expected repair parent");
    }
    return (pid_t)parsed;
}

static void verify_parent(pid_t expected)
{
    int signal_number = 0;
    if (getppid() != expected || prctl(PR_GET_PDEATHSIG, &signal_number, 0, 0, 0) != 0
        || signal_number != SIGKILL || getppid() != expected) {
        errno = ESRCH;
        fail("Repair parent lifetime guard is unavailable");
    }
}

static void arm_parent(pid_t expected)
{
    /* The caller keeps the creating OS thread alive until this child exits.
       An already-dead parent emits no later event: check around setup as well. */
    if (getppid() != expected) {
        errno = ESRCH;
        fail("Repair parent changed before lifetime setup");
    }
    if (prctl(PR_SET_PDEATHSIG, SIGKILL, 0, 0, 0) != 0)
        fail("Cannot bind repair child lifetime");
    verify_parent(expected);
}

static void verify_unprivileged_qemu(void)
{
    int fd = open(AS_QEMU, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
    if (fd < 0)
        fail("Cannot inspect the fixed repair executable");
    struct stat info;
    if (fstat(fd, &info) != 0 || !S_ISREG(info.st_mode) || info.st_uid != 0
        || info.st_nlink != 1 || (info.st_mode & (S_ISUID | S_ISGID | S_IWGRP | S_IWOTH))) {
        errno = EACCES;
        fail("Repair executable must not change privileges");
    }
    /* Privileged exec can clear PDEATHSIG even after a successful setup. */
    errno = 0;
    if (fgetxattr(fd, "security.capability", NULL, 0) >= 0
        || (errno != ENODATA && errno != ENOTSUP)) {
        errno = EACCES;
        fail("Repair executable capabilities are not permitted");
    }
    close(fd);
}

static void lower_limit(int resource, rlim_t requested)
{
    struct rlimit existing;
    if (getrlimit(resource, &existing) != 0)
        fail("Cannot inspect inherited repair ceiling");
    if (existing.rlim_cur != RLIM_INFINITY && requested > existing.rlim_cur)
        requested = existing.rlim_cur;
    if (existing.rlim_max != RLIM_INFINITY && requested > existing.rlim_max)
        requested = existing.rlim_max;
    struct rlimit bounded = { requested, requested };
    if (setrlimit(resource, &bounded) != 0)
        fail("Cannot preserve inherited repair ceiling");
}

int main(int argc, char **argv)
{
    if (argc < 6 || geteuid() == 0 || !valid_hex(argv[2], 64) || strcmp(argv[4], "--") != 0) {
        errno = EINVAL;
        fail("Invalid trusted repair launch request");
    }
    pid_t parent_pid = expected_parent(argv[3]);
    char *job = realpath(argv[1], NULL);
    if (!job || strcmp(job, argv[1]) != 0)
        fail("Repair workspace must be canonical");
    char *leaf = strrchr(job, '/');
    const char *parent = "/source-maintenance/vm-jobs/";
    if (!leaf || !valid_hex(leaf + 1, 32) || (size_t)(leaf + 1 - job) < strlen(parent)
        || strncmp(leaf + 1 - strlen(parent), parent, strlen(parent)) != 0) {
        errno = EINVAL;
        fail("Invalid repair workspace ownership scope");
    }
    struct stat info;
    if (lstat(job, &info) != 0 || !S_ISDIR(info.st_mode)
        || info.st_uid != geteuid() || (info.st_mode & 0077) != 0) {
        errno = EACCES;
        fail("Repair workspace must be private to the application user");
    }
    if (setpgid(0, 0) != 0)
        fail("Cannot establish repair process ownership");
    if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0)
        fail("Cannot restrict repair privileges");
    struct __user_cap_header_struct cap_header = {
        .version = _LINUX_CAPABILITY_VERSION_3, .pid = 0
    };
    struct __user_cap_data_struct cap_data[2] = {{0}, {0}};
    if (syscall(SYS_capset, &cap_header, cap_data) != 0)
        fail("Cannot clear repair capabilities");
    /* Credential drops precede setup because credential transitions can clear it. */
    verify_unprivileged_qemu();
    arm_parent(parent_pid);
    if (clearenv() != 0 || setenv("LANG", "C.UTF-8", 1) != 0
        || setenv("TZ", "UTC", 1) != 0 || setenv("HOME", "/nonexistent", 1) != 0
        || setenv("TMPDIR", job, 1) != 0)
        fail("Cannot isolate repair environment");
    int abi = (int)syscall(AS_LL_CREATE, NULL, 0, 1);
    if (abi < 6) {
        errno = EOPNOTSUPP;
        fail("Landlock ABI6 is required; no isolation fallback is enabled");
    }
    struct as_ruleset policy = { .fs = AS_FS_ALL, .net = 3, .scoped = 3 };
    int ruleset = (int)syscall(AS_LL_CREATE, &policy, sizeof policy, 0);
    if (ruleset < 0)
        fail("Cannot create repair restriction domain");
    const char *libraries[] = { "/lib", "/lib64", "/usr/lib", "/usr/lib64", NULL };
    for (size_t i = 0; libraries[i]; ++i)
        allow_path(ruleset, libraries[i], AS_READ | AS_DIR | AS_EXEC, 1);
    const char *readonly[] = {
        "/usr/share/qemu", "/usr/share/seabios", "/etc/ld.so.cache",
        "/proc/self", "/proc/cpuinfo", "/proc/meminfo", "/sys/devices/system/cpu", NULL
    };
    for (size_t i = 0; readonly[i]; ++i)
        allow_path(ruleset, readonly[i], AS_READ | AS_DIR, 1);
    allow_path(ruleset, AS_QEMU, AS_READ | AS_EXEC, 0);
    allow_path(ruleset, AS_GUEST, AS_READ | AS_DIR, 0);
    allow_path(ruleset, "/dev/kvm", AS_READ | AS_WRITE | AS_IOCTL, 0);
    allow_path(ruleset, "/dev/null", AS_READ | AS_WRITE, 0);
    allow_path(ruleset, "/dev/urandom", AS_READ, 0);
    allow_path(ruleset, job, AS_READ | AS_DIR | AS_WRITE | AS_TRUNCATE, 0);
    if (syscall(AS_LL_RESTRICT, ruleset, 0) != 0)
        fail("Cannot enter repair restriction domain");
    close(ruleset);
    if (syscall(__NR_close_range, 3U, ~0U, 0) != 0)
        fail("Cannot close unrelated inherited file descriptors");
    lower_limit(RLIMIT_AS, 8ULL << 30);
    lower_limit(RLIMIT_NOFILE, 1024);
    restrict_syscalls();
    verify_parent(parent_pid);
    fprintf(stderr, "AUTOSOURCARR_SANDBOX_V2 %s {\"pid\":%ld,\"uid\":%ld,\"ppid\":%ld,\"pdeathsig\":%d,\"landlock\":%d,\"nnp\":1,\"seccomp\":2}\n",
        argv[2], (long)getpid(), (long)geteuid(), (long)parent_pid, SIGKILL, abi);
    fflush(stderr);
    verify_parent(parent_pid);
    argv[4] = AS_QEMU;
    /* Parent PID and proof nonce are host-only and never reach QEMU or its guest. */
    execv(AS_QEMU, &argv[4]);
    fail("Cannot start hardware-isolated repair VM");
    return 78;
}
