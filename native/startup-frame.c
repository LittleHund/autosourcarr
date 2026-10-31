#include "startup-frame.h"
#include <string.h>

static int is_digit(unsigned char value)
{
    return value >= '0' && value <= '9';
}

static int canonical_integer(const unsigned char *input, size_t length)
{
    if (length == 1 && input[0] == '0') return 1;
    size_t start = length > 0 && input[0] == '-' ? 1U : 0U;
    if (length == start || input[start] < '1' || input[start] > '9') return 0;
    for (size_t index = start + 1; index < length; index++)
        if (!is_digit(input[index])) return 0;
    return 1;
}

int autosourcarr_startup_parse(const unsigned char *frame, size_t length,
    const unsigned char expected_nonce[64], struct autosourcarr_startup_request *request)
{
    if (frame == NULL || expected_nonce == NULL || request == NULL || length == 0
        || length > AUTOSOURCARR_STARTUP_MAXIMUM || frame[length - 1] != '\n') return -1;
    const unsigned char *fields[5];
    size_t sizes[5];
    size_t count = 0, start = 0;
    for (size_t index = 0; index < length; index++) {
        unsigned char value = frame[index];
        if (value == 0 || value > 127 || value == '\r') return -1;
        if (value == '\n') {
            if (count == 5) return -1;
            fields[count] = frame + start;
            sizes[count++] = index - start;
            start = index + 1;
        }
    }
    static const char magic[] = "AUTOSOURCARR-STARTUP-1";
    if (count != 5 || start != length || sizes[0] != sizeof(magic) - 1
        || memcmp(fields[0], magic, sizeof(magic) - 1) != 0 || sizes[1] != 64) return -1;
    unsigned char different = 0;
    for (size_t index = 0; index < 64; index++) {
        unsigned char expected = expected_nonce[index];
        if (!is_digit(expected) && (expected < 'a' || expected > 'f')) return -1;
        different |= (unsigned char)(fields[1][index] ^ expected);
    }
    if (different != 0) return -1;
    int integrated;
    if (sizes[2] == 10 && memcmp(fields[2], "integrated", 10) == 0) integrated = 1;
    else if (sizes[2] == 8 && memcmp(fields[2], "external", 8) == 0) integrated = 0;
    else return -1;
    if (!canonical_integer(fields[3], sizes[3]) || !canonical_integer(fields[4], sizes[4])) return -1;
    *request = (struct autosourcarr_startup_request) {
        .integrated = integrated, .uid = fields[3], .uid_length = sizes[3],
        .gid = fields[4], .gid_length = sizes[4]
    };
    return 0;
}

static int positive_id(const unsigned char *input, size_t length, uint32_t *output)
{
    uint32_t value = 0;
    if (length == 0 || input[0] == '-') return -1;
    for (size_t index = 0; index < length; index++) {
        if (!is_digit(input[index])) return -1;
        uint32_t digit = (uint32_t)(input[index] - '0');
        if (value > (UINT32_MAX - 1U - digit) / 10U) return -1;
        value = value * 10U + digit;
    }
    if (value == 0 || value == UINT32_MAX) return -1;
    *output = value;
    return 0;
}

int autosourcarr_source_identity_select(const struct autosourcarr_startup_request *request,
    uint32_t real_caller_uid, uint32_t real_caller_gid,
    uint32_t directory_uid, uint32_t directory_gid, int empty_directory,
    struct autosourcarr_source_identity *identity)
{
    if (request == NULL || identity == NULL || request->uid == NULL || request->gid == NULL
        || !canonical_integer(request->uid, request->uid_length)
        || !canonical_integer(request->gid, request->gid_length)
        || (request->integrated != 0 && request->integrated != 1)) return -1;
    *identity = (struct autosourcarr_source_identity) { .kind = AUTOSOURCARR_SOURCE_REFUSED };
    if (!request->integrated) {
        identity->kind = AUTOSOURCARR_SOURCE_EXTERNAL;
        return 0;
    }
    if (real_caller_uid != 0) {
        identity->kind = AUTOSOURCARR_SOURCE_INHERIT;
        identity->uid = real_caller_uid;
        identity->gid = real_caller_gid;
        return 0;
    }
    uint32_t uid, gid;
    if (request->uid_length == 1 && request->uid[0] == '0') uid = directory_uid != 0 ? directory_uid : 1000U;
    else if (positive_id(request->uid, request->uid_length, &uid) != 0) return 0;
    if (request->gid_length == 1 && request->gid[0] == '0') gid = directory_gid != 0 ? directory_gid : uid;
    else if (positive_id(request->gid, request->gid_length, &gid) != 0) return 0;
    if (uid == 0 || gid == 0 || uid == UINT32_MAX || gid == UINT32_MAX) return 0;
    *identity = (struct autosourcarr_source_identity) {
        .kind = AUTOSOURCARR_SOURCE_SELECTED, .uid = uid, .gid = gid,
        .clear_groups = 1, .chown_empty_root = directory_uid == 0 && empty_directory != 0
    };
    return 0;
}
