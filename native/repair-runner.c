#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <glob.h>
#include <linux/audit.h>
#include <linux/capability.h>
#include <linux/filter.h>
#include <linux/sched.h>
#include <linux/seccomp.h>
#include <limits.h>
#include <netinet/in.h>
#include <poll.h>
#include <signal.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/prctl.h>
#include <sys/resource.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/uio.h>
#include <sys/wait.h>
#include <unistd.h>

/*
 * Process repair runner: Landlock (ABI >= 4, network rules required), no_new_privs, empty
 * capabilities, seccomp and a connect() gate.
 *
 *   repair-runner <job-dir> <proxy-port> -- <program> [args...]
 *
 * Landlock network rules match a TCP port, never an address, so "connect only to the proxy
 * port" alone would still allow 203.0.113.9:<proxy-port>. The runner therefore stays as a
 * small supervisor: the program's connect() calls are delivered to it through a seccomp user
 * notification, it compares the requested address with 127.0.0.1:<proxy-port>, and performs an
 * allowed connect itself on a duplicate of the program's socket using its own constant address
 * (pidfd_getfd). The program's argument memory is only read to choose allow or deny; it never
 * decides where the socket goes, so rewriting it after the check cannot redirect the connection.
 * Landlock still limits both processes to the proxy port as the second layer.
 */

/* Stable Landlock layout; no dependency on newer host UAPI headers. */
struct as_ruleset { uint64_t fs, net, scoped; };
struct as_path { uint64_t access; int32_t parent_fd; } __attribute__((packed));
struct as_port { uint64_t access; uint64_t port; };
#define AS_LL_CREATE 444
#define AS_LL_ADD 445
#define AS_LL_RESTRICT 446
#define AS_RULE_PATH 1
#define AS_RULE_PORT 2
#define AS_EXEC (1ULL << 0)
#define AS_WRITE (1ULL << 1)
#define AS_READ (1ULL << 2)
#define AS_DIR (1ULL << 3)
#define AS_REMOVE_DIR (1ULL << 4)
#define AS_REMOVE_FILE (1ULL << 5)
#define AS_MAKE_DIR (1ULL << 7)
#define AS_MAKE_REG (1ULL << 8)
#define AS_MAKE_FIFO (1ULL << 10)
#define AS_MAKE_SYM (1ULL << 12)
#define AS_REFER (1ULL << 13)
#define AS_TRUNCATE (1ULL << 14)
#define AS_IOCTL (1ULL << 15)
#define AS_FILE_RIGHTS (AS_EXEC | AS_WRITE | AS_READ | AS_TRUNCATE | AS_IOCTL)
#define AS_NET_BIND 1ULL
#define AS_NET_CONNECT 2ULL
#define AS_SCOPE_ALL 3ULL
#define AS_RX (AS_READ | AS_DIR | AS_EXEC)
#define AS_RO (AS_READ | AS_DIR)
#define AS_JOB (AS_EXEC | AS_WRITE | AS_READ | AS_DIR | AS_REMOVE_DIR | AS_REMOVE_FILE | AS_MAKE_DIR \
    | AS_MAKE_REG | AS_MAKE_FIFO | AS_MAKE_SYM | AS_REFER | AS_TRUNCATE)
#define AS_JOB_SCOPE "/source-maintenance/runner-jobs/"
#ifndef PIDFD_THREAD
#define PIDFD_THREAD O_EXCL
#endif
#define AS_NS_FLAGS (CLONE_NEWNS | CLONE_NEWUTS | CLONE_NEWIPC | CLONE_NEWUSER | CLONE_NEWPID \
    | CLONE_NEWNET | CLONE_NEWCGROUP)

static uint64_t handled_fs;
static int landlock_abi;
/* Collected before any domain: inside one, /etc itself is no longer listable. */
static glob_t java_config;
static int java_config_found;

static void fail(const char *stage)
{
    perror(stage);
    _exit(78);
}

static void refuse(const char *message)
{
    fprintf(stderr, "repair-runner: %s\n", message);
    _exit(78);
}

static void allow_path(int ruleset, const char *path, uint64_t access, int optional)
{
    int fd = open(path, O_PATH | O_CLOEXEC);
    if (fd < 0) {
        if (optional && errno == ENOENT)
            return;
        fail(path);
    }
    struct stat info;
    if (fstat(fd, &info) != 0)
        fail("Cannot inspect fixed sandbox path");
    if (!S_ISDIR(info.st_mode))
        access &= AS_FILE_RIGHTS;
    struct as_path rule = { .access = access & handled_fs, .parent_fd = fd };
    if (syscall(AS_LL_ADD, ruleset, AS_RULE_PATH, &rule, 0) != 0)
        fail("Cannot add sandbox path rule");
    close(fd);
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

/* JAVA_TOOL_OPTIONS has no portable quoting; job paths stay in a plain alphabet. */
static int plain_path(const char *value)
{
    for (const char *c = value; *c; ++c)
        if (!((*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') || (*c >= '0' && *c <= '9')
              || *c == '/' || *c == '.' || *c == '_' || *c == '-'))
            return 0;
    return 1;
}

static int beneath(const char *path, const char *root)
{
    size_t length = strlen(root);
    return strncmp(path, root, length) == 0 && (path[length] == '/' || path[length] == '\0');
}

static unsigned proxy_port(const char *value)
{
    if (!value || value[0] < '1' || value[0] > '9' || strlen(value) > 5)
        refuse("Invalid proxy port");
    unsigned long parsed = 0;
    for (const char *c = value; *c; ++c) {
        if (*c < '0' || *c > '9')
            refuse("Invalid proxy port");
        parsed = parsed * 10 + (unsigned long)(*c - '0');
    }
    if (parsed < 1024 || parsed > 65535)
        refuse("Proxy port must be within 1024-65535");
    return (unsigned)parsed;
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

static void arm_parent(pid_t expected)
{
    /* An already-dead parent emits no later event: check around setup as well. */
    int signal_number = 0;
    if (getppid() != expected || prctl(PR_SET_PDEATHSIG, SIGKILL, 0, 0, 0) != 0
        || prctl(PR_GET_PDEATHSIG, &signal_number, 0, 0, 0) != 0 || signal_number != SIGKILL
        || getppid() != expected) {
        errno = ESRCH;
        fail("Repair parent lifetime guard is unavailable");
    }
}

static char *jdk_root(const char *program)
{
    /* /usr/lib/jvm/<jdk>/bin/java or /opt/<jdk>/bin/java -> the JDK directory. */
    if (!beneath(program, "/usr/lib/jvm") && !beneath(program, "/opt"))
        return NULL;
    char *root = strdup(program);
    if (!root)
        fail("Cannot inspect program root");
    char *slash = strrchr(root, '/');
    if (slash && slash != root) {
        *slash = '\0';
        slash = strrchr(root, '/');
        if (slash && strcmp(slash, "/bin") == 0 && slash != root) {
            *slash = '\0';
            if (strcmp(root, "/opt") != 0 && strcmp(root, "/usr/lib/jvm") != 0 && strncmp(root, "/opt/autosourcarr", 17) != 0)
                return root;
        }
    }
    free(root);
    return NULL;
}

/* The supervisor's domain is a superset of the program's domain: nested domains intersect. */
static int build_ruleset(const char *job, const char *jdk, unsigned port, int supervisor)
{
    struct as_ruleset policy = {
        .fs = handled_fs, .net = AS_NET_BIND | AS_NET_CONNECT,
        .scoped = landlock_abi >= 6 ? AS_SCOPE_ALL : 0
    };
    int ruleset = (int)syscall(AS_LL_CREATE, &policy, landlock_abi >= 6 ? sizeof policy : 2 * sizeof(uint64_t), 0);
    if (ruleset < 0)
        fail("Cannot create repair restriction domain");
    const char *executable[] = { "/usr", "/lib", "/lib64", "/bin", "/etc/alternatives", "/opt/java", NULL };
    for (size_t i = 0; executable[i]; ++i)
        allow_path(ruleset, executable[i], AS_RX, 1);
    if (jdk)
        allow_path(ruleset, jdk, AS_RX, 0);
    const char *readonly[] = { "/etc/ld.so.cache", "/etc/ssl", "/etc/ca-certificates",
        "/proc/cpuinfo", "/proc/meminfo", "/sys/devices/system/cpu", NULL };
    for (size_t i = 0; readonly[i]; ++i)
        allow_path(ruleset, readonly[i], AS_RO, 1);
    for (size_t i = 0; java_config_found && i < java_config.gl_pathc; ++i)
        allow_path(ruleset, java_config.gl_pathv[i], AS_RO, 1);
    /* The supervisor resolves a notifying thread's process through /proc/<tid>/status. */
    allow_path(ruleset, supervisor ? "/proc" : "/proc/self", AS_RO, 0);
    allow_path(ruleset, "/dev/null", AS_READ | AS_WRITE | AS_TRUNCATE, 0);
    allow_path(ruleset, "/dev/urandom", AS_READ, 0);
    allow_path(ruleset, job, AS_JOB, 0);
    struct as_port connect_rule = { .access = AS_NET_CONNECT, .port = port };
    if (syscall(AS_LL_ADD, ruleset, AS_RULE_PORT, &connect_rule, 0) != 0)
        fail("Cannot add sandbox network rule");
    return ruleset;
}

static void enter_domain(int ruleset)
{
    if (syscall(AS_LL_RESTRICT, ruleset, 0) != 0)
        fail("Cannot enter repair restriction domain");
    close(ruleset);
}

/* --- seccomp ------------------------------------------------------------------------------ */

#define AS_ARG(i) (offsetof(struct seccomp_data, args) + 8 * (i))
#define AS_RET(value) BPF_STMT(BPF_RET | BPF_K, (value))
#define AS_ERR(code) AS_RET(SECCOMP_RET_ERRNO | ((code) & SECCOMP_RET_DATA))
#define AS_DENY(nr) BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, (nr), 0, 1), AS_ERR(EPERM)
/* Deny when a flag argument carries any bit of mask; otherwise allow this syscall. */
#define AS_DENY_FLAGS(nr, arg, mask, code) \
    BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, (nr), 0, 4), \
    BPF_STMT(BPF_LD | BPF_W | BPF_ABS, AS_ARG(arg)), \
    BPF_JUMP(BPF_JMP | BPF_JSET | BPF_K, (mask), 0, 1), \
    AS_ERR(code), \
    AS_RET(SECCOMP_RET_ALLOW)
#define AS_HEADER \
    BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, arch)), \
    BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, AUDIT_ARCH_X86_64, 1, 0), \
    AS_RET(SECCOMP_RET_KILL_PROCESS), \
    BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, nr)), \
    BPF_JUMP(BPF_JMP | BPF_JSET | BPF_K, 0x40000000, 0, 1), \
    AS_RET(SECCOMP_RET_KILL_PROCESS)
/* Shared with the launcher's list, minus the socket calls a JVM needs (gated below instead). */
#define AS_COMMON_DENY \
    AS_DENY(__NR_ptrace), AS_DENY(__NR_process_vm_writev), AS_DENY(__NR_kcmp), \
    AS_DENY(__NR_io_uring_setup), AS_DENY(__NR_io_uring_enter), AS_DENY(__NR_io_uring_register), \
    AS_DENY(__NR_bpf), AS_DENY(__NR_perf_event_open), AS_DENY(__NR_userfaultfd), \
    AS_DENY(__NR_mount), AS_DENY(__NR_umount2), AS_DENY(__NR_pivot_root), \
    AS_DENY(__NR_unshare), AS_DENY(__NR_setns), \
    AS_DENY(__NR_keyctl), AS_DENY(__NR_add_key), AS_DENY(__NR_request_key), \
    AS_DENY(__NR_bind), AS_DENY(__NR_listen), AS_DENY(__NR_accept), AS_DENY(__NR_accept4)

static void install_filter(const struct sock_filter *instructions, size_t count, unsigned flags, int *listener)
{
    struct sock_fprog program = { .len = (unsigned short)count, .filter = (struct sock_filter *)instructions };
    long result = syscall(__NR_seccomp, SECCOMP_SET_MODE_FILTER, flags, &program);
    if (result < 0)
        fail("Cannot enforce repair syscall restrictions");
    if (listener)
        *listener = (int)result;
}

static int restrict_program(void)
{
#if !defined(__x86_64__)
#error Repair runner currently requires Linux x86_64.
#endif
    const struct sock_filter instructions[] = {
        AS_HEADER,
        /* Every connect() is decided by the supervisor (see the file comment). */
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, __NR_connect, 0, 1), AS_RET(SECCOMP_RET_USER_NOTIF),
        /* Only TCP over IPv4/IPv6: no UDP/DNS, raw, netlink, packet or Unix-path sockets.
           Landlock network rules cover TCP alone, and Unix sockets ignore them entirely. */
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, __NR_socket, 0, 11),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, AS_ARG(0)),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, AF_INET, 1, 0),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, AF_INET6, 0, 7),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, AS_ARG(1)),
        BPF_STMT(BPF_ALU | BPF_AND | BPF_K, ~(uint32_t)(SOCK_NONBLOCK | SOCK_CLOEXEC)),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, SOCK_STREAM, 0, 4),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, AS_ARG(2)),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, 0, 1, 0),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, IPPROTO_TCP, 0, 1),
        AS_RET(SECCOMP_RET_ALLOW),
        AS_ERR(EACCES),
        /* TCP Fast Open connects from sendto/sendmsg without connect(). */
        AS_DENY_FLAGS(__NR_sendto, 3, MSG_FASTOPEN, EOPNOTSUPP),
        AS_DENY_FLAGS(__NR_sendmsg, 2, MSG_FASTOPEN, EOPNOTSUPP),
        AS_DENY_FLAGS(__NR_sendmmsg, 3, MSG_FASTOPEN, EOPNOTSUPP),
        /* No new namespaces; clone3 hides its flags in memory, so libc falls back to clone. */
        AS_DENY_FLAGS(__NR_clone, 0, AS_NS_FLAGS, EPERM),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, __NR_clone3, 0, 1), AS_ERR(ENOSYS),
        /* A second listener could answer CONTINUE for this one (the kernel also says EBUSY). */
        AS_DENY_FLAGS(__NR_seccomp, 1, SECCOMP_FILTER_FLAG_NEW_LISTENER, EPERM),
        /* The whole run stays in the runner's process group, so a group kill reaches it. */
        AS_DENY(__NR_setsid), AS_DENY(__NR_setpgid),
        AS_DENY(__NR_process_vm_readv), AS_DENY(__NR_pidfd_getfd), AS_DENY(__NR_pidfd_open),
        AS_COMMON_DENY,
        AS_RET(SECCOMP_RET_ALLOW)
    };
    int listener = -1;
    install_filter(instructions, sizeof instructions / sizeof instructions[0], SECCOMP_FILTER_FLAG_NEW_LISTENER, &listener);
    return listener;
}

static void restrict_supervisor(void)
{
    /* The supervisor never creates sockets or runs programs; it only duplicates and connects. */
    const struct sock_filter instructions[] = {
        AS_HEADER,
        AS_DENY(__NR_socket), AS_DENY(__NR_socketpair), AS_DENY(__NR_execve), AS_DENY(__NR_execveat),
        AS_DENY(__NR_clone), AS_DENY(__NR_clone3), AS_DENY(__NR_fork), AS_DENY(__NR_vfork),
        AS_DENY(__NR_sendto), AS_DENY(__NR_sendmmsg),
        AS_COMMON_DENY,
        AS_RET(SECCOMP_RET_ALLOW)
    };
    install_filter(instructions, sizeof instructions / sizeof instructions[0], 0, NULL);
}

/* --- descriptor passing ------------------------------------------------------------------- */

static void send_fd(int channel, int fd)
{
    char byte = 'L';
    struct iovec data = { .iov_base = &byte, .iov_len = 1 };
    union { struct cmsghdr header; char space[CMSG_SPACE(sizeof(int))]; } control;
    memset(&control, 0, sizeof control);
    struct msghdr message = { .msg_iov = &data, .msg_iovlen = 1, .msg_control = control.space, .msg_controllen = sizeof control.space };
    struct cmsghdr *header = CMSG_FIRSTHDR(&message);
    header->cmsg_level = SOL_SOCKET;
    header->cmsg_type = SCM_RIGHTS;
    header->cmsg_len = CMSG_LEN(sizeof(int));
    memcpy(CMSG_DATA(header), &fd, sizeof fd);
    if (sendmsg(channel, &message, MSG_NOSIGNAL) != 1)
        fail("Cannot hand the connect gate to the supervisor");
}

static int receive_fd(int channel)
{
    char byte = 0;
    struct iovec data = { .iov_base = &byte, .iov_len = 1 };
    union { struct cmsghdr header; char space[CMSG_SPACE(sizeof(int))]; } control;
    memset(&control, 0, sizeof control);
    struct msghdr message = { .msg_iov = &data, .msg_iovlen = 1, .msg_control = control.space, .msg_controllen = sizeof control.space };
    ssize_t received;
    do received = recvmsg(channel, &message, MSG_CMSG_CLOEXEC);
    while (received < 0 && errno == EINTR);
    struct cmsghdr *header = received == 1 ? CMSG_FIRSTHDR(&message) : NULL;
    if (!header || header->cmsg_level != SOL_SOCKET || header->cmsg_type != SCM_RIGHTS
        || header->cmsg_len != CMSG_LEN(sizeof(int)) || (message.msg_flags & MSG_CTRUNC))
        return -1;
    int fd;
    memcpy(&fd, CMSG_DATA(header), sizeof fd);
    return fd;
}

/* --- supervisor --------------------------------------------------------------------------- */

static int notification_valid(int listener, uint64_t id)
{
    return ioctl(listener, SECCOMP_IOCTL_NOTIF_ID_VALID, &id) == 0;
}

static pid_t thread_group(pid_t tid)
{
    char path[64], buffer[1024];
    snprintf(path, sizeof path, "/proc/%d/status", (int)tid);
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0)
        return -1;
    ssize_t length = read(fd, buffer, sizeof buffer - 1);
    close(fd);
    if (length <= 0)
        return -1;
    buffer[length] = '\0';
    char *line = strstr(buffer, "\nTgid:");
    if (!line)
        return -1;
    long value = strtol(line + 6, NULL, 10);
    return value > 0 && value <= INT_MAX ? (pid_t)value : -1;
}

static int open_task(pid_t tid)
{
    int pidfd = (int)syscall(SYS_pidfd_open, tid, PIDFD_THREAD);
    if (pidfd >= 0 || errno != EINVAL)
        return pidfd;
    /* Kernels before 6.9 accept only thread-group leaders. */
    pid_t leader = thread_group(tid);
    return leader > 0 ? (int)syscall(SYS_pidfd_open, leader, 0) : -1;
}

static int socket_option(int fd, int option)
{
    int value = -1;
    socklen_t size = sizeof value;
    return getsockopt(fd, SOL_SOCKET, option, &value, &size) == 0 && size == sizeof value ? value : -1;
}

static unsigned refusals_logged;

static int decide_connect(int listener, const struct seccomp_notif *request, unsigned port)
{
    uint64_t length = request->data.args[2];
    struct sockaddr_storage requested;
    memset(&requested, 0, sizeof requested);
    if (length < sizeof(sa_family_t) || length > sizeof requested)
        return EINVAL;
    struct iovec local = { .iov_base = &requested, .iov_len = (size_t)length };
    struct iovec remote = { .iov_base = (void *)(uintptr_t)request->data.args[1], .iov_len = (size_t)length };
    if (process_vm_readv((pid_t)request->pid, &local, 1, &remote, 1, 0) != (ssize_t)length)
        return EFAULT;
    if (!notification_valid(listener, request->id))
        return ESRCH;
    int allowed = 0;
    char shown[64] = "unsupported address";
    if (requested.ss_family == AF_INET && length >= sizeof(struct sockaddr_in)) {
        const struct sockaddr_in *v4 = (const struct sockaddr_in *)&requested;
        const unsigned char *a = (const unsigned char *)&v4->sin_addr;
        allowed = v4->sin_addr.s_addr == htonl(INADDR_LOOPBACK) && ntohs(v4->sin_port) == port;
        snprintf(shown, sizeof shown, "%u.%u.%u.%u:%u", a[0], a[1], a[2], a[3], ntohs(v4->sin_port));
    } else if (requested.ss_family == AF_INET6 && length >= sizeof(struct sockaddr_in6)) {
        const struct sockaddr_in6 *v6 = (const struct sockaddr_in6 *)&requested;
        static const unsigned char mapped[16] = { 0,0,0,0,0,0,0,0,0,0,0xff,0xff,127,0,0,1 };
        allowed = memcmp(&v6->sin6_addr, mapped, 16) == 0 && ntohs(v6->sin6_port) == port;
        snprintf(shown, sizeof shown, "[ipv6]:%u", ntohs(v6->sin6_port));
    }
    if (!allowed) {
        if (refusals_logged < 32) {
            ++refusals_logged;
            fprintf(stderr, "repair-runner: refused direct connect to %s (only the egress proxy is reachable)\n", shown);
        }
        return EACCES;
    }
    int pidfd = open_task((pid_t)request->pid);
    if (pidfd < 0)
        return EACCES;
    if (!notification_valid(listener, request->id)) {
        close(pidfd);
        return ESRCH;
    }
    int target = (int)syscall(SYS_pidfd_getfd, pidfd, (int)request->data.args[0], 0);
    close(pidfd);
    if (target < 0)
        return errno == EBADF ? EBADF : EACCES;
    int result = EACCES;
    if (socket_option(target, SO_DOMAIN) == requested.ss_family && socket_option(target, SO_TYPE) == SOCK_STREAM
        && socket_option(target, SO_PROTOCOL) == IPPROTO_TCP) {
        int domain = requested.ss_family;
        /* The supervisor's own constant address; never the bytes it just inspected. */
        struct sockaddr_in v4 = { .sin_family = AF_INET, .sin_port = htons((uint16_t)port), .sin_addr = { htonl(INADDR_LOOPBACK) } };
        struct sockaddr_in6 v6 = { .sin6_family = AF_INET6, .sin6_port = htons((uint16_t)port) };
        static const unsigned char mapped[16] = { 0,0,0,0,0,0,0,0,0,0,0xff,0xff,127,0,0,1 };
        memcpy(&v6.sin6_addr, mapped, 16);
        int status;
        do status = domain == AF_INET ? connect(target, (struct sockaddr *)&v4, sizeof v4)
                                      : connect(target, (struct sockaddr *)&v6, sizeof v6);
        while (status != 0 && errno == EINTR);
        result = status == 0 ? 0 : errno;
    }
    close(target);
    return result;
}

static int exit_status(int status)
{
    if (WIFEXITED(status))
        return WEXITSTATUS(status);
    if (WIFSIGNALED(status))
        return 128 + WTERMSIG(status);
    return 78;
}

static int supervise(pid_t child, int pidfd, int listener, unsigned port)
{
    struct pollfd watched[2] = { { .fd = pidfd, .events = POLLIN }, { .fd = listener, .events = POLLIN } };
    for (;;) {
        if (poll(watched, 2, -1) < 0) {
            if (errno == EINTR)
                continue;
            kill(child, SIGKILL);
            fail("Repair supervisor lost its wait");
        }
        if (watched[1].revents & POLLIN) {
            struct seccomp_notif request;
            memset(&request, 0, sizeof request);
            if (ioctl(listener, SECCOMP_IOCTL_NOTIF_RECV, &request) == 0) {
                struct seccomp_notif_resp response = { .id = request.id };
                int error = request.data.nr == __NR_connect ? decide_connect(listener, &request, port) : EPERM;
                response.error = error ? -error : 0;
                /* ENOENT: the caller was interrupted or killed; nothing to answer. */
                if (ioctl(listener, SECCOMP_IOCTL_NOTIF_SEND, &response) != 0 && errno != ENOENT)
                    fprintf(stderr, "repair-runner: connect gate response failed: %s\n", strerror(errno));
            } else if (errno != EINTR && errno != ENOENT) {
                kill(child, SIGKILL);
                fail("Repair connect gate failed");
            }
        }
        if (watched[0].revents & (POLLIN | POLLHUP) || watched[1].revents & (POLLHUP | POLLERR)) {
            int status = 0;
            pid_t reaped;
            do reaped = waitpid(child, &status, 0);
            while (reaped < 0 && errno == EINTR);
            if (reaped != child)
                fail("Cannot collect the repair program");
            return exit_status(status);
        }
    }
}

int main(int argc, char **argv)
{
    int forced_abi = 0;
#ifdef AUTOSOURCARR_TEST
    const char *forced = getenv("AUTOSOURCARR_TEST_FORCE_ABI");
    if (forced)
        forced_abi = atoi(forced);
#endif
    if (argc < 5 || strcmp(argv[3], "--") != 0)
        refuse("usage: repair-runner <job-dir> <proxy-port> -- <program> [args...]");
    if (geteuid() == 0)
        refuse("The repair runner must not run as root");
    unsigned port = proxy_port(argv[2]);
    char *job = realpath(argv[1], NULL);
    if (!job || strcmp(job, argv[1]) != 0)
        refuse("Repair workspace must be canonical");
    char *leaf = strrchr(job, '/');
    size_t scope = strlen(AS_JOB_SCOPE);
    if (!leaf || !valid_hex(leaf + 1, 32) || (size_t)(leaf + 1 - job) < scope
        || strncmp(leaf + 1 - scope, AS_JOB_SCOPE, scope) != 0 || !plain_path(job))
        refuse("Invalid repair workspace ownership scope");
    struct stat info;
    if (lstat(job, &info) != 0 || !S_ISDIR(info.st_mode)
        || info.st_uid != geteuid() || (info.st_mode & 0077) != 0)
        refuse("Repair workspace must be a private (0700) directory owned by the caller");
    char *program = realpath(argv[4], NULL);
    if (argv[4][0] != '/' || !program)
        refuse("The repair program must be an absolute existing path");
    char *jdk = jdk_root(program);
    const char *roots[] = { "/usr", "/lib", "/lib64", "/bin", "/opt/java", job, jdk, NULL };
    int inside = 0;
    for (size_t i = 0; i < sizeof roots / sizeof roots[0] && roots[i]; ++i)
        if (beneath(program, roots[i]))
            inside = 1;
    if (!inside)
        refuse("The repair program must live inside the sandbox's executable roots");

    pid_t parent_pid = getppid();
    if (setpgid(0, 0) != 0)
        fail("Cannot establish repair process ownership");
    if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0)
        fail("Cannot restrict repair privileges");
    struct __user_cap_header_struct cap_header = { .version = _LINUX_CAPABILITY_VERSION_3, .pid = 0 };
    struct __user_cap_data_struct cap_data[2] = {{0}, {0}};
    if (syscall(SYS_capset, &cap_header, cap_data) != 0)
        fail("Cannot clear repair capabilities");
    /* Credential drops precede setup because credential transitions can clear it. */
    arm_parent(parent_pid);

    landlock_abi = (int)syscall(AS_LL_CREATE, NULL, 0, 1);
    if (forced_abi > 0 && forced_abi < landlock_abi)
        landlock_abi = forced_abi;
    if (landlock_abi < 4)
        refuse("Landlock ABI 4 (TCP network rules) is required; no isolation fallback is enabled");
    handled_fs = landlock_abi >= 5 ? (1ULL << 16) - 1 : (1ULL << 15) - 1;

    char port_text[8], proxy[48], java_options[PATH_MAX * 2 + 512];
    snprintf(port_text, sizeof port_text, "%u", port);
    snprintf(proxy, sizeof proxy, "http://127.0.0.1:%u", port);
    snprintf(java_options, sizeof java_options,
        "-Dhttp.proxyHost=127.0.0.1 -Dhttp.proxyPort=%u -Dhttps.proxyHost=127.0.0.1 -Dhttps.proxyPort=%u "
        "-Dhttp.nonProxyHosts= -Djava.net.useSystemProxies=false -Duser.home=%s -Djava.io.tmpdir=%s",
        port, port, job, job);
    if (clearenv() != 0 || setenv("LANG", "C.UTF-8", 1) != 0 || setenv("TZ", "UTC", 1) != 0
        || setenv("PATH", "/usr/local/bin:/usr/bin:/bin", 1) != 0
        || setenv("HOME", job, 1) != 0 || setenv("TMPDIR", job, 1) != 0
        || setenv("HTTP_PROXY", proxy, 1) != 0 || setenv("HTTPS_PROXY", proxy, 1) != 0
        || setenv("http_proxy", proxy, 1) != 0 || setenv("https_proxy", proxy, 1) != 0
        || setenv("NO_PROXY", "", 1) != 0 || setenv("no_proxy", "", 1) != 0
        || setenv("AUTOSOURCARR_PROXY_PORT", port_text, 1) != 0
        || setenv("JAVA_TOOL_OPTIONS", java_options, 1) != 0
        || (jdk && setenv("JAVA_HOME", jdk, 1) != 0))
        fail("Cannot isolate repair environment");
    if (chdir(job) != 0)
        fail("Cannot enter the repair workspace");

    java_config_found = glob("/etc/java*", 0, NULL, &java_config) == 0;
    enter_domain(build_ruleset(job, jdk, port, 1));
    if (syscall(__NR_close_range, 3U, ~0U, 0) != 0)
        fail("Cannot close unrelated inherited file descriptors");
    lower_limit(RLIMIT_AS, 16ULL << 30);
    lower_limit(RLIMIT_NOFILE, 4096);
    lower_limit(RLIMIT_FSIZE, 4ULL << 30);
    lower_limit(RLIMIT_CORE, 0);

    int channel[2];
    if (socketpair(AF_UNIX, SOCK_SEQPACKET | SOCK_CLOEXEC, 0, channel) != 0)
        fail("Cannot prepare the connect gate");
    pid_t supervisor = getpid();
    pid_t child = fork();
    if (child < 0)
        fail("Cannot start the repair program");
    if (child == 0) {
        close(channel[0]);
        arm_parent(supervisor);
        enter_domain(build_ruleset(job, jdk, port, 0));
        int listener = restrict_program();
        send_fd(channel[1], listener);
        close(listener);
        char ready = 0;
        ssize_t got;
        do got = read(channel[1], &ready, 1);
        while (got < 0 && errno == EINTR);
        if (got != 1 || ready != 'G')
            _exit(78);
        close(channel[1]);
        argv[4] = program;
        execv(program, &argv[4]);
        fail("Cannot start the repair program");
    }
    close(channel[1]);
    int pidfd = (int)syscall(SYS_pidfd_open, child, 0);
    int listener = receive_fd(channel[0]);
    int probe = pidfd >= 0 ? (int)syscall(SYS_pidfd_getfd, pidfd, 2, 0) : -1;
    if (pidfd < 0 || listener < 0 || probe < 0) {
        kill(child, SIGKILL);
        waitpid(child, NULL, 0);
        refuse("The connect gate (pidfd_open, pidfd_getfd, seccomp user notification) is unavailable; no isolation fallback is enabled");
    }
    close(probe);
    restrict_supervisor();
    if (getppid() != parent_pid) {
        kill(child, SIGKILL);
        refuse("Repair parent changed during setup");
    }
    fprintf(stderr, "AUTOSOURCARR_RUNNER_V1 %s {\"pid\":%ld,\"uid\":%ld,\"ppid\":%ld,\"pdeathsig\":%d,\"landlock\":%d,\"nnp\":1,\"seccomp\":2,\"netgate\":1,\"proxy\":%u,\"scoped\":%d}\n",
        leaf + 1, (long)getpid(), (long)geteuid(), (long)parent_pid, SIGKILL, landlock_abi, port, landlock_abi >= 6 ? 3 : 0);
    fflush(stderr);
    char go = 'G';
    if (write(channel[0], &go, 1) != 1) {
        kill(child, SIGKILL);
        fail("Cannot release the repair program");
    }
    close(channel[0]);
    return supervise(child, pidfd, listener, port);
}
