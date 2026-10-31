#define _GNU_SOURCE
#include "source-observation.h"
#include <errno.h>
#include <limits.h>
#include <stddef.h>
#include <string.h>

_Static_assert(sizeof(struct autosourcarr_source_observation_result)==176U,"Exact helper result ABI");
_Static_assert(offsetof(struct autosourcarr_source_observation_result,length)==16U,"Exact helper length offset");
_Static_assert(offsetof(struct autosourcarr_source_observation_result,sha256)==24U,"Exact helper digest offset");
_Static_assert(offsetof(struct autosourcarr_source_observation_result,root_device)==56U,"Exact helper roots offset");
_Static_assert(offsetof(struct autosourcarr_source_observation_result,cleanup)==168U,"Exact helper cleanup offset");
_Static_assert(sizeof(struct autosourcarr_source_observation_cleanup_result)==32U,"Exact cleanup helper result ABI");
_Static_assert(sizeof(struct autosourcarr_source_startup_cleanup_result)==40U,"Exact startup cleanup result ABI");

/* Streaming SHA256 for bounded captured bytes: FIPS180-4(2015)4.1.2/4.2.2,
 * 5.1.1/5.3.3/6.2.2. This is not a validation/certification claim. No key or
 * encryption protocol is implemented. Original NIST vectors/differential checks cover the hash bodies; this does not
 * qualify Linux file/process behavior or authenticate a parent completion. */
struct snapshot_sha256 {
    uint32_t words[8];
    uint64_t bytes;
    unsigned char block[64];
    size_t used;
};
static uint32_t snapshot_rotr(uint32_t value,unsigned int count)
{ return (value>>count)|(value<<(32U-count)); }
static uint32_t snapshot_be32(const unsigned char *p)
{ return (uint32_t)p[0]<<24|(uint32_t)p[1]<<16|(uint32_t)p[2]<<8|(uint32_t)p[3]; }
static void snapshot_sha_block(struct snapshot_sha256 *state,const unsigned char bytes[64])
{
    static const uint32_t k[64]={
        0x428a2f98U,0x71374491U,0xb5c0fbcfU,0xe9b5dba5U,0x3956c25bU,0x59f111f1U,0x923f82a4U,0xab1c5ed5U,
        0xd807aa98U,0x12835b01U,0x243185beU,0x550c7dc3U,0x72be5d74U,0x80deb1feU,0x9bdc06a7U,0xc19bf174U,
        0xe49b69c1U,0xefbe4786U,0x0fc19dc6U,0x240ca1ccU,0x2de92c6fU,0x4a7484aaU,0x5cb0a9dcU,0x76f988daU,
        0x983e5152U,0xa831c66dU,0xb00327c8U,0xbf597fc7U,0xc6e00bf3U,0xd5a79147U,0x06ca6351U,0x14292967U,
        0x27b70a85U,0x2e1b2138U,0x4d2c6dfcU,0x53380d13U,0x650a7354U,0x766a0abbU,0x81c2c92eU,0x92722c85U,
        0xa2bfe8a1U,0xa81a664bU,0xc24b8b70U,0xc76c51a3U,0xd192e819U,0xd6990624U,0xf40e3585U,0x106aa070U,
        0x19a4c116U,0x1e376c08U,0x2748774cU,0x34b0bcb5U,0x391c0cb3U,0x4ed8aa4aU,0x5b9cca4fU,0x682e6ff3U,
        0x748f82eeU,0x78a5636fU,0x84c87814U,0x8cc70208U,0x90befffaU,0xa4506cebU,0xbef9a3f7U,0xc67178f2U};
    uint32_t w[64];
    for (size_t i=0;i<16;i++) w[i]=snapshot_be32(bytes+4U*i);
    for (size_t i=16;i<64;i++) {
        uint32_t a=w[i-15],b=w[i-2];
        uint32_t s0=snapshot_rotr(a,7)^snapshot_rotr(a,18)^(a>>3);
        uint32_t s1=snapshot_rotr(b,17)^snapshot_rotr(b,19)^(b>>10);
        w[i]=w[i-16]+s0+w[i-7]+s1;
    }
    uint32_t a=state->words[0],b=state->words[1],c=state->words[2],d=state->words[3];
    uint32_t e=state->words[4],f=state->words[5],g=state->words[6],h=state->words[7];
    for (size_t i=0;i<64;i++) {
        uint32_t s1=snapshot_rotr(e,6)^snapshot_rotr(e,11)^snapshot_rotr(e,25);
        uint32_t choice=(e&f)^((~e)&g),t1=h+s1+choice+k[i]+w[i];
        uint32_t s0=snapshot_rotr(a,2)^snapshot_rotr(a,13)^snapshot_rotr(a,22);
        uint32_t majority=(a&b)^(a&c)^(b&c),t2=s0+majority;
        h=g;g=f;f=e;e=d+t1;d=c;c=b;b=a;a=t1+t2;
    }
    state->words[0]+=a;state->words[1]+=b;state->words[2]+=c;state->words[3]+=d;
    state->words[4]+=e;state->words[5]+=f;state->words[6]+=g;state->words[7]+=h;
    explicit_bzero(w,sizeof(w));
}
static void snapshot_sha_init(struct snapshot_sha256 *state)
{
    static const uint32_t initial[8]={0x6a09e667U,0xbb67ae85U,0x3c6ef372U,0xa54ff53aU,0x510e527fU,0x9b05688cU,0x1f83d9abU,0x5be0cd19U};
    memset(state,0,sizeof(*state));memcpy(state->words,initial,sizeof(initial));
}
static int snapshot_sha_update(struct snapshot_sha256 *state,const unsigned char *bytes,size_t size)
{
    if ((size!=0 && bytes==NULL) || state->used>=64 || state->bytes>UINT64_MAX/8U
        || (uint64_t)size>UINT64_MAX/8U-state->bytes) { errno=EOVERFLOW;return -1; }
    state->bytes+=(uint64_t)size;
    while (size!=0) {
        size_t part=64U-state->used;if (part>size) part=size;
        memcpy(state->block+state->used,bytes,part);state->used+=part;bytes+=part;size-=part;
        if (state->used==64) { snapshot_sha_block(state,state->block);state->used=0; }
    }
    return 0;
}
static void snapshot_sha_finish(struct snapshot_sha256 *state,unsigned char digest[32])
{
    uint64_t bits=state->bytes*8U;
    state->block[state->used++]=0x80;
    if (state->used>56) { memset(state->block+state->used,0,64U-state->used);snapshot_sha_block(state,state->block);state->used=0; }
    memset(state->block+state->used,0,56U-state->used);
    for (size_t i=0;i<8;i++) state->block[56+i]=(unsigned char)(bits>>(56U-8U*i));
    snapshot_sha_block(state,state->block);
    for (size_t i=0;i<8;i++) for (size_t j=0;j<4;j++) digest[4*i+j]=(unsigned char)(state->words[i]>>(24U-8U*j));
    explicit_bzero(state,sizeof(*state));
}

static int snapshot_jar_leaf(const char *leaf)
{
    if (leaf==NULL) return 0;
    size_t count=strnlen(leaf,AUTOSOURCARR_SOURCE_OBSERVATION_LEAF_MAXIMUM+1U);
    if (count<5 || count>AUTOSOURCARR_SOURCE_OBSERVATION_LEAF_MAXIMUM || memcmp(leaf+count-4,".jar",4)!=0) return 0;
    for (size_t i=0;i<count;i++) {
        unsigned char c=(unsigned char)leaf[i];
        if (!(c>='A'&&c<='Z') && !(c>='a'&&c<='z') && !(c>='0'&&c<='9') && c!='_' && c!='.' && c!='+' && c!='-') return 0;
    }
    return 1;
}
static int snapshot_ticket(const char *ticket)
{
    if (ticket==NULL || strnlen(ticket,33)!=32) return 0;
    for (size_t i=0;i<32;i++) if (!(ticket[i]>='0'&&ticket[i]<='9') && !(ticket[i]>='a'&&ticket[i]<='f')) return 0;
    return 1;
}

#if defined(__linux__) && defined(__x86_64__)
#include <fcntl.h>
#include <dirent.h>
#include <grp.h>
#include <linux/capability.h>
#include <signal.h>
#include <sys/fsuid.h>
#include <sys/prctl.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <unistd.h>

static void snapshot_close(int *descriptor,int *failure)
{
    if (*descriptor<0) return;
    int old=*descriptor;*descriptor=-1;
    /* Linux close errors must never trigger a retry against a reused number. */
    if (close(old)!=0 && *failure==0) *failure=errno?errno:EIO;
}
static int snapshot_identity(const struct autosourcarr_source_observation_config *c)
{
    uid_t r,e,s;gid_t rg,eg,sg;
    struct __user_cap_header_struct header={.version=_LINUX_CAPABILITY_VERSION_3,.pid=0};
    struct __user_cap_data_struct caps[2]={{0},{0}};
    if (getppid()!=c->expected_parent || getresuid(&r,&e,&s)!=0 || getresgid(&rg,&eg,&sg)!=0
        || r!=c->uid || e!=c->uid || s!=c->uid || rg!=c->gid || eg!=c->gid || sg!=c->gid
        || (uint32_t)setfsuid((uid_t)-1)!=c->uid || (uint32_t)setfsgid((gid_t)-1)!=c->gid
        || getgroups(0,NULL)!=0 || prctl(PR_GET_DUMPABLE,0L,0L,0L,0L)!=0
        || prctl(PR_GET_NO_NEW_PRIVS,0L,0L,0L,0L)!=1 || syscall(SYS_capget,&header,caps)!=0) return 0;
    for (size_t i=0;i<2;i++) if (caps[i].effective!=0 || caps[i].permitted!=0 || caps[i].inheritable!=0) return 0;
    return 1;
}
static int snapshot_child_prepare(const struct autosourcarr_source_observation_config *c)
{
    /* The source owner is single-threaded and already non-dumpable. Never exec
     * or repair dumpability after a descriptor exposure window. */
    if (prctl(PR_GET_DUMPABLE,0L,0L,0L,0L)!=0
        || prctl(PR_SET_PDEATHSIG,SIGKILL,0L,0L,0L)!=0 || getppid()!=c->expected_parent
        || !snapshot_identity(c)) {errno=EPERM;return -1;}
    struct sigaction defaults;memset(&defaults,0,sizeof(defaults));defaults.sa_handler=SIG_DFL;
    if (sigaction(SIGTERM,&defaults,NULL)!=0 || sigaction(SIGINT,&defaults,NULL)!=0) return -1;
    umask(0077);return 0;
}
static int snapshot_directory(const struct stat *s,uint64_t device,uint64_t inode)
{
    return S_ISDIR(s->st_mode) && s->st_nlink!=0 && (uint64_t)s->st_dev==device && (uint64_t)s->st_ino==inode;
}
static int snapshot_regular(const struct stat *s,uint64_t maximum)
{ return S_ISREG(s->st_mode) && s->st_nlink==1 && s->st_size>=0 && (uint64_t)s->st_size<=maximum; }
static int snapshot_same_file(const struct stat *a,const struct stat *b)
{
    return a->st_dev==b->st_dev && a->st_ino==b->st_ino && a->st_mode==b->st_mode
        && a->st_nlink==b->st_nlink && a->st_uid==b->st_uid && a->st_gid==b->st_gid && a->st_size==b->st_size
        && a->st_mtim.tv_sec==b->st_mtim.tv_sec && a->st_mtim.tv_nsec==b->st_mtim.tv_nsec
        && a->st_ctim.tv_sec==b->st_ctim.tv_sec && a->st_ctim.tv_nsec==b->st_ctim.tv_nsec;
}
static int snapshot_source_current(const struct autosourcarr_source_observation_config *c)
{
    static const char *const parts[]={"home","suwayomi",".local","share","Tachidesk"};
    struct stat held,named;
    if (fstat(c->root_fd,&held)!=0 || !snapshot_directory(&held,c->root_device,c->root_inode)) return 0;
    int current=open("/",O_PATH|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC);
    if (current<0) return 0;
    int failure=0;
    for (size_t i=0;i<sizeof(parts)/sizeof(parts[0]);i++) {
        int next=openat(current,parts[i],O_PATH|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC);
        snapshot_close(&current,&failure);
        if (next<0 || failure!=0) { if (next>=0) snapshot_close(&next,&failure);return 0; }
        current=next;
    }
    int valid=fstat(current,&named)==0 && snapshot_directory(&named,c->root_device,c->root_inode);
    snapshot_close(&current,&failure);return valid && failure==0;
}
static int snapshot_exchange_held(const struct autosourcarr_source_observation_config *c)
{
    struct stat status;int flags=fcntl(c->exchange_fd,F_GETFL),descriptor=fcntl(c->exchange_fd,F_GETFD);
    return flags>=0 && (flags&O_PATH)==0 && (flags&(O_ACCMODE|O_DIRECTORY))==(O_RDONLY|O_DIRECTORY)
        && descriptor>=0 && (descriptor&FD_CLOEXEC)!=0 && fstat(c->exchange_fd,&status)==0
        && snapshot_directory(&status,c->exchange_device,c->exchange_inode)
        && status.st_uid==c->uid && status.st_gid==568 && (status.st_mode&07777)==0770;
}
static int snapshot_source_unchanged(const struct autosourcarr_source_observation_config *c,
    int directory,int input,const char *parent,const char *leaf,const struct stat *directory_before,const struct stat *before,uint64_t maximum)
{
    struct stat after,dir_after,reopened;
    if (fstat(input,&after)!=0 || !snapshot_regular(&after,maximum) || !snapshot_same_file(before,&after)
        || fstat(directory,&dir_after)!=0 || !snapshot_directory(&dir_after,(uint64_t)directory_before->st_dev,(uint64_t)directory_before->st_ino)
        || !snapshot_source_current(c)) return 0;
    int current_dir=openat(c->root_fd,parent,O_PATH|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC),current=-1,failure=0;
    int valid=current_dir>=0 && fstat(current_dir,&dir_after)==0
        && snapshot_directory(&dir_after,(uint64_t)directory_before->st_dev,(uint64_t)directory_before->st_ino);
    if (valid) {
        current=openat(current_dir,leaf,O_RDONLY|O_NONBLOCK|O_NOFOLLOW|O_CLOEXEC);
        valid=current>=0 && fstat(current,&reopened)==0 && snapshot_regular(&reopened,maximum) && snapshot_same_file(before,&reopened);
    }
    snapshot_close(&current,&failure);snapshot_close(&current_dir,&failure);return valid && failure==0;
}
static int snapshot_write_all(int output,const unsigned char *bytes,size_t count)
{
    size_t at=0;
    while (at<count) {
        ssize_t wrote=write(output,bytes+at,count-at);
        if (wrote>0) { at+=(size_t)wrote;continue; }
        if (wrote<0 && errno==EINTR) continue;
        if (wrote==0) errno=EIO;
        return -1;
    }
    return 0;
}
static void snapshot_cleanup(const struct autosourcarr_source_observation_config *c,
    int output,int created,const struct stat *created_status,struct autosourcarr_source_observation_result *result)
{
    if (!created) return;
    struct stat held,named;
    if (output<0 || fstat(output,&held)!=0 || held.st_dev!=created_status->st_dev || held.st_ino!=created_status->st_ino
        || fstatat(c->exchange_fd,c->ticket,&named,AT_SYMLINK_NOFOLLOW)!=0
        || named.st_dev!=held.st_dev || named.st_ino!=held.st_ino || !S_ISREG(named.st_mode)
        || unlinkat(c->exchange_fd,c->ticket,0)!=0) result->cleanup=2;
    else result->cleanup=1;
}

int autosourcarr_source_observation_capture(const struct autosourcarr_source_observation_config *c,
    struct autosourcarr_source_observation_result *result)
{
    if (result==NULL) {errno=EINVAL;return -1;}
    memset(result,0,sizeof(*result));result->outcome=AUTOSOURCARR_OBSERVATION_FAILED;
    if (c==NULL || c->root_fd<3 || c->exchange_fd<3 || c->root_fd==c->exchange_fd || c->expected_parent<=1
        || c->uid==0 || c->uid==UINT32_MAX || c->gid==0 || c->gid==UINT32_MAX || !snapshot_ticket(c->ticket)
        || (c->kind!=AUTOSOURCARR_OBSERVE_JAR && c->kind!=AUTOSOURCARR_OBSERVE_UA)
        || (c->kind==AUTOSOURCARR_OBSERVE_JAR && !snapshot_jar_leaf(c->leaf))) {errno=EINVAL;return -1;}
    result->kind=c->kind;
    if (snapshot_child_prepare(c)!=0) return -1;
    if (!snapshot_source_current(c) || !snapshot_exchange_held(c)) {result->error=ESTALE;return 0;}
    result->root_device=c->root_device;result->root_inode=c->root_inode;
    result->exchange_device=c->exchange_device;result->exchange_inode=c->exchange_inode;
    const char *parent=c->kind==AUTOSOURCARR_OBSERVE_JAR?"extensions":"cache";
    const char *leaf=c->kind==AUTOSOURCARR_OBSERVE_JAR?c->leaf:"autosourcarr-user-agent.properties";
    uint64_t maximum=c->kind==AUTOSOURCARR_OBSERVE_JAR?AUTOSOURCARR_SOURCE_OBSERVATION_JAR_MAXIMUM:AUTOSOURCARR_SOURCE_OBSERVATION_UA_MAXIMUM;
    int directory=-1,input=-1,output=-1,created=0,created_known=0,failure=0,changed=0;
    struct stat directory_before,before,created_status,after,named;
    unsigned char bytes[16384],digest[32];struct snapshot_sha256 hash;memset(&hash,0,sizeof(hash));memset(digest,0,sizeof(digest));
    uint64_t size=0;
    directory=openat(c->root_fd,parent,O_PATH|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC);
    if (directory<0) {failure=errno;goto missing_or_failed;}
    if (fstat(directory,&directory_before)!=0 || !S_ISDIR(directory_before.st_mode) || directory_before.st_nlink==0) {failure=errno?errno:EIO;goto done;}
    input=openat(directory,leaf,O_RDONLY|O_NONBLOCK|O_NOFOLLOW|O_CLOEXEC);
    if (input<0) {failure=errno;goto missing_or_failed;}
    if (fstat(input,&before)!=0 || !snapshot_regular(&before,maximum)
        || (c->kind==AUTOSOURCARR_OBSERVE_JAR && before.st_size==0)) {failure=EINVAL;goto done;}
    output=openat(c->exchange_fd,c->ticket,O_WRONLY|O_NONBLOCK|O_CREAT|O_EXCL|O_NOFOLLOW|O_CLOEXEC,0600);
    if (output<0) {failure=errno;goto done;}
    created=1;
    if (fstat(output,&created_status)!=0) {failure=errno;goto done;}created_known=1;
    if (!snapshot_regular(&created_status,0) || created_status.st_uid!=c->uid || created_status.st_gid!=c->gid
        || (created_status.st_mode&07777)!=0600) {failure=EPERM;goto done;}
    snapshot_sha_init(&hash);
    for (;;) {
        size_t count=(size_t)(maximum+1U-size);if(count>sizeof(bytes)) count=sizeof(bytes);
        ssize_t got=read(input,bytes,count);
        if (got<0 && errno==EINTR) continue;
        if (got<0) {failure=errno;goto done;}
        if (got==0) break;
        if ((uint64_t)got>maximum-size) {failure=EFBIG;changed=1;goto done;}
        if (snapshot_write_all(output,bytes,(size_t)got)!=0 || snapshot_sha_update(&hash,bytes,(size_t)got)!=0) {failure=errno;goto done;}
        size+=(uint64_t)got;explicit_bzero(bytes,sizeof(bytes));
    }
    if (size!=(uint64_t)before.st_size || !snapshot_source_unchanged(c,directory,input,parent,leaf,&directory_before,&before,maximum)
        || !snapshot_exchange_held(c)) {failure=ESTALE;changed=1;goto done;}
    snapshot_sha_finish(&hash,digest);
    if (fsync(output)!=0 || fchmod(output,0444)!=0 || fsync(output)!=0) {failure=errno;goto done;}
    if (fstat(output,&after)!=0 || !snapshot_regular(&after,maximum) || (uint64_t)after.st_size!=size
        || after.st_uid!=c->uid || after.st_gid!=c->gid || (after.st_mode&07777)!=0444
        || after.st_dev!=created_status.st_dev || after.st_ino!=created_status.st_ino
        || fstatat(c->exchange_fd,c->ticket,&named,AT_SYMLINK_NOFOLLOW)!=0 || !snapshot_same_file(&after,&named)
        || !snapshot_source_unchanged(c,directory,input,parent,leaf,&directory_before,&before,maximum)
        || !snapshot_exchange_held(c)) {failure=ESTALE;changed=1;goto done;}
    result->length=size;memcpy(result->sha256,digest,sizeof(digest));
    result->output_device=(uint64_t)after.st_dev;result->output_inode=(uint64_t)after.st_ino;
    result->source_device=(uint64_t)before.st_dev;result->source_inode=(uint64_t)before.st_ino;
    result->directory_device=(uint64_t)directory_before.st_dev;result->directory_inode=(uint64_t)directory_before.st_ino;
    result->output_uid=(uint32_t)after.st_uid;result->output_gid=(uint32_t)after.st_gid;result->output_mode=(uint32_t)after.st_mode;result->output_links=(uint32_t)after.st_nlink;
    result->source_uid=(uint32_t)before.st_uid;result->source_gid=(uint32_t)before.st_gid;result->source_mode=(uint32_t)before.st_mode;result->source_links=(uint32_t)before.st_nlink;
    result->facts=AUTOSOURCARR_OBSERVATION_STABLE|AUTOSOURCARR_OBSERVATION_ROOT_CURRENT|AUTOSOURCARR_OBSERVATION_EXCHANGE_HELD|AUTOSOURCARR_OBSERVATION_PUBLISHED;
    result->outcome=AUTOSOURCARR_OBSERVATION_COMPLETE;goto done;
missing_or_failed:
    if (failure==ENOENT && snapshot_source_current(c) && snapshot_exchange_held(c)) {
        result->outcome=AUTOSOURCARR_OBSERVATION_MISSING;
        result->facts=AUTOSOURCARR_OBSERVATION_ROOT_CURRENT|AUTOSOURCARR_OBSERVATION_EXCHANGE_HELD;failure=0;
    }
done:
    if (failure!=0) {
        if (created && created_known) snapshot_cleanup(c,output,created,&created_status,result);
        else if (created) result->cleanup=2;
    }
    snapshot_close(&input,&failure);snapshot_close(&directory,&failure);snapshot_close(&output,&failure);
    if (failure!=0) {
        uint32_t cleanup=result->cleanup;
        memset(result,0,sizeof(*result));result->kind=c->kind;result->outcome=changed?AUTOSOURCARR_OBSERVATION_CHANGED:AUTOSOURCARR_OBSERVATION_FAILED;
        result->error=(uint32_t)failure;result->cleanup=cleanup;
        /* Retain only the created inode identity for failed-output reconciliation.
         * This is ownership evidence, not a complete/readable snapshot receipt. */
        if (created_known) {
            result->output_device=(uint64_t)created_status.st_dev;
            result->output_inode=(uint64_t)created_status.st_ino;
        }
        /* A failed final close can leave an issued file. Parent must reconcile
         * that ticket, never claim that cancelled/failed output was cleaned. */
        if (created && cleanup==0) result->cleanup=2;
    }
    explicit_bzero(bytes,sizeof(bytes));explicit_bzero(digest,sizeof(digest));explicit_bzero(&hash,sizeof(hash));
    return 0;
}

int autosourcarr_source_observation_remove(const struct autosourcarr_source_observation_config *c,
    uint64_t output_device,uint64_t output_inode,struct autosourcarr_source_observation_cleanup_result *result)
{
    if (result==NULL) {errno=EINVAL;return -1;}
    memset(result,0,sizeof(*result));result->outcome=AUTOSOURCARR_OBSERVATION_FAILED;result->cleanup=2;
    if (c==NULL || c->exchange_fd<3 || c->expected_parent<=1 || !snapshot_ticket(c->ticket)
        || c->uid==0 || c->uid==UINT32_MAX || c->gid==0 || c->gid==UINT32_MAX
        || (output_inode==0 && output_device!=0)) {errno=EINVAL;return -1;}
    if (snapshot_child_prepare(c)!=0) return -1;
    if (!snapshot_exchange_held(c)) {result->error=ESTALE;return 0;}
    int input=-1,failure=0;struct stat named,held,current;
    if (fstatat(c->exchange_fd,c->ticket,&named,AT_SYMLINK_NOFOLLOW)!=0) {
        if (errno==ENOENT) goto removed;
        failure=errno;goto done;
    }
    /* Never infer that a present but unknown inode belongs to this operation. */
    if (output_inode==0 || !snapshot_regular(&named,AUTOSOURCARR_SOURCE_OBSERVATION_JAR_MAXIMUM)
        || (uint64_t)named.st_dev!=output_device || (uint64_t)named.st_ino!=output_inode
        || named.st_uid!=c->uid || named.st_gid!=c->gid
        || ((named.st_mode&07777)!=0600 && (named.st_mode&07777)!=0444)) {failure=ESTALE;goto done;}
    input=openat(c->exchange_fd,c->ticket,O_RDONLY|O_NONBLOCK|O_NOFOLLOW|O_CLOEXEC);
    if (input<0) {failure=errno;goto done;}
    if (fstat(input,&held)!=0 || !snapshot_same_file(&named,&held)
        || fstatat(c->exchange_fd,c->ticket,&current,AT_SYMLINK_NOFOLLOW)!=0 || !snapshot_same_file(&held,&current)
        || !snapshot_exchange_held(c)) {failure=ESTALE;goto done;}
    /* Main cannot replace/reuse a leased ticket; the JVM cannot traverse its0700
     * ancestor. unlinkat has no inode-CAS. Same-UID legacy mode is not a new
     * process credential boundary, and uncertain/substituted names stay refused. */
    if (unlinkat(c->exchange_fd,c->ticket,0)!=0) {failure=errno;goto done;}
removed:
    if (!snapshot_exchange_held(c)) {failure=ESTALE;goto done;}
    result->outcome=AUTOSOURCARR_OBSERVATION_COMPLETE;result->cleanup=1;
    result->output_device=output_device;result->output_inode=output_inode;
done:
    snapshot_close(&input,&failure);
    if (failure!=0) {
        memset(result,0,sizeof(*result));result->outcome=AUTOSOURCARR_OBSERVATION_FAILED;result->cleanup=2;result->error=(uint32_t)failure;
    }
    return 0;
}

struct startup_ticket {char name[33];struct stat before;};
static int startup_exchange(const struct autosourcarr_source_startup_cleanup_config *c)
{
    struct autosourcarr_source_observation_config binding={.exchange_fd=c->exchange_fd,.uid=c->source_uid,
        .exchange_device=c->exchange_device,.exchange_inode=c->exchange_inode};
    if (!snapshot_exchange_held(&binding)) return 0;
    static const char *const parts[]={"run","autosourcarr-private","source-observations"};
    int current=open("/",O_PATH|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC),failure=0;struct stat status;
    if (current<0) return 0;
    int valid=fstat(current,&status)==0 && S_ISDIR(status.st_mode) && status.st_nlink!=0
        && status.st_uid==0 && (status.st_mode&(0022|07000))==0;
    for (size_t i=0;valid && i<3;i++) {
        int next=openat(current,parts[i],O_PATH|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC);
        snapshot_close(&current,&failure);current=next;
        valid=current>=0 && failure==0 && fstat(current,&status)==0 && S_ISDIR(status.st_mode) && status.st_nlink!=0;
        if (!valid) break;
        if (i==0) valid=status.st_uid==0 && (status.st_mode&(0022|07000))==0;
        else if (i==1) valid=status.st_uid==568 && status.st_gid==568 && (status.st_mode&07777)==0700
            && (uint64_t)status.st_dev==c->ancestor_device && (uint64_t)status.st_ino==c->ancestor_inode;
        else valid=status.st_uid==c->source_uid && status.st_gid==568 && (status.st_mode&07777)==0770
            && (uint64_t)status.st_dev==c->exchange_device && (uint64_t)status.st_ino==c->exchange_inode;
    }
    snapshot_close(&current,&failure);return valid && failure==0;
}
static int startup_file(const struct stat *status,const struct autosourcarr_source_startup_cleanup_config *c)
{
    return snapshot_regular(status,AUTOSOURCARR_SOURCE_OBSERVATION_JAR_MAXIMUM)
        && status->st_uid==c->source_uid && status->st_gid==c->source_gid
        && ((status->st_mode&07777)==0600 || (status->st_mode&07777)==0444);
}
static int startup_scan(const struct autosourcarr_source_startup_cleanup_config *c,
    struct startup_ticket tickets[AUTOSOURCARR_SOURCE_STARTUP_TICKETS_MAXIMUM],uint32_t *count)
{
    *count=0;
    /* openat('.') creates a fresh directory offset; dup would share end-of-scan
     * state and could falsely report the final scan empty. */
    int descriptor=openat(c->exchange_fd,".",O_RDONLY|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC);
    if (descriptor<0) return -1;
    struct stat held;
    if (fstat(descriptor,&held)!=0 || !snapshot_directory(&held,c->exchange_device,c->exchange_inode)) {
        int failure=ESTALE;snapshot_close(&descriptor,&failure);errno=failure;return -1;
    }
    DIR *directory=fdopendir(descriptor);
    if (directory==NULL) {int failure=errno;snapshot_close(&descriptor,&failure);errno=failure;return -1;}
    int failure=0;
    for (;;) {
        errno=0;struct dirent *entry=readdir(directory);
        if (entry==NULL) {failure=errno;break;}
        if (!strcmp(entry->d_name,".") || !strcmp(entry->d_name,"..")) continue;
        if (*count==AUTOSOURCARR_SOURCE_STARTUP_TICKETS_MAXIMUM || !snapshot_ticket(entry->d_name)) {failure=EFBIG;break;}
        struct startup_ticket *ticket=&tickets[*count];memcpy(ticket->name,entry->d_name,33);
        if (fstatat(c->exchange_fd,ticket->name,&ticket->before,AT_SYMLINK_NOFOLLOW)!=0
            || !startup_file(&ticket->before,c)) {failure=ESTALE;break;}
        (*count)++;
    }
    if (closedir(directory)!=0 && failure==0) failure=errno?errno:EIO;
    if (failure!=0) {errno=failure;return -1;}
    return 0;
}
int autosourcarr_source_observation_startup_cleanup(const struct autosourcarr_source_startup_cleanup_config *c,
    struct autosourcarr_source_startup_cleanup_result *result)
{
    if (result==NULL) {errno=EINVAL;return -1;}
    memset(result,0,sizeof(*result));result->version=1;
    if (c==NULL || c->exchange_fd<3 || c->expected_parent!=1 || c->source_uid==0 || c->source_uid==UINT32_MAX
        || c->source_gid==0 || c->source_gid==UINT32_MAX || c->exchange_inode==0 || c->ancestor_inode==0) {errno=EINVAL;return -1;}
    struct autosourcarr_source_observation_config identity={.uid=568,.gid=568,.expected_parent=1};
    if (snapshot_child_prepare(&identity)!=0) return -1;
    int failure=0;
    struct startup_ticket tickets[AUTOSOURCARR_SOURCE_STARTUP_TICKETS_MAXIMUM];memset(tickets,0,sizeof(tickets));
    uint32_t count=0;
    if (!startup_exchange(c)) {failure=ESTALE;goto done;}
    if (startup_scan(c,tickets,&count)!=0) {result->scanned=count;failure=errno;goto done;}
    result->scanned=count;
    if (!startup_exchange(c)) {failure=ESTALE;goto done;}
    for (uint32_t i=0;i<count;i++) {
        int held=openat(c->exchange_fd,tickets[i].name,O_PATH|O_NOFOLLOW|O_CLOEXEC),close_failure=0;
        struct stat actual,named;
        int valid=held>=0 && fstat(held,&actual)==0 && startup_file(&actual,c) && snapshot_same_file(&tickets[i].before,&actual)
            && fstatat(c->exchange_fd,tickets[i].name,&named,AT_SYMLINK_NOFOLLOW)==0 && snapshot_same_file(&actual,&named)
            && startup_exchange(c);
        if (!valid) failure=ESTALE;
        else if (unlinkat(c->exchange_fd,tickets[i].name,0)!=0) failure=errno;
        else result->removed++;
        snapshot_close(&held,&close_failure);
        if (failure==0 && close_failure!=0) failure=close_failure;
        if (failure!=0) goto done;
    }
    if (fsync(c->exchange_fd)!=0) {failure=errno;goto done;}
    if (startup_scan(c,tickets,&count)!=0) {failure=errno;goto done;}
    if (count!=0 || !startup_exchange(c)) {failure=ESTALE;goto done;}
    result->exchange_device=c->exchange_device;result->exchange_inode=c->exchange_inode;result->complete=1;
done:
    result->error=(uint32_t)failure;explicit_bzero(tickets,sizeof(tickets));return 0;
}
#endif
