#include "bootstrap-binding.h"
#include <limits.h>
#include <string.h>

static uint32_t get32(const unsigned char *p)
{
    uint32_t value = 0;
    for (size_t i = 0; i < 4; i++) value |= (uint32_t)p[i] << (8U * i);
    return value;
}
static uint64_t get64(const unsigned char *p)
{
    uint64_t value = 0;
    for (size_t i = 0; i < 8; i++) value |= (uint64_t)p[i] << (8U * i);
    return value;
}
static void put32(unsigned char *p, uint32_t value)
{ for (size_t i = 0; i < 4; i++) p[i] = (unsigned char)(value >> (8U * i)); }
static void put64(unsigned char *p, uint64_t value)
{ for (size_t i = 0; i < 8; i++) p[i] = (unsigned char)(value >> (8U * i)); }
static int lower_hex(unsigned char value)
{
    if (value >= '0' && value <= '9') return value - '0';
    if (value >= 'a' && value <= 'f') return value - 'a' + 10;
    return -1;
}
static int valid_proof(const struct autosourcarr_boot_binding *binding)
{
    const unsigned char *proof = binding->expected_owner_proof;
    if (proof == NULL || binding->expected_owner_proof_length != AUTOSOURCARR_SOURCE_PROOF_BYTES
        || memcmp(proof, "ASOPRF1\n", 8) != 0) return 0;
    for (size_t i = 0; i < 32; i++) {
        int high = lower_hex(binding->nonce[i * 2]), low = lower_hex(binding->nonce[i * 2 + 1]);
        if (high < 0 || low < 0 || proof[8 + i] != (unsigned char)((high << 4) | low)) return 0;
    }
    uint32_t owner_pid = get32(proof + 40), uid = get32(proof + 48), gid = get32(proof + 52);
    return owner_pid > 1 && owner_pid <= INT_MAX && owner_pid != binding->main_pid
        && get32(proof + 44) == 1 && uid != 0 && uid != UINT32_MAX && gid != 0 && gid != UINT32_MAX
        && get64(proof + 64) != 0 && get32(proof + 72) == 0;
}

int autosourcarr_boot_binding_encode(const struct autosourcarr_boot_binding *binding,
    unsigned char *output, size_t capacity, size_t *length)
{
    if (length == NULL) return -1;
    *length = 0;
    if (binding == NULL || output == NULL || binding->main_pid <= 1 || binding->main_pid > INT_MAX
        || binding->original_uid == UINT32_MAX || binding->original_gid == UINT32_MAX
        || binding->group_count > 1 || (binding->group_count == 0 && binding->kvm_gid != 0)
        || binding->kvm_gid == UINT32_MAX || binding->startup == NULL
        || binding->startup_length == 0 || binding->startup_length > AUTOSOURCARR_STARTUP_MAXIMUM) return -1;
    struct autosourcarr_startup_request request;
    if (autosourcarr_startup_parse(binding->startup, binding->startup_length, binding->nonce, &request) != 0) return -1;
    for (size_t i = 0; i < AUTOSOURCARR_BOOT_PIPE_COUNT; i++) {
        int present = request.integrated || i == 3; /* Descriptor6 in external mode. */
        const struct autosourcarr_boot_pipe *pipe = &binding->pipes[i];
        if (present ? pipe->inode == 0 : (pipe->device != 0 || pipe->inode != 0)) return -1;
        if (present) for (size_t j = 0; j < i; j++)
            if (binding->pipes[j].inode != 0 && pipe->device == binding->pipes[j].device
                && pipe->inode == binding->pipes[j].inode) return -1;
    }
    if (request.integrated) {
        if (!valid_proof(binding)) return -1;
    } else if (binding->expected_owner_proof_length != 0 || binding->expected_owner_proof != NULL) return -1;
    size_t total = AUTOSOURCARR_BOOT_HEADER_BYTES + binding->startup_length + binding->expected_owner_proof_length;
    if (total > capacity) return -1;
    memset(output, 0, AUTOSOURCARR_BOOT_HEADER_BYTES);
    memcpy(output, "ASBIND1\n", 8); put32(output + 8, 1); put32(output + 12, binding->main_pid);
    put32(output + 16, 1); put32(output + 20, binding->original_uid); put32(output + 24, binding->original_gid);
    memcpy(output + 32, binding->nonce, 64); put32(output + 96, (uint32_t)binding->startup_length);
    put32(output + 100, (uint32_t)binding->expected_owner_proof_length);
    put32(output + 104, binding->group_count); put32(output + 108, binding->kvm_gid);
    for (size_t i = 0; i < AUTOSOURCARR_BOOT_PIPE_COUNT; i++) {
        put64(output + 112 + i * 16, binding->pipes[i].device);
        put64(output + 120 + i * 16, binding->pipes[i].inode);
    }
    memcpy(output + AUTOSOURCARR_BOOT_HEADER_BYTES, binding->startup, binding->startup_length);
    if (binding->expected_owner_proof_length != 0)
        memcpy(output + AUTOSOURCARR_BOOT_HEADER_BYTES + binding->startup_length,
            binding->expected_owner_proof, binding->expected_owner_proof_length);
    *length = total;
    return 0;
}

int autosourcarr_owner_reap_encode(const unsigned char nonce[32], uint32_t owner_pid,
    int32_t actual_wait_status, unsigned char output[AUTOSOURCARR_OWNER_REAP_BYTES])
{
    if (nonce == NULL || output == NULL || owner_pid <= 1 || owner_pid > INT_MAX) return -1;
    memcpy(output, "ASREAP1\n", 8); memcpy(output + 8, nonce, 32);
    put32(output + 40, owner_pid); put32(output + 44, (uint32_t)actual_wait_status);
    return 0;
}
