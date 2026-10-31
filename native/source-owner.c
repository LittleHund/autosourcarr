#define _GNU_SOURCE
#include "source-owner.h"
#include "process-identity.h"
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <poll.h>
#include <signal.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/fsuid.h>
#include <sys/prctl.h>
#include <sys/random.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <sys/xattr.h>
#include <time.h>
#include <unistd.h>

#if !defined(__linux__) || !defined(__x86_64__)
#error "The source owner requires the qualified Linux x86_64 ABI."
#endif

/* No application/configuration/parser code runs before the shared native drop.
 * This file has no main or caller-selected executable/path factory. The actual
 * immutable image and managed observer consumer remain dependent integration.
 * Missing assets are a refused start, never a home-script fallback. */
#define IO_BUDGET_MS 5000U
#define TERM_GRACE_MS 10000U
#define REPLY_CAPACITY 4U
#define ENV_CAPACITY 280U
#define CHILD_LIST_MAXIMUM 16384U
#define OBSERVATION_BUDGET_MS 10000U

/* Fixed binary markers deliberately have no C string terminator. */
static const unsigned char command_magic[8] = {'A', 'S', 'O', 'C', 'M', 'D', '1', '\n'};
static const unsigned char response_magic[8] = {'A', 'S', 'O', 'R', 'E', 'P', '1', '\n'};
static const unsigned char proof_magic[8] = {'A', 'S', 'O', 'P', 'R', 'F', '1', '\n'};
static const unsigned char log_magic[8] = {'A', 'S', 'O', 'L', 'O', 'G', '1', '\n'};
static const unsigned char observation_magic[8] = {'A', 'S', 'O', 'O', 'B', 'S', '1', '\n'};
_Static_assert(AUTOSOURCARR_SOURCE_LOG_FRAME_MAXIMUM <= PIPE_BUF, "Source output frames must be atomic pipe writes");
_Static_assert(AUTOSOURCARR_SOURCE_OBSERVATION_EVENT_BYTES <= PIPE_BUF, "Observation event must be an atomic pipe write");
static volatile sig_atomic_t termination_requested;
static int clock_failed;
extern char **environ;

enum observation_phase { OBSERVATION_NONE=0,OBSERVATION_CAPTURING=1,OBSERVATION_ISSUED=2,OBSERVATION_CLEANING=3,OBSERVATION_BLOCKED=4 };
struct observation {
    int phase,pid,read,eof,reaped,status,expired,invalid,event_pending,automatic_cleanup;
    uint32_t kind;
    uint64_t request,generation,release_request,deadline,event_deadline;
    size_t used,wanted;
    char ticket[33],leaf[AUTOSOURCARR_SOURCE_OBSERVATION_LEAF_MAXIMUM+1U];
    unsigned char incoming[AUTOSOURCARR_SOURCE_OBSERVATION_RESULT_BYTES];
    struct autosourcarr_source_observation_result result;
};

struct owner {
    struct autosourcarr_source_owner_config binding;
    int root, control, response, observation_root;
    int log_read, exec_read;
    int physical_test, launch_selftest, log_eof, exec_eof, killed_reaped;
    int state, fault, shutdown;
    int no_children, leader, pgid, leader_reaped, exit_known, exit_status;
    uint32_t reaped, adopted;
    uint64_t generation, discarded, last_request, log_offset;
    int log_gap;
    uint64_t stop_at, exec_at, command_at;
    uint64_t start_request, stop_request;
    uint16_t start_opcode, stop_opcode;
    unsigned char incoming[AUTOSOURCARR_SOURCE_COMMAND_BYTES + AUTOSOURCARR_SOURCE_PAYLOAD_MAXIMUM];
    size_t incoming_size, incoming_wanted;
    unsigned char exec_error[8];
    size_t exec_error_size;
    unsigned char replies[REPLY_CAPACITY][AUTOSOURCARR_SOURCE_RESPONSE_BYTES];
    uint64_t reply_at[REPLY_CAPACITY];
    size_t reply_head, reply_count;
    struct observation observation;
};

struct launch_environment { char *items[ENV_CAPACITY]; size_t count; };

static uint16_t get16(const unsigned char *p) { return (uint16_t)((uint16_t)p[0] | (uint16_t)p[1] << 8); }
static uint32_t get32(const unsigned char *p)
{ return (uint32_t)p[0] | (uint32_t)p[1] << 8 | (uint32_t)p[2] << 16 | (uint32_t)p[3] << 24; }
static uint64_t get64(const unsigned char *p) { return (uint64_t)get32(p) | (uint64_t)get32(p + 4) << 32; }
static void put16(unsigned char *p, uint16_t n) { p[0] = (unsigned char)n; p[1] = (unsigned char)(n >> 8); }
static void put32(unsigned char *p, uint32_t n) { for (size_t i = 0; i < 4; i++) p[i] = (unsigned char)(n >> (8U * i)); }
static void put64(unsigned char *p, uint64_t n) { for (size_t i = 0; i < 8; i++) p[i] = (unsigned char)(n >> (8U * i)); }
static uint64_t milliseconds(void)
{
    struct timespec now;
    if (clock_gettime(CLOCK_MONOTONIC, &now) != 0) { clock_failed = 1; return 0; }
    return (uint64_t)now.tv_sec * 1000U + (uint64_t)now.tv_nsec / 1000000U;
}
static void close_owned(int *fd) { if (*fd >= 0) { int old = *fd; *fd = -1; (void)close(old); } }
static void signal_stop(int number) { (void)number; termination_requested = 1; }
static int nonblocking(int fd)
{
    int flags = fcntl(fd, F_GETFL);
    return flags < 0 ? -1 : fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}
static int discard_inherited_environment(void)
{
    /* The caller is a fork child; this never edits the native parent's copy.
     * No parent account/config values enter a worker exec environment. */
    if (environ != NULL) for (char **entry = environ; *entry != NULL; entry++) explicit_bzero(*entry, strlen(*entry));
    return clearenv();
}
static int same_root(int fd, const struct autosourcarr_source_owner_config *binding)
{
    struct stat status;
    return fstat(fd, &status) == 0 && S_ISDIR(status.st_mode) && status.st_nlink != 0
        && (uint64_t)status.st_dev == binding->root_device && (uint64_t)status.st_ino == binding->root_inode;
}
static int same_observation_root(int fd, const struct autosourcarr_source_owner_config *binding)
{
    struct stat status;
    int flags = fcntl(fd, F_GETFL), descriptor_flags = fcntl(fd, F_GETFD);
    return flags >= 0 && (flags & O_PATH) == 0
        && (flags & (O_ACCMODE | O_DIRECTORY)) == (O_RDONLY | O_DIRECTORY)
        && descriptor_flags >= 0 && (descriptor_flags & FD_CLOEXEC) != 0
        && fstat(fd, &status) == 0 && S_ISDIR(status.st_mode) && status.st_nlink != 0
        && status.st_uid == binding->uid && status.st_gid == 568 && (status.st_mode & 07777) == 0770
        && (uint64_t)status.st_dev == binding->observation_root_device
        && (uint64_t)status.st_ino == binding->observation_root_inode;
}

/* Paths passed here are fixed compile-time image paths, never control payloads. */
static int fixed_open(const char *path, int immutable, int regular)
{
    char remaining[PATH_MAX];
    if (path[0] != '/' || strlen(path) >= sizeof(remaining)) { errno = EINVAL; return -1; }
    memcpy(remaining, path + 1, strlen(path));
    int directory = open("/", O_PATH | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (directory < 0) return -1;
    struct stat initial;
    if (fstat(directory, &initial) != 0 || !S_ISDIR(initial.st_mode)
        || (immutable && (initial.st_uid != 0 || (initial.st_mode & 0022) != 0))) {
        (void)close(directory); errno = EPERM; return -1;
    }
    char *save = NULL, *part = strtok_r(remaining, "/", &save);
    while (part != NULL) {
        char *next_part = strtok_r(NULL, "/", &save);
        int flags = next_part == NULL && regular ? O_RDONLY | O_NONBLOCK : O_PATH | O_DIRECTORY;
        int next = openat(directory, part, flags | O_NOFOLLOW | O_CLOEXEC);
        struct stat status;
        if (next < 0 || fstat(next, &status) != 0
            || (next_part != NULL || !regular ? !S_ISDIR(status.st_mode) : !S_ISREG(status.st_mode))
            || status.st_nlink == 0 || (immutable && (status.st_uid != 0 || (status.st_mode & 0022) != 0))) {
            int saved = errno == 0 ? EPERM : errno;
            if (next >= 0) (void)close(next);
            (void)close(directory); errno = saved; return -1;
        }
        (void)close(directory); directory = next; part = next_part;
    }
    return directory;
}
static int root_current(struct owner *o)
{
    if (!same_root(o->root, &o->binding)) return -1;
    if (o->physical_test) return 0; /* Physical-only entry never admits an engine. */
    int current = fixed_open(AUTOSOURCARR_SOURCE_ROOT, 0, 0);
    if (current < 0) return -1;
    int valid = same_root(current, &o->binding); (void)close(current);
    if (!valid) { errno = ESTALE; return -1; }
    return 0;
}
static int trusted_asset(const char *path, int elf)
{
    int fd = fixed_open(path, 1, 1);
    if (fd < 0) return -1;
    struct stat status;
    unsigned char header[20];
    int valid = fstat(fd, &status) == 0 && status.st_nlink == 1
        && (status.st_mode & 06000) == 0;
    if (elf) valid = valid && (status.st_mode & 0111) != 0
        && pread(fd, header, sizeof(header), 0) == (ssize_t)sizeof(header)
        && memcmp(header, "\177ELF\002\001", 6) == 0 && header[18] == 62 && header[19] == 0;
    errno = 0;
    ssize_t capabilities = fgetxattr(fd, "security.capability", NULL, 0);
    valid = valid && capabilities == -1 && errno == ENODATA;
    if (!valid) { (void)close(fd); errno = EPERM; return -1; }
    return fd;
}
static int equals(const unsigned char *bytes, size_t size, const char *text)
{ return strlen(text) == size && memcmp(bytes, text, size) == 0; }
static int starts(const unsigned char *bytes, size_t size, const char *prefix)
{ return size >= strlen(prefix) && memcmp(bytes, prefix, strlen(prefix)) == 0; }
static int utf8(const unsigned char *bytes, size_t size)
{
    for (size_t i = 0; i < size;) {
        unsigned char first = bytes[i++];
        if (first == 0) return 0;
        if (first < 128) continue;
        uint32_t value; size_t count; uint32_t minimum;
        if (first >= 0xc2 && first <= 0xdf) { count = 1; value = first & 31U; minimum = 0x80; }
        else if (first >= 0xe0 && first <= 0xef) { count = 2; value = first & 15U; minimum = 0x800; }
        else if (first >= 0xf0 && first <= 0xf4) { count = 3; value = first & 7U; minimum = 0x10000; }
        else return 0;
        if (size - i < count) return 0;
        for (size_t j = 0; j < count; j++) { unsigned char c = bytes[i++]; if ((c & 0xc0U) != 0x80U) return 0; value = (value << 6) | (c & 63U); }
        if (value < minimum || value > 0x10ffff || (value >= 0xd800 && value <= 0xdfff)) return 0;
    }
    return 1;
}
static void environment_clear(struct launch_environment *environment)
{
    for (size_t i = 0; i < environment->count; i++) {
        if (environment->items[i] != NULL) { explicit_bzero(environment->items[i], strlen(environment->items[i])); free(environment->items[i]); }
    }
    memset(environment, 0, sizeof(*environment));
}
static int environment_add(struct launch_environment *environment, const unsigned char *key, size_t key_size,
    const unsigned char *value, size_t value_size)
{
    if (environment->count + 1 >= ENV_CAPACITY) return -1;
    char *item = malloc(key_size + value_size + 2U);
    if (item == NULL) return -1;
    memcpy(item, key, key_size); item[key_size] = '=';
    memcpy(item + key_size + 1, value, value_size); item[key_size + value_size + 1] = 0;
    environment->items[environment->count++] = item; environment->items[environment->count] = NULL;
    return 0;
}
static int environment_literal(struct launch_environment *environment, const char *key, const char *value)
{ return environment_add(environment, (const unsigned char *)key, strlen(key), (const unsigned char *)value, strlen(value)); }
static const char *forced_value(const unsigned char *key, size_t length)
{
    if (equals(key, length, "BIND_IP")) return "127.0.0.1";
    if (equals(key, length, "BIND_PORT")) return "4567";
    if (equals(key, length, "AUTH_MODE")) return "none";
    if (equals(key, length, "BASIC_AUTH_ENABLED") || equals(key, length, "WEB_UI_ENABLED") || equals(key, length, "INITIAL_OPEN_IN_BROWSER_ENABLED")) return "false";
    if (equals(key, length, "KCEF_ENABLED")) return "true";
    return NULL;
}
static int environment_parse(const unsigned char *payload, size_t length, struct launch_environment *environment)
{
    static const char *const fixed[][2] = {
        {"HOME", "/home/suwayomi"}, {"PATH", "/usr/bin:/bin"}, {"LANG", "C.UTF-8"},
        {"BIND_IP", "127.0.0.1"}, {"BIND_PORT", "4567"}, {"AUTH_MODE", "none"},
        {"BASIC_AUTH_ENABLED", "false"}, {"WEB_UI_ENABLED", "false"},
        {"INITIAL_OPEN_IN_BROWSER_ENABLED", "false"}, {"KCEF_ENABLED", "true"},
        {"AUTOSOURCARR_SOURCE_DATA_FD", "5"}
    };
    memset(environment, 0, sizeof(*environment));
    for (size_t i = 0; i < sizeof(fixed) / sizeof(fixed[0]); i++)
        if (environment_literal(environment, fixed[i][0], fixed[i][1]) != 0) goto invalid;
    if (length == 0) return 0;
    if (length < 2) goto invalid;
    size_t count = get16(payload), at = 2;
    if (count > 256) goto invalid;
    const unsigned char *keys[256]; size_t sizes[256];
    for (size_t i = 0; i < count; i++) {
        if (length - at < 6) goto invalid;
        size_t key_size = get16(payload + at), value_size = get32(payload + at + 2); at += 6;
        if (key_size == 0 || key_size > 128 || value_size > 8192 || key_size > length - at || value_size > length - at - key_size) goto invalid;
        const unsigned char *key = payload + at, *value = key + key_size; at += key_size + value_size;
        if (!utf8(value, value_size)) goto invalid;
        for (size_t j = 0; j < key_size; j++)
            if (!((key[j] >= 'A' && key[j] <= 'Z') || (key[j] >= 'a' && key[j] <= 'z') || key[j] == '_' || (j != 0 && key[j] >= '0' && key[j] <= '9'))) goto invalid;
        for (size_t j = 0; j < i; j++) if (sizes[j] == key_size && memcmp(keys[j], key, key_size) == 0) goto invalid;
        keys[i] = key; sizes[i] = key_size;
        if (equals(key, key_size, "HOME") || equals(key, key_size, "PATH") || equals(key, key_size, "JAVA_HOME")
            || starts(key, key_size, "LD_") || starts(key, key_size, "DOTNET_") || starts(key, key_size, "CORECLR_")
            || starts(key, key_size, "COMPlus_") || starts(key, key_size, "PYTHON")
            || equals(key, key_size, "_JAVA_OPTIONS") || equals(key, key_size, "JDK_JAVA_OPTIONS")
            || equals(key, key_size, "CLASSPATH") || equals(key, key_size, "BASH_ENV") || equals(key, key_size, "ENV") || equals(key, key_size, "SHELLOPTS")) goto invalid;
        if (equals(key, key_size, "JAVA_TOOL_OPTIONS")) {
            if (!equals(value, value_size, "-javaagent:/app/source-browser-agent.jar")) goto invalid;
            int agent = trusted_asset("/app/source-browser-agent.jar", 0); if (agent < 0) goto invalid; (void)close(agent);
        }
        if (starts(key, key_size, "AUTOSOURCARR_")) {
            if (equals(key, key_size, "AUTOSOURCARR_SOURCE_USER_AGENT")) {
                if (value_size == 0 || value_size > 1024) goto invalid;
                for (size_t j = 0; j < value_size; j++) if (value[j] < 32 || value[j] > 126) goto invalid;
            } else if (equals(key, key_size, "AUTOSOURCARR_SOURCE_UA_REVISION")) {
                if (value_size != 64) goto invalid;
                for (size_t j = 0; j < value_size; j++) if (!((value[j] >= '0' && value[j] <= '9') || (value[j] >= 'a' && value[j] <= 'f'))) goto invalid;
            } else if (equals(key, key_size, "AUTOSOURCARR_SOURCE_UA_STATUS")) {
                if (!equals(value, value_size, AUTOSOURCARR_SOURCE_ROOT "/cache/autosourcarr-user-agent.properties")) goto invalid;
            } else goto invalid;
        }
        const char *forced = forced_value(key, key_size);
        if (forced != NULL) { if (!equals(value, value_size, forced)) goto invalid; continue; }
        /* LANG is image-owned here; an identical supplied value is harmless but
         * duplicate exec environment entries are never emitted. */
        if (equals(key, key_size, "LANG")) { if (!equals(value, value_size, "C.UTF-8")) goto invalid; continue; }
        if (environment_add(environment, key, key_size, value, value_size) != 0) goto invalid;
    }
    if (at != length) goto invalid;
    return 0;
invalid:
    environment_clear(environment); errno = EINVAL; return -1;
}

static void begin_stop(struct owner *o)
{
    if (o->state != AUTOSOURCARR_SOURCE_STOPPING) { o->state = AUTOSOURCARR_SOURCE_STOPPING; o->stop_at = milliseconds() + TERM_GRACE_MS; }
}
static void fail_owner(struct owner *o, int diagnostic)
{
    if (o->fault == 0) o->fault = diagnostic;
    o->shutdown = 1; begin_stop(o);
}
static uint32_t facts(const struct owner *o)
{
    return (o->exit_known ? AUTOSOURCARR_SOURCE_FACT_EXIT : 0U)
        | (o->no_children ? AUTOSOURCARR_SOURCE_FACT_NO_CHILDREN : 0U)
        | (o->log_eof && o->exec_eof && o->observation.eof && o->observation.read<0 ? AUTOSOURCARR_SOURCE_FACT_STREAMS_DRAINED : 0U)
        | (o->physical_test || o->launch_selftest ? AUTOSOURCARR_SOURCE_FACT_PHYSICAL_TEST : 0U)
        | (o->killed_reaped ? AUTOSOURCARR_SOURCE_FACT_SIGKILL_REAPED : 0U)
        | (o->fault ? AUTOSOURCARR_SOURCE_FACT_FAILED : 0U);
}
static void queue_reply(struct owner *o, uint64_t request, uint16_t opcode, uint16_t result, int diagnostic)
{
    if (o->response < 0) return;
    if (o->reply_count == REPLY_CAPACITY) { fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_PROTOCOL); return; }
    size_t slot = (o->reply_head + o->reply_count) % REPLY_CAPACITY;
    unsigned char *reply = o->replies[slot]; memset(reply, 0, AUTOSOURCARR_SOURCE_RESPONSE_BYTES);
    memcpy(reply, response_magic, 8); memcpy(reply + 8, o->binding.boot_nonce, 32);
    put64(reply + 40, request); put16(reply + 48, opcode); put16(reply + 50, result);
    put32(reply + 52, 56); put64(reply + 56, o->generation);
    put32(reply + 64, (uint32_t)o->state); put32(reply + 68, (uint32_t)o->leader); put32(reply + 72, (uint32_t)o->pgid);
    put32(reply + 76, (uint32_t)o->exit_status); put32(reply + 80, facts(o));
    put32(reply + 84, o->reaped); put32(reply + 88, o->adopted); put32(reply + 92, (uint32_t)diagnostic);
    put64(reply + 96, o->discarded); put64(reply + 104, o->binding.root_device); put64(reply + 112, o->binding.root_inode);
    o->reply_at[slot] = milliseconds() + IO_BUDGET_MS; o->reply_count++;
}
static void flush_replies(struct owner *o)
{
    while (o->response >= 0 && o->reply_count != 0) {
        ssize_t wrote = write(o->response, o->replies[o->reply_head], AUTOSOURCARR_SOURCE_RESPONSE_BYTES);
        if (wrote == (ssize_t)AUTOSOURCARR_SOURCE_RESPONSE_BYTES) { o->reply_head = (o->reply_head + 1) % REPLY_CAPACITY; o->reply_count--; continue; }
        if (wrote < 0 && errno == EINTR) continue;
        if (wrote < 0 && (errno == EAGAIN || errno == EWOULDBLOCK) && !clock_failed && milliseconds() < o->reply_at[o->reply_head]) return;
        /* <=PIPE_BUF writes are atomic. Never resume a surprising partial frame. */
        close_owned(&o->response); o->reply_count = 0; fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_IO); return;
    }
}
static int discard_log_fragment(struct owner *o, size_t size)
{
    if ((uint64_t)size > UINT64_MAX - o->discarded) { fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_IO); return -1; }
    o->discarded += (uint64_t)size; o->log_gap = 1; return 0;
}
static int emit_log_fragment(struct owner *o, const unsigned char *bytes, size_t size)
{
    if (size == 0 || size > AUTOSOURCARR_SOURCE_LOG_PAYLOAD_MAXIMUM) { fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_IO); return -1; }
    if (o->generation == 0 || (uint64_t)size > UINT64_MAX - o->log_offset) {
        (void)discard_log_fragment(o, size); fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_IO); return -1;
    }
    unsigned char frame[AUTOSOURCARR_SOURCE_LOG_FRAME_MAXIMUM]; memset(frame, 0, sizeof(frame));
    memcpy(frame, log_magic, 8); memcpy(frame + 8, o->binding.boot_nonce, 32);
    put64(frame + 40, o->generation); put64(frame + 48, o->log_offset);
    put32(frame + 56, (uint32_t)size); put32(frame + 60, o->log_gap ? AUTOSOURCARR_SOURCE_LOG_GAP : 0);
    memcpy(frame + AUTOSOURCARR_SOURCE_LOG_HEADER_BYTES, bytes, size); o->log_offset += (uint64_t)size;
    if (o->response < 0 || o->reply_count != 0) {
        explicit_bzero(frame, sizeof(frame)); return discard_log_fragment(o, size);
    }
    size_t total = AUTOSOURCARR_SOURCE_LOG_HEADER_BYTES + size;
    ssize_t wrote = write(o->response, frame, total); int saved = errno;
    explicit_bzero(frame, sizeof(frame));
    if (wrote == (ssize_t)total) { o->log_gap = 0; return 0; }
    if (wrote < 0 && (saved == EAGAIN || saved == EWOULDBLOCK || saved == EINTR)) return discard_log_fragment(o, size);
    /* Never replay a surprising partial frame into the ownership channel. */
    (void)discard_log_fragment(o, size); close_owned(&o->response); o->reply_count = 0;
    fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_IO); return -1;
}
static void drain_log(struct owner *o)
{
    unsigned char bytes[8192];
    for (size_t round = 0; o->log_read >= 0 && round < 8; round++) {
        ssize_t count = read(o->log_read, bytes, sizeof(bytes));
        if (count > 0) {
            size_t at = 0;
            while (at < (size_t)count) {
                size_t size = (size_t)count - at;
                if (size > AUTOSOURCARR_SOURCE_LOG_PAYLOAD_MAXIMUM) size = AUTOSOURCARR_SOURCE_LOG_PAYLOAD_MAXIMUM;
                if (emit_log_fragment(o, bytes + at, size) != 0) {
                    size_t remaining = (size_t)count - at - size;
                    if (remaining != 0) {
                        if ((uint64_t)remaining > UINT64_MAX - o->log_offset) fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_IO);
                        else o->log_offset += (uint64_t)remaining;
                        (void)discard_log_fragment(o, remaining);
                    }
                    explicit_bzero(bytes, sizeof(bytes)); return;
                }
                at += size;
            }
            explicit_bzero(bytes, sizeof(bytes)); continue;
        }
        if (count == 0) { explicit_bzero(bytes, sizeof(bytes)); o->log_eof = 1; close_owned(&o->log_read); return; }
        if (errno == EINTR) { round--; continue; }
        if (errno == EAGAIN || errno == EWOULDBLOCK) { explicit_bzero(bytes, sizeof(bytes)); return; }
        explicit_bzero(bytes, sizeof(bytes)); close_owned(&o->log_read); fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_IO); return;
    }
    explicit_bzero(bytes, sizeof(bytes));
}
static void child_fail(int fd, int stage)
{
    unsigned char error[8]; put32(error, (uint32_t)stage); put32(error + 4, (uint32_t)errno);
    (void)write(fd, error, sizeof(error)); _exit(126);
}
static int place_cloexec(int from, int to)
{ return from == to ? fcntl(to, F_SETFD, FD_CLOEXEC) : dup3(from, to, O_CLOEXEC); }

static int observation_ticket_valid(const unsigned char *value,size_t count)
{
    if (count!=32) return 0;
    for (size_t i=0;i<count;i++) if (!(value[i]>='0'&&value[i]<='9') && !(value[i]>='a'&&value[i]<='f')) return 0;
    return 1;
}
static int observation_request(struct observation *observation,const unsigned char *payload,size_t length)
{
    if (length<16) return -1;
    uint32_t kind=get32(payload+8),count=get32(payload+12);
    if (count>AUTOSOURCARR_SOURCE_OBSERVATION_LEAF_MAXIMUM || length!=16U+count
        || (kind!=AUTOSOURCARR_OBSERVE_JAR && kind!=AUTOSOURCARR_OBSERVE_UA)) return -1;
    if (kind==AUTOSOURCARR_OBSERVE_UA) {if (count!=0) return -1;}
    else {
        if (count<5 || memcmp(payload+length-4,".jar",4)!=0) return -1;
        for (size_t i=16;i<length;i++) {
            unsigned char c=payload[i];
            if (!(c>='A'&&c<='Z') && !(c>='a'&&c<='z') && !(c>='0'&&c<='9') && c!='_' && c!='.' && c!='+' && c!='-') return -1;
        }
    }
    observation->kind=kind;observation->generation=get64(payload);
    memcpy(observation->leaf,payload+16,count);observation->leaf[count]=0;return 0;
}
static int observation_random_ticket(char ticket[33])
{
    static const char hex[]="0123456789abcdef";unsigned char random[16];
    ssize_t got=getrandom(random,sizeof(random),GRND_NONBLOCK);
    if (got!=(ssize_t)sizeof(random)) {explicit_bzero(random,sizeof(random));errno=EAGAIN;return -1;}
    for (size_t i=0;i<sizeof(random);i++) {ticket[2*i]=hex[random[i]>>4];ticket[2*i+1]=hex[random[i]&15U];}
    ticket[32]=0;explicit_bzero(random,sizeof(random));return 0;
}
static void observation_child(const struct owner *o,int output,int cleanup,pid_t expected)
{
    /* These are trusted single-threaded native fork children, never CLR execs.
     * Retain no command/proof/response/JVM stream descriptors. High duplicates
     * avoid aliasing originals before the final3/4/5 capability placement. */
    if (prctl(PR_GET_DUMPABLE,0L,0L,0L,0L)!=0
        || prctl(PR_SET_PDEATHSIG,SIGKILL,0L,0L,0L)!=0 || getppid()!=expected) _exit(125);
    int inputs[3]={o->root,o->observation_root,output},keep[3];
    for (size_t i=0;i<3;i++) {keep[i]=fcntl(inputs[i],F_DUPFD_CLOEXEC,20);if (keep[i]<0) _exit(125);}
    int neutral=open("/dev/null",O_RDWR|O_NOFOLLOW|O_CLOEXEC);
    struct stat null_status;
    if (neutral<0 || fstat(neutral,&null_status)!=0 || !S_ISCHR(null_status.st_mode)) _exit(125);
    for (int i=0;i<3;i++) if (dup2(neutral,i)<0) _exit(125);
    for (int i=0;i<3;i++) if (place_cloexec(keep[i],i+3)<0) _exit(125);
    if (syscall(SYS_close_range,6U,~0U,0)!=0) _exit(125);
    struct autosourcarr_source_observation_config config={
        .root_fd=3,.exchange_fd=4,.expected_parent=expected,.uid=o->binding.uid,.gid=o->binding.gid,
        .kind=o->observation.kind,.root_device=o->binding.root_device,.root_inode=o->binding.root_inode,
        .exchange_device=o->binding.observation_root_device,.exchange_inode=o->binding.observation_root_inode,
        .leaf=o->observation.leaf,.ticket=o->observation.ticket};
    unsigned char bytes[AUTOSOURCARR_SOURCE_OBSERVATION_RESULT_BYTES];size_t size;
    if (cleanup) {
        if (close(3)!=0) _exit(125);config.root_fd=-1;
        struct autosourcarr_source_observation_cleanup_result result;
        int okay=autosourcarr_source_observation_remove(&config,o->observation.result.output_device,o->observation.result.output_inode,&result);
        if (okay!=0) {memset(&result,0,sizeof(result));result.outcome=AUTOSOURCARR_OBSERVATION_FAILED;result.cleanup=2;result.error=(uint32_t)(errno?errno:EIO);}
        size=sizeof(result);memcpy(bytes,&result,size);explicit_bzero(&result,sizeof(result));
    } else {
        struct autosourcarr_source_observation_result result;
        int okay=autosourcarr_source_observation_capture(&config,&result);
        if (okay!=0) {memset(&result,0,sizeof(result));result.outcome=AUTOSOURCARR_OBSERVATION_FAILED;result.kind=config.kind;result.error=(uint32_t)(errno?errno:EIO);}
        size=sizeof(result);memcpy(bytes,&result,size);explicit_bzero(&result,sizeof(result));
    }
    ssize_t wrote;do {wrote=write(5,bytes,size);} while (wrote<0 && errno==EINTR);
    explicit_bzero(bytes,sizeof(bytes));
    int okay=wrote==(ssize_t)size;
    if (close(4)!=0) okay=0;if (!cleanup && close(3)!=0) okay=0;if (close(5)!=0) okay=0;
    _exit(okay?0:125);
}
static int observation_spawn(struct owner *o,int cleanup)
{
    int pipes[2]={-1,-1};
    if (pipe2(pipes,O_CLOEXEC)!=0 || nonblocking(pipes[0])!=0) goto failed;
    pid_t expected=getpid(),child=fork();
    if (child<0) goto failed;
    if (child==0) observation_child(o,pipes[1],cleanup,expected);
    struct observation *v=&o->observation;
    v->pid=child;v->read=pipes[0];pipes[0]=-1;v->eof=0;v->reaped=0;v->status=0;v->expired=0;v->invalid=0;
    v->used=0;v->wanted=cleanup?sizeof(struct autosourcarr_source_observation_cleanup_result):sizeof(struct autosourcarr_source_observation_result);
    memset(v->incoming,0,sizeof(v->incoming));v->deadline=milliseconds()+OBSERVATION_BUDGET_MS;
    v->phase=cleanup?OBSERVATION_CLEANING:OBSERVATION_CAPTURING;o->no_children=0;
    close_owned(&pipes[1]);return 0;
failed:
    close_owned(&pipes[0]);close_owned(&pipes[1]);return -1;
}
static int observation_result_valid(const struct owner *o,const struct autosourcarr_source_observation_result *r)
{
    const struct observation *v=&o->observation;
    uint64_t maximum=v->kind==AUTOSOURCARR_OBSERVE_JAR?AUTOSOURCARR_SOURCE_OBSERVATION_JAR_MAXIMUM:AUTOSOURCARR_SOURCE_OBSERVATION_UA_MAXIMUM;
    if (r->kind!=v->kind || r->reserved!=0 || r->cleanup>2 || r->outcome<1 || r->outcome>4) return 0;
    if (r->outcome==AUTOSOURCARR_OBSERVATION_COMPLETE) {
        return r->facts==15 && r->error==0 && r->cleanup==0 && r->length<=maximum
            && (v->kind!=AUTOSOURCARR_OBSERVE_JAR || r->length!=0)
            && r->root_device==o->binding.root_device && r->root_inode==o->binding.root_inode
            && r->exchange_device==o->binding.observation_root_device && r->exchange_inode==o->binding.observation_root_inode
            && r->output_inode!=0 && r->source_inode!=0 && r->directory_inode!=0
            && r->output_uid==o->binding.uid && r->output_gid==o->binding.gid && r->output_mode==(S_IFREG|0444) && r->output_links==1
            && S_ISREG(r->source_mode) && (r->source_mode&~0xffffU)==0 && r->source_links==1;
    }
    unsigned char digest=0;for (size_t i=0;i<32;i++) digest|=r->sha256[i];
    if (digest!=0 || r->length!=0 || r->source_device!=0 || r->source_inode!=0 || r->directory_device!=0 || r->directory_inode!=0
        || r->output_uid!=0 || r->output_gid!=0 || r->output_mode!=0 || r->output_links!=0
        || r->source_uid!=0 || r->source_gid!=0 || r->source_mode!=0 || r->source_links!=0) return 0;
    if (r->outcome==AUTOSOURCARR_OBSERVATION_MISSING)
        return r->facts==6 && r->error==0 && r->cleanup==0 && r->output_device==0 && r->output_inode==0
            && r->root_device==o->binding.root_device && r->root_inode==o->binding.root_inode
            && r->exchange_device==o->binding.observation_root_device && r->exchange_inode==o->binding.observation_root_inode;
    return r->facts==0 && r->error>0 && r->error<=4095 && r->root_device==0 && r->root_inode==0
        && r->exchange_device==0 && r->exchange_inode==0 && (r->output_inode!=0 || r->output_device==0)
        && (r->cleanup!=0 || (r->output_device==0 && r->output_inode==0));
}
static void observation_failed_result(struct observation *v,int error,int retain_known)
{
    uint64_t device=retain_known?v->result.output_device:0,inode=retain_known?v->result.output_inode:0;
    memset(&v->result,0,sizeof(v->result));v->result.kind=v->kind;v->result.outcome=AUTOSOURCARR_OBSERVATION_FAILED;
    v->result.error=(uint32_t)error;v->result.cleanup=2;v->result.output_device=device;v->result.output_inode=inode;
}
static void observation_finish(struct owner *o)
{
    struct observation *v=&o->observation;
    if ((v->phase!=OBSERVATION_CAPTURING && v->phase!=OBSERVATION_CLEANING) || !v->reaped || !v->eof) return;
    int normal=WIFEXITED(v->status) && WEXITSTATUS(v->status)==0;
    if (v->phase==OBSERVATION_CAPTURING) {
        int valid=v->used==sizeof(v->result) && !v->invalid;
        if (valid) {memcpy(&v->result,v->incoming,sizeof(v->result));valid=observation_result_valid(o,&v->result);}
        if (!valid) {v->invalid=1;observation_failed_result(v,EIO,0);}
        else if (!normal || v->expired) observation_failed_result(v,v->expired?ETIMEDOUT:EIO,1);
        v->phase=OBSERVATION_ISSUED;v->event_pending=1;v->event_deadline=milliseconds()+IO_BUDGET_MS;
    } else {
        struct autosourcarr_source_observation_cleanup_result r;memset(&r,0,sizeof(r));
        if (v->used==sizeof(r) && !v->invalid) memcpy(&r,v->incoming,sizeof(r));
        int okay=normal && !v->expired && !v->invalid && v->used==sizeof(r) && r.outcome==AUTOSOURCARR_OBSERVATION_COMPLETE
            && r.cleanup==1 && r.error==0 && r.reserved==0 && r.output_device==v->result.output_device && r.output_inode==v->result.output_inode;
        uint64_t request=v->release_request;int automatic=v->automatic_cleanup;
        if (okay) {explicit_bzero(v,sizeof(*v));v->read=-1;v->eof=1;v->reaped=1;}
        else {v->phase=OBSERVATION_BLOCKED;v->pid=0;v->release_request=0;if (automatic) fail_owner(o,AUTOSOURCARR_SOURCE_DIAG_IO);}
        if (request!=0) queue_reply(o,request,AUTOSOURCARR_SOURCE_RELEASE_SNAPSHOT,okay?AUTOSOURCARR_SOURCE_OK:AUTOSOURCARR_SOURCE_FAILED,okay?AUTOSOURCARR_SOURCE_DIAG_NONE:AUTOSOURCARR_SOURCE_DIAG_IO);
        explicit_bzero(&r,sizeof(r));
    }
    explicit_bzero(v->incoming,sizeof(v->incoming));
}
static void pump_observation(struct owner *o)
{
    struct observation *v=&o->observation;unsigned char extra[256];
    for (size_t round=0;v->read>=0 && round<8;round++) {
        void *target=v->used<v->wanted?v->incoming+v->used:extra;
        size_t room=v->used<v->wanted?v->wanted-v->used:sizeof(extra);
        ssize_t got=read(v->read,target,room);
        if (got>0) {if (v->used<v->wanted) v->used+=(size_t)got;else v->invalid=1;continue;}
        if (got==0) {v->eof=1;close_owned(&v->read);break;}
        if (errno==EINTR) {round--;continue;}
        if (errno!=EAGAIN && errno!=EWOULDBLOCK) {fail_owner(o,AUTOSOURCARR_SOURCE_DIAG_IO);}
        break;
    }
    explicit_bzero(extra,sizeof(extra));
}
static void flush_observation(struct owner *o)
{
    struct observation *v=&o->observation;
    if (!v->event_pending || o->reply_count!=0 || o->response<0) return;
    unsigned char frame[AUTOSOURCARR_SOURCE_OBSERVATION_EVENT_BYTES];memset(frame,0,sizeof(frame));
    memcpy(frame,observation_magic,8);memcpy(frame+8,o->binding.boot_nonce,32);put64(frame+40,v->request);put64(frame+48,v->generation);
    put32(frame+56,(uint32_t)v->pid);
    uint32_t physical=(v->reaped?AUTOSOURCARR_SOURCE_OBSERVER_REAPED:0U)|(v->eof?AUTOSOURCARR_SOURCE_OBSERVER_EOF:0U)
        |(WIFEXITED(v->status)&&WEXITSTATUS(v->status)==0?AUTOSOURCARR_SOURCE_OBSERVER_EXIT_ZERO:0U)
        |(v->expired?AUTOSOURCARR_SOURCE_OBSERVER_TIMEOUT:0U)|(v->invalid?AUTOSOURCARR_SOURCE_OBSERVER_INVALID:0U);
    put32(frame+60,physical);put32(frame+64,(uint32_t)v->status);memcpy(frame+72,v->ticket,32);memcpy(frame+104,&v->result,sizeof(v->result));
    ssize_t wrote=write(o->response,frame,sizeof(frame));int saved=errno;explicit_bzero(frame,sizeof(frame));
    if (wrote==(ssize_t)sizeof(frame)) {
        v->event_pending=0;queue_reply(o,v->request,AUTOSOURCARR_SOURCE_OBSERVE,AUTOSOURCARR_SOURCE_OK,AUTOSOURCARR_SOURCE_DIAG_NONE);return;
    }
    if (wrote<0 && (saved==EINTR||saved==EAGAIN||saved==EWOULDBLOCK) && !clock_failed && milliseconds()<v->event_deadline) return;
    close_owned(&o->response);o->reply_count=0;v->event_pending=0;fail_owner(o,AUTOSOURCARR_SOURCE_DIAG_IO);
}
static void physical_child(void)
{
    /* Real escaped-group descendant: only the source subreaper can reconcile it
     * after its wrapper exits. No disk write, external command or network. */
    pid_t child = fork();
    if (child < 0) _exit(124);
    if (child == 0) {
        if (setsid() < 0) _exit(123);
        struct sigaction ignored; memset(&ignored, 0, sizeof(ignored)); ignored.sa_handler = SIG_IGN;
        if (sigaction(SIGTERM, &ignored, NULL) != 0) _exit(122);
        const char marker[] = "actual-source-owner-adopted-child\n";
        (void)write(STDOUT_FILENO, marker, sizeof(marker) - 1);
        for (;;) pause();
    }
    _exit(0);
}
static int spawn(struct owner *o, int selftest, const unsigned char *payload, size_t size)
{
    int asset = -1, logs[2] = {-1, -1}, execs[2] = {-1, -1};
    struct launch_environment environment; memset(&environment, 0, sizeof(environment));
    if (!selftest) {
        asset = trusted_asset(AUTOSOURCARR_SOURCE_LAUNCHER, 1);
        if (asset < 0 || environment_parse(payload, size, &environment) != 0) goto failed;
    }
    if (pipe2(logs, O_CLOEXEC) != 0 || pipe2(execs, O_CLOEXEC) != 0
        || nonblocking(logs[0]) != 0 || nonblocking(execs[0]) != 0) goto failed;
    pid_t expected = getpid(), child = fork();
    if (child < 0) goto failed;
    if (child == 0) {
        if (dup2(logs[1], STDOUT_FILENO) < 0 || dup2(logs[1], STDERR_FILENO) < 0) child_fail(execs[1], 1);
        int null_input = open("/dev/null", O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
        if (null_input < 0 || dup2(null_input, STDIN_FILENO) < 0 || dup2(o->root, 5) < 0
            || place_cloexec(execs[1], 7) < 0 || (!selftest && place_cloexec(asset, 6) < 0)) child_fail(execs[1], 2);
        if (setsid() < 0 || fchdir(5) != 0 || prctl(PR_SET_PDEATHSIG, SIGKILL, 0L, 0L, 0L) != 0 || getppid() != expected) child_fail(7, 3);
        if (syscall(SYS_close_range, 3U, 4U, 0) != 0 || syscall(SYS_close_range, 8U, ~0U, 0) != 0) child_fail(7, 4);
        if (selftest) { (void)close(6); (void)close(7); physical_child(); }
        char *const args[] = { AUTOSOURCARR_SOURCE_LAUNCHER, "--native-source-root-fd=5", NULL };
        (void)syscall(SYS_execveat, 6, "", args, environment.items, AT_EMPTY_PATH);
        child_fail(7, 5);
    }
    o->leader = child; o->pgid = child; o->leader_reaped = 0; o->no_children = 0; o->exit_known = 0; o->exit_status = 0;
    o->launch_selftest = selftest; o->log_eof = 0; o->exec_eof = 0; o->killed_reaped = 0;
    o->generation++; o->log_offset = 0; o->log_gap = 0;
    o->log_read = logs[0]; logs[0] = -1; o->exec_read = execs[0]; execs[0] = -1;
    o->exec_error_size = 0; o->exec_at = milliseconds() + IO_BUDGET_MS; o->state = AUTOSOURCARR_SOURCE_STARTING;
    close_owned(&logs[1]); close_owned(&execs[1]); if (asset >= 0) (void)close(asset); environment_clear(&environment); return 0;
failed:
    close_owned(&logs[0]); close_owned(&logs[1]); close_owned(&execs[0]); close_owned(&execs[1]);
    if (asset >= 0) (void)close(asset); environment_clear(&environment); return -1;
}

static void pump_exec(struct owner *o)
{
    if (o->exec_read < 0) return;
    unsigned char extra[16];
    ssize_t count = read(o->exec_read, extra, sizeof(extra));
    if (count > 0) {
        if ((size_t)count > sizeof(o->exec_error) - o->exec_error_size) { fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_EXEC); return; }
        memcpy(o->exec_error + o->exec_error_size, extra, (size_t)count); o->exec_error_size += (size_t)count;
    } else if (count == 0) {
        o->exec_eof = 1;
        close_owned(&o->exec_read);
        if (o->exec_error_size != 0) { begin_stop(o); return; }
        if (o->state == AUTOSOURCARR_SOURCE_STARTING) {
            o->state = AUTOSOURCARR_SOURCE_RUNNING;
            queue_reply(o, o->start_request, o->start_opcode, AUTOSOURCARR_SOURCE_OK, AUTOSOURCARR_SOURCE_DIAG_NONE); o->start_request = 0;
        }
    } else if (errno != EINTR && errno != EAGAIN && errno != EWOULDBLOCK) {
        close_owned(&o->exec_read); fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_IO);
    }
    if (o->exec_read >= 0 && (clock_failed || milliseconds() >= o->exec_at)) { close_owned(&o->exec_read); fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_EXEC); }
}
static int child_list(pid_t *children, size_t *count)
{
    char path[80], bytes[CHILD_LIST_MAXIMUM + 1]; size_t used = 0; int valid = 1;
    *count = 0;
    int length = snprintf(path, sizeof(path), "/proc/self/task/%ld/children", (long)getpid());
    if (length <= 0 || (size_t)length >= sizeof(path)) return -1;
    int fd = open(path, O_RDONLY | O_CLOEXEC); if (fd < 0) return -1;
    while (used < CHILD_LIST_MAXIMUM) {
        ssize_t amount = read(fd, bytes + used, CHILD_LIST_MAXIMUM - used);
        if (amount > 0) { used += (size_t)amount; continue; }
        if (amount < 0 && errno == EINTR) continue;
        if (amount < 0) valid = 0;
        break;
    }
    (void)close(fd); bytes[used] = 0;
    if (used == CHILD_LIST_MAXIMUM) {
        valid = 0;
        while (used != 0 && bytes[used - 1] != ' ') used--;
        bytes[used] = 0; /* Still signal/reap the complete bounded prefix. */
    }
    char *cursor = bytes;
    while (*cursor != 0) {
        while (*cursor == ' ' || *cursor == '\n' || *cursor == '\t') cursor++;
        if (*cursor == 0) break;
        errno = 0; char *end; long child = strtol(cursor, &end, 10);
        if (end == cursor || errno != 0 || child <= 0 || child > INT_MAX || (*end != 0 && *end != ' ' && *end != '\n' && *end != '\t') || *count == 2048) return -1;
        children[(*count)++] = (pid_t)child; cursor = end;
    }
    return valid ? 0 : -1;
}
static void count_reap(struct owner *o, pid_t child, int status)
{
    struct observation *v=&o->observation;
    if ((v->phase==OBSERVATION_CAPTURING || v->phase==OBSERVATION_CLEANING) && !v->reaped && child==v->pid) {
        v->reaped=1;v->status=status;return; /* Not an adopted JVM descendant. */
    }
    if (o->reaped != UINT32_MAX) o->reaped++;
    if (child != o->leader && o->adopted != UINT32_MAX) o->adopted++;
    if (WIFSIGNALED(status) && WTERMSIG(status) == SIGKILL) o->killed_reaped = 1;
}
static void reap(struct owner *o)
{
    if (!o->leader_reaped) {
        pid_t children[2048]; size_t count = 0; int other_live = 0;
        if (child_list(children, &count) != 0) fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_WAIT);
        for (size_t i = 0; i < count; i++) {
            if (children[i] == o->leader) {
                siginfo_t info; memset(&info, 0, sizeof(info));
                if (waitid(P_PID, (id_t)o->leader, &info, WEXITED | WNOHANG | WNOWAIT) != 0) { if (errno != EINTR) fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_WAIT); continue; }
                if (info.si_pid != 0) {
                    o->exit_known = 1; o->exit_status = info.si_code == CLD_EXITED ? info.si_status : -info.si_status;
                    if (o->state == AUTOSOURCARR_SOURCE_RUNNING || o->state == AUTOSOURCARR_SOURCE_STARTING) begin_stop(o);
                }
                continue;
            }
            int status; pid_t child;
            do { child = waitpid(children[i], &status, WNOHANG); } while (child < 0 && errno == EINTR);
            if (child > 0) count_reap(o, child, status);
            else if (child == 0) other_live = 1;
            else fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_WAIT);
        }
        /* Keep the leader waitable (and its PID reserved) while its group may
         * exist, avoiding a stale/reused killpg target after wrapper exit. */
        if (o->exit_known && !other_live && o->log_read < 0 && o->exec_read < 0) {
            int status; pid_t child;
            do { child = waitpid(o->leader, &status, WNOHANG); } while (child < 0 && errno == EINTR);
            if (child == o->leader) { count_reap(o, child, status); o->leader_reaped = 1; }
            else if (child < 0) fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_WAIT);
        }
        if (!o->leader_reaped) { o->no_children = 0; return; }
    }
    for (;;) {
        int status; pid_t child = waitpid(-1, &status, WNOHANG);
        if (child > 0) { count_reap(o, child, status); continue; }
        if (child == 0) { o->no_children = 0; return; }
        if (errno == EINTR) continue;
        if (errno == ECHILD) { o->no_children = 1; return; }
        o->no_children = 0; fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_WAIT); return;
    }
}
static int signal_child(pid_t child, int number)
{
    /* No competing wait thread/SIGCHLD reaper exists. WNOWAIT establishes this
     * is still our actual direct/adopted child before a PID handle is used. */
    siginfo_t information; memset(&information, 0, sizeof(information));
    if (waitid(P_PID, (id_t)child, &information, WEXITED | WNOHANG | WNOWAIT) != 0) return errno == ECHILD ? 0 : -1;
    if (information.si_pid != 0) return 0;
    int handle = (int)syscall(SYS_pidfd_open, child, 0U);
    if (handle < 0) return errno == ESRCH ? 0 : -1;
    int result = (int)syscall(SYS_pidfd_send_signal, handle, number, NULL, 0U);
    int saved = errno; (void)close(handle); errno = saved;
    return result == 0 || errno == ESRCH ? 0 : -1;
}
static void observation_tick(struct owner *o)
{
    struct observation *v=&o->observation;
    if ((v->phase!=OBSERVATION_CAPTURING && v->phase!=OBSERVATION_CLEANING) || v->reaped) return;
    if (clock_failed || milliseconds()>=v->deadline) v->expired=1;
    if ((v->expired || v->invalid) && signal_child((pid_t)v->pid,SIGKILL)!=0) fail_owner(o,AUTOSOURCARR_SOURCE_DIAG_SIGNAL);
}
static void observation_shutdown_cleanup(struct owner *o)
{
    struct observation *v=&o->observation;
    if (!o->shutdown || v->event_pending || v->automatic_cleanup
        || (v->phase!=OBSERVATION_ISSUED && v->phase!=OBSERVATION_BLOCKED)) return;
    v->automatic_cleanup=1;v->release_request=0;
    if (observation_spawn(o,1)!=0) {v->phase=OBSERVATION_BLOCKED;fail_owner(o,AUTOSOURCARR_SOURCE_DIAG_IO);}
}
static int observation_drained(const struct owner *o)
{
    const struct observation *v=&o->observation;
    return v->read<0 && v->eof && !v->event_pending && v->phase!=OBSERVATION_CAPTURING && v->phase!=OBSERVATION_CLEANING;
}
static void signal_descendants(struct owner *o)
{
    int number = !clock_failed && milliseconds() < o->stop_at ? SIGTERM : SIGKILL;
    if (!o->leader_reaped && o->pgid > 0 && kill(-o->pgid, number) != 0 && errno != ESRCH) fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_SIGNAL);
    pid_t children[2048]; size_t count = 0;
    if (child_list(children, &count) != 0) fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_WAIT);
    for (size_t i = 0; i < count; i++) {
        /* A trusted cleanup admitted by shutdown owns its own finite timeout.
         * Do not immediately SIGTERM the very work needed to reconcile it. */
        if (o->observation.phase==OBSERVATION_CLEANING && !o->observation.reaped && children[i]==o->observation.pid) continue;
        if (signal_child(children[i], number) != 0) fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_SIGNAL);
    }
}

static void dispatch(struct owner *o)
{
    const unsigned char *header = o->incoming, *payload = header + AUTOSOURCARR_SOURCE_COMMAND_BYTES;
    uint64_t request = get64(header + 40); uint16_t opcode = get16(header + 48); size_t length = get32(header + 52);
    unsigned char different = 0; for (size_t i = 0; i < 32; i++) different |= (unsigned char)(header[8 + i] ^ o->binding.boot_nonce[i]);
    if (memcmp(header, command_magic, 8) != 0 || different != 0 || get16(header + 50) != 0
        || o->last_request == UINT64_MAX || request != o->last_request + 1U) { fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_PROTOCOL); return; }
    o->last_request = request;
    if (opcode != AUTOSOURCARR_SOURCE_START && opcode != AUTOSOURCARR_SOURCE_OBSERVE
        && opcode != AUTOSOURCARR_SOURCE_RELEASE_SNAPSHOT && length != 0) { fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_PROTOCOL); return; }
    if (opcode == AUTOSOURCARR_SOURCE_STATUS) { queue_reply(o, request, opcode, AUTOSOURCARR_SOURCE_OK, o->fault); return; }
    if (opcode == AUTOSOURCARR_SOURCE_OBSERVE) {
        struct observation candidate;memset(&candidate,0,sizeof(candidate));candidate.read=-1;candidate.eof=1;candidate.reaped=1;
        if (observation_request(&candidate,payload,length)!=0) {fail_owner(o,AUTOSOURCARR_SOURCE_DIAG_PROTOCOL);return;}
        if (o->physical_test || o->observation_root<0) {queue_reply(o,request,opcode,AUTOSOURCARR_SOURCE_UNAVAILABLE,AUTOSOURCARR_SOURCE_DIAG_ASSET);return;}
        if (o->shutdown || o->fault || o->observation.phase!=OBSERVATION_NONE
            || (o->state!=AUTOSOURCARR_SOURCE_IDLE && o->state!=AUTOSOURCARR_SOURCE_RUNNING)
            || candidate.generation!=o->generation
            || (candidate.kind==AUTOSOURCARR_OBSERVE_JAR && (o->state!=AUTOSOURCARR_SOURCE_RUNNING || o->generation==0))) {
            queue_reply(o,request,opcode,AUTOSOURCARR_SOURCE_BUSY,AUTOSOURCARR_SOURCE_DIAG_NONE);return;
        }
        if (root_current(o)!=0 || !same_observation_root(o->observation_root,&o->binding)
            || observation_random_ticket(candidate.ticket)!=0) {
            queue_reply(o,request,opcode,AUTOSOURCARR_SOURCE_UNAVAILABLE,AUTOSOURCARR_SOURCE_DIAG_IO);return;
        }
        candidate.request=request;o->observation=candidate;
        if (observation_spawn(o,0)!=0) {
            explicit_bzero(&o->observation,sizeof(o->observation));o->observation.read=-1;o->observation.eof=1;o->observation.reaped=1;
            queue_reply(o,request,opcode,AUTOSOURCARR_SOURCE_UNAVAILABLE,AUTOSOURCARR_SOURCE_DIAG_IO);
        }
        return;
    }
    if (opcode == AUTOSOURCARR_SOURCE_RELEASE_SNAPSHOT) {
        if (!observation_ticket_valid(payload,length)) {fail_owner(o,AUTOSOURCARR_SOURCE_DIAG_PROTOCOL);return;}
        struct observation *v=&o->observation;
        if ((v->phase!=OBSERVATION_ISSUED && v->phase!=OBSERVATION_BLOCKED) || v->event_pending
            || memcmp(v->ticket,payload,32)!=0 || o->physical_test) {
            queue_reply(o,request,opcode,AUTOSOURCARR_SOURCE_BUSY,AUTOSOURCARR_SOURCE_DIAG_NONE);return;
        }
        v->release_request=request;v->automatic_cleanup=0;
        if (observation_spawn(o,1)!=0) {v->release_request=0;queue_reply(o,request,opcode,AUTOSOURCARR_SOURCE_UNAVAILABLE,AUTOSOURCARR_SOURCE_DIAG_IO);}
        return;
    }
    if (opcode == AUTOSOURCARR_SOURCE_STOP || opcode == AUTOSOURCARR_SOURCE_SHUTDOWN) {
        if (o->stop_request != 0) { queue_reply(o, request, opcode, AUTOSOURCARR_SOURCE_BUSY, o->fault); return; }
        o->stop_request = request; o->stop_opcode = opcode;
        if (opcode == AUTOSOURCARR_SOURCE_SHUTDOWN) o->shutdown = 1;
        begin_stop(o); return;
    }
    if (opcode == AUTOSOURCARR_SOURCE_START || opcode == AUTOSOURCARR_SOURCE_PHYSICAL_SELFTEST) {
        if (o->shutdown || o->fault || o->state != AUTOSOURCARR_SOURCE_IDLE || !o->no_children || o->log_read >= 0 || o->exec_read >= 0
            || o->observation.phase!=OBSERVATION_NONE) {
            queue_reply(o, request, opcode, AUTOSOURCARR_SOURCE_BUSY, o->fault); return;
        }
        if (o->physical_test && opcode == AUTOSOURCARR_SOURCE_START) { queue_reply(o, request, opcode, AUTOSOURCARR_SOURCE_UNAVAILABLE, AUTOSOURCARR_SOURCE_DIAG_ASSET); return; }
        if (root_current(o) != 0) { fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_ROOT); return; }
        o->start_request = request; o->start_opcode = opcode;
        if (spawn(o, opcode == AUTOSOURCARR_SOURCE_PHYSICAL_SELFTEST, payload, length) != 0) {
            o->start_request = 0; queue_reply(o, request, opcode, AUTOSOURCARR_SOURCE_UNAVAILABLE, AUTOSOURCARR_SOURCE_DIAG_ASSET);
        }
        return;
    }
    queue_reply(o, request, opcode, AUTOSOURCARR_SOURCE_PROTOCOL_ERROR, AUTOSOURCARR_SOURCE_DIAG_PROTOCOL);
    fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_PROTOCOL);
}
static void pump_commands(struct owner *o)
{
    if (o->control < 0 || o->shutdown) return;
    for (size_t round = 0; round < 4; round++) {
        ssize_t count = read(o->control, o->incoming + o->incoming_size, o->incoming_wanted - o->incoming_size);
        if (count > 0) {
            if (o->incoming_size == 0) o->command_at = milliseconds() + IO_BUDGET_MS;
            o->incoming_size += (size_t)count;
            if (o->incoming_size == AUTOSOURCARR_SOURCE_COMMAND_BYTES && o->incoming_wanted == AUTOSOURCARR_SOURCE_COMMAND_BYTES) {
                size_t length = get32(o->incoming + 52);
                if (length > AUTOSOURCARR_SOURCE_PAYLOAD_MAXIMUM) { fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_PROTOCOL); return; }
                o->incoming_wanted += length;
            }
            if (o->incoming_size == o->incoming_wanted) {
                dispatch(o); explicit_bzero(o->incoming, o->incoming_size); o->incoming_size = 0;
                o->incoming_wanted = AUTOSOURCARR_SOURCE_COMMAND_BYTES;
                if (o->shutdown) return;
            }
            continue;
        }
        if (count == 0) {
            close_owned(&o->control); o->shutdown = 1;
            if (o->incoming_size != 0) fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_PROTOCOL); else begin_stop(o);
            return;
        }
        if (errno == EINTR) { round--; continue; }
        if (errno != EAGAIN && errno != EWOULDBLOCK) { close_owned(&o->control); fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_IO); }
        break;
    }
    if (o->incoming_size != 0 && milliseconds() >= o->command_at) fail_owner(o, AUTOSOURCARR_SOURCE_DIAG_PROTOCOL);
}
static int fifo(int fd, int access, struct stat *status)
{
    int flags = fcntl(fd, F_GETFL);
    return flags >= 0 && (flags & O_ACCMODE) == access && fstat(fd, status) == 0 && S_ISFIFO(status->st_mode);
}
static int proof(int fd, const struct owner *o)
{
    unsigned char bytes[AUTOSOURCARR_SOURCE_PROOF_BYTES]; memset(bytes, 0, sizeof(bytes));
    memcpy(bytes, proof_magic, 8); memcpy(bytes + 8, o->binding.boot_nonce, 32);
    put32(bytes + 40, (uint32_t)getpid()); put32(bytes + 44, (uint32_t)getppid()); put32(bytes + 48, o->binding.uid); put32(bytes + 52, o->binding.gid);
    put64(bytes + 56, o->binding.root_device); put64(bytes + 64, o->binding.root_inode); put32(bytes + 72, (uint32_t)o->physical_test);
    uint64_t deadline = milliseconds() + IO_BUDGET_MS;
    for (;;) {
        ssize_t count = write(fd, bytes, sizeof(bytes));
        if (count == (ssize_t)sizeof(bytes)) return 0;
        if (count < 0 && errno == EINTR) continue;
        if (count < 0 && (errno == EAGAIN || errno == EWOULDBLOCK) && !clock_failed && milliseconds() < deadline) {
            struct pollfd wait = { .fd = fd, .events = POLLOUT }; (void)poll(&wait, 1, 50); continue;
        }
        return -1;
    }
}
static int run(const struct autosourcarr_source_owner_config *config, int physical_test)
{
    if (config == NULL || config->expected_parent <= 0 || config->uid == 0 || config->uid == UINT32_MAX || config->gid == 0 || config->gid == UINT32_MAX) return 78;
    struct stat input, output, proof_status;
    size_t descriptor_count = physical_test ? 4U : 5U;
    int original[5] = {config->root_fd, config->control_read_fd, config->response_write_fd, config->proof_write_fd,
        config->observation_root_fd};
    for (size_t i = 0; i < descriptor_count; i++) {
        if (original[i] < 3) return 78;
        for (size_t j = 0; j < i; j++) if (original[i] == original[j]) return 78;
    }
    if (!fifo(config->control_read_fd, O_RDONLY, &input) || !fifo(config->response_write_fd, O_WRONLY, &output)
        || !fifo(config->proof_write_fd, O_WRONLY, &proof_status)
        || (input.st_dev == output.st_dev && input.st_ino == output.st_ino)
        || (input.st_dev == proof_status.st_dev && input.st_ino == proof_status.st_ino)
        || (output.st_dev == proof_status.st_dev && output.st_ino == proof_status.st_ino)
        || !same_root(config->root_fd, config)
        || (!physical_test && !same_observation_root(config->observation_root_fd, config))
        || getppid() != config->expected_parent) return 78;
    int owned[5] = {-1, -1, -1, -1, -1};
    for (size_t i = 0; i < descriptor_count; i++) {
        owned[i] = fcntl(original[i], F_DUPFD_CLOEXEC, 10);
        if (owned[i] < 0) { for (size_t j = 0; j < i; j++) (void)close(owned[j]); return 78; }
    }
    /* Monotonically allocated duplicates; close all gaps with bounded syscalls
     * even if the original descriptor numbers were unusually large. */
    unsigned int first = 3;
    for (size_t i = 0; i < descriptor_count; i++) {
        unsigned int keep = (unsigned int)owned[i];
        if (first < keep && syscall(SYS_close_range, first, keep - 1U, 0) != 0) goto failed;
        first = keep + 1U;
    }
    if (syscall(SYS_close_range, first, ~0U, 0) != 0) goto failed;
    struct owner o; memset(&o, 0, sizeof(o)); o.binding = *config; o.physical_test = physical_test;
    o.root = owned[0]; o.control = owned[1]; o.response = owned[2]; o.log_read = -1; o.exec_read = -1;
    o.observation_root = physical_test ? -1 : owned[4];
    o.observation.read=-1;o.observation.eof=1;o.observation.reaped=1;
    o.log_eof = 1; o.exec_eof = 1; o.leader_reaped = 1;
    o.incoming_wanted = AUTOSOURCARR_SOURCE_COMMAND_BYTES;
    termination_requested = 0; clock_failed = 0;
    (void)milliseconds(); if (clock_failed) goto failed;
    struct sigaction stop, ignored; memset(&stop, 0, sizeof(stop)); memset(&ignored, 0, sizeof(ignored));
    stop.sa_handler = signal_stop; ignored.sa_handler = SIG_IGN;
    if (sigaction(SIGTERM, &stop, NULL) != 0 || sigaction(SIGINT, &stop, NULL) != 0 || sigaction(SIGPIPE, &ignored, NULL) != 0) goto failed;
    struct sigaction child_default; memset(&child_default, 0, sizeof(child_default)); child_default.sa_handler = SIG_DFL;
    if (sigaction(SIGCHLD, &child_default, NULL) != 0
        || autosourcarr_drop_identity(config->uid, config->gid, NULL, 0, 1) != 0
        || discard_inherited_environment() != 0
        || (uint32_t)setfsuid((uid_t)-1) != config->uid || (uint32_t)setfsgid((gid_t)-1) != config->gid
        || prctl(PR_SET_CHILD_SUBREAPER, 1L, 0L, 0L, 0L) != 0
        || prctl(PR_SET_PDEATHSIG, SIGKILL, 0L, 0L, 0L) != 0 || getppid() != config->expected_parent
        || nonblocking(o.control) != 0 || nonblocking(o.response) != 0 || nonblocking(owned[3]) != 0
        || root_current(&o) != 0
        || (!physical_test && !same_observation_root(o.observation_root, config))) goto failed;
    int subreaper = 0, death_signal = 0;
    pid_t initial_children[2048]; size_t initial_count = 0;
    if (prctl(PR_GET_CHILD_SUBREAPER, &subreaper, 0L, 0L, 0L) != 0 || subreaper != 1
        || prctl(PR_GET_PDEATHSIG, &death_signal, 0L, 0L, 0L) != 0 || death_signal != SIGKILL
        || child_list(initial_children, &initial_count) != 0 || initial_count != 0
        || proof(owned[3], &o) != 0) goto failed;
    close_owned(&owned[3]);
    for (;;) {
        if (clock_failed) fail_owner(&o, AUTOSOURCARR_SOURCE_DIAG_IO);
        if (termination_requested) { o.shutdown = 1; begin_stop(&o); }
        if (o.state != AUTOSOURCARR_SOURCE_IDLE && o.state != AUTOSOURCARR_SOURCE_FAULTED
            && root_current(&o) != 0) fail_owner(&o, AUTOSOURCARR_SOURCE_DIAG_ROOT);
        pump_exec(&o);pump_observation(&o);reap(&o);observation_finish(&o);observation_tick(&o);pump_commands(&o);
        /* Bound control latency before forwarding untrusted process output. */
        flush_replies(&o);flush_observation(&o);observation_shutdown_cleanup(&o);drain_log(&o);
        if (o.state == AUTOSOURCARR_SOURCE_STOPPING) {
            if (!o.no_children) signal_descendants(&o);
            reap(&o);pump_observation(&o);observation_finish(&o);flush_observation(&o);observation_shutdown_cleanup(&o);drain_log(&o);
            if (o.no_children && o.log_read < 0 && o.exec_read < 0 && observation_drained(&o)) {
                o.state = o.fault ? AUTOSOURCARR_SOURCE_FAULTED : AUTOSOURCARR_SOURCE_IDLE;
                if (o.start_request != 0) { queue_reply(&o, o.start_request, o.start_opcode, AUTOSOURCARR_SOURCE_UNAVAILABLE, AUTOSOURCARR_SOURCE_DIAG_EXEC); o.start_request = 0; }
                if (o.stop_request != 0) { queue_reply(&o, o.stop_request, o.stop_opcode, o.fault ? AUTOSOURCARR_SOURCE_FAILED : AUTOSOURCARR_SOURCE_OK, o.fault); o.stop_request = 0; }
            }
        }
        flush_replies(&o);
        if (o.shutdown && o.no_children && o.log_read < 0 && o.exec_read < 0 && o.reply_count == 0 && observation_drained(&o)) break;
        struct pollfd waits[5] = {
            {.fd = o.shutdown ? -1 : o.control, .events = POLLIN}, {.fd = o.log_read, .events = POLLIN},
            {.fd = o.exec_read, .events = POLLIN}, {.fd = o.reply_count || o.observation.event_pending ? o.response : -1, .events = POLLOUT},
            {.fd = o.observation.read,.events = POLLIN}
        };
        if (poll(waits, 5, 50) < 0 && errno != EINTR) fail_owner(&o, AUTOSOURCARR_SOURCE_DIAG_IO);
    }
    close_owned(&o.control); close_owned(&o.response); close_owned(&o.root); close_owned(&o.observation_root);
    explicit_bzero(&o.observation,sizeof(o.observation));
    explicit_bzero(o.incoming, sizeof(o.incoming)); explicit_bzero(o.binding.boot_nonce, sizeof(o.binding.boot_nonce));
    return o.fault ? 78 : 0;
failed:
    for (size_t i = 0; i < descriptor_count; i++) close_owned(&owned[i]);
    return 78;
}

int autosourcarr_source_owner_run(const struct autosourcarr_source_owner_config *config) { return run(config, 0); }
int autosourcarr_source_owner_physical_test(const struct autosourcarr_source_owner_config *config) { return run(config, 1); }
