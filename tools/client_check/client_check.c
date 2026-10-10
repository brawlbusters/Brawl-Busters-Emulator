/*
 * Client check module of the Brawl Busters emulator - built as LightFX.dll and put next to pbclient.exe.
 *
 * The game looks for a library of that name when it starts (Alienware keyboard lights, client 0x4019B7) and works
 * the same without it, so this one is loaded into the game by the game itself; the executable is not changed.
 *
 * What it does, once at start and then every minute while the game runs:
 *   1. reads ..\Data\xmandb.bus and makes a SHA-256 digest of its files (name, size, stored bytes of each, in the
 *      order of the archive's table) - all but the server address table, which differs between installations;
 *   2. finds the lobby address of the -ServerGroup the game was started with in that server address table;
 *   3. asks the server for a challenge on the lobby's UDP port and answers SHA-256(challenge + digest + salt).
 * The server (BrawlBusters.Core/Security/ClientCheck.cs) compares that with the digest of its own copy.
 *
 * Datagrams:  "BBIC" 01                 ->      "BBIC" 02 + 16 bytes challenge
 *             "BBIC" 03 + 32 bytes      ->      "BBIC" 04 + 1 (verified) / 0
 *
 * Build: tools\client_check\build.bat (32-bit, like the game).
 */
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <bcrypt.h>
#include <stdlib.h>
#include <string.h>

#define HEADER_OFFSET 0x21
#define HEADER_SIZE 0x110
#define ENTRY_SIZE 0x114
#define NAME_SIZE 0x108
#define KEY_SIZE 0x1000
#define NONCE_SIZE 16
#define HASH_SIZE 32

static const char MAGIC[4] = { 'B', 'B', 'I', 'C' };
static const char SALT[] = "BrawlBusters client check v1";
static unsigned char g_key[KEY_SIZE];

/* The archive's XOR key: mt19937(seed 0x6D2C1B76), each value / 16384 % 105 (client 0x50AEA3). */
static void make_key(void)
{
    static unsigned long state[624];
    int index = 624, produced, k;
    state[0] = 0x6D2C1B76UL;
    for (k = 1; k < 624; k++) state[k] = 1812433253UL * (state[k - 1] ^ (state[k - 1] >> 30)) + (unsigned long)k;
    for (produced = 0; produced < KEY_SIZE; produced++)
    {
        unsigned long y;
        if (index >= 624)
        {
            for (k = 0; k < 624; k++)
            {
                unsigned long mixed = (state[k] & 0x80000000UL) | (state[(k + 1) % 624] & 0x7FFFFFFFUL);
                state[k] = state[(k + 397) % 624] ^ (mixed >> 1) ^ ((mixed & 1) ? 0x9908B0DFUL : 0);
            }
            index = 0;
        }
        y = state[index++];
        y ^= y >> 11;
        y ^= (y << 7) & 0x9D2C5680UL;
        y ^= (y << 15) & 0xEFC60000UL;
        y ^= y >> 18;
        g_key[produced] = (unsigned char)(y / 16384 % 105);
    }
}

static void xor_copy(unsigned char *plain, const unsigned char *stored, unsigned long size)
{
    unsigned long i;
    for (i = 0; i < size; i++) plain[i] = stored[i] ^ g_key[i % KEY_SIZE];
}

static unsigned long u32_at(const unsigned char *p)
{
    return (unsigned long)p[0] | ((unsigned long)p[1] << 8) | ((unsigned long)p[2] << 16) | ((unsigned long)p[3] << 24);
}

static unsigned char *read_file(const char *path, unsigned long *size)
{
    HANDLE file = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    unsigned char *data = NULL;
    DWORD length, done = 0;
    if (file == INVALID_HANDLE_VALUE) return NULL;
    length = GetFileSize(file, NULL);
    if (length != INVALID_FILE_SIZE && length > 0 && (data = (unsigned char *)malloc(length + 1)) != NULL)
    {
        if (!ReadFile(file, data, length, &done, NULL) || done != length) { free(data); data = NULL; }
        else { data[length] = 0; *size = length; }
    }
    CloseHandle(file);
    return data;
}

static int contains_nocase(const char *text, const char *word)
{
    size_t n = strlen(word);
    for (; *text; text++) if (_strnicmp(text, word, n) == 0) return 1;
    return 0;
}

/*
 * The digest of the archive's files, and a decoded copy of the server address table (caller frees it).
 * Returns 0 when the bytes are not an archive.
 */
static int digest_archive(const unsigned char *archive, unsigned long size, unsigned char digest[HASH_SIZE], char **config)
{
    unsigned char header[HEADER_SIZE], entry[ENTRY_SIZE];
    unsigned long count, i, table = HEADER_OFFSET + HEADER_SIZE, payloads;
    BCRYPT_ALG_HANDLE algorithm = NULL;
    BCRYPT_HASH_HANDLE hash = NULL;
    int ok = 0;

    *config = NULL;
    if (size < table) return 0;
    xor_copy(header, archive + HEADER_OFFSET, HEADER_SIZE);
    count = u32_at(header + NAME_SIZE + 4);
    if (count == 0 || count > 100000UL || table + count * ENTRY_SIZE > size) return 0;
    payloads = table + count * ENTRY_SIZE;

    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, NULL, 0) != 0) return 0;
    if (BCryptCreateHash(algorithm, &hash, NULL, 0, NULL, 0, 0) != 0) goto done;

    for (i = 0; i < count; i++)
    {
        unsigned long offset, length, name_length = 0;
        unsigned char size_bytes[4];
        xor_copy(entry, archive + table + i * ENTRY_SIZE, ENTRY_SIZE);
        while (name_length < NAME_SIZE && entry[name_length] != 0) name_length++;
        offset = u32_at(entry + NAME_SIZE);
        length = u32_at(entry + NAME_SIZE + 4);
        if (payloads + offset + length > size || payloads + offset + length < payloads) goto done;

        entry[NAME_SIZE - 1] = 0;
        if (contains_nocase((const char *)entry, "clientconfigdb"))
        {
            /* the table of server addresses: not part of the digest; the one named exactly so is read for the address */
            if (name_length >= 18 && _stricmp((const char *)entry + name_length - 18, "clientconfigdb.xml") == 0 && *config == NULL)
            {
                *config = (char *)malloc(length + 1);
                if (*config != NULL)
                {
                    if (entry[NAME_SIZE + 9]) xor_copy((unsigned char *)*config, archive + payloads + offset, length);
                    else memcpy(*config, archive + payloads + offset, length);
                    (*config)[length] = 0;
                }
            }
            continue;
        }

        memcpy(size_bytes, entry + NAME_SIZE + 4, 4);
        if (BCryptHashData(hash, entry, name_length, 0) != 0) goto done;
        if (BCryptHashData(hash, size_bytes, 4, 0) != 0) goto done;
        if (length > 0 && BCryptHashData(hash, (PUCHAR)(archive + payloads + offset), length, 0) != 0) goto done;
    }
    ok = BCryptFinishHash(hash, digest, HASH_SIZE, 0) == 0;

done:
    if (hash) BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    return ok;
}

static int sha256_3(const void *a, unsigned long a_size, const void *b, unsigned long b_size, const void *c, unsigned long c_size,
    unsigned char out[HASH_SIZE])
{
    BCRYPT_ALG_HANDLE algorithm = NULL;
    BCRYPT_HASH_HANDLE hash = NULL;
    int ok = 0;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, NULL, 0) != 0) return 0;
    if (BCryptCreateHash(algorithm, &hash, NULL, 0, NULL, 0, 0) == 0)
    {
        ok = BCryptHashData(hash, (PUCHAR)a, a_size, 0) == 0 && BCryptHashData(hash, (PUCHAR)b, b_size, 0) == 0
            && BCryptHashData(hash, (PUCHAR)c, c_size, 0) == 0 && BCryptFinishHash(hash, out, HASH_SIZE, 0) == 0;
        BCryptDestroyHash(hash);
    }
    BCryptCloseAlgorithmProvider(algorithm, 0);
    return ok;
}

/* The value of -ServerGroup on the game's command line. */
static int server_group(char *group, size_t room)
{
    const char *line = GetCommandLineA();
    const char *at = line;
    size_t n = 0;
    for (; *at; at++) if (_strnicmp(at, "-ServerGroup", 12) == 0) break;
    if (!*at) return 0;
    at += 12;
    while (*at == ' ' || *at == '\t' || *at == '"') at++;
    while (*at && *at != '"' && *at != ' ' && *at != '\t' && n + 1 < room) group[n++] = *at++;
    group[n] = 0;
    return n > 0;
}

static const char *skip_open(const char *p)
{
    while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n') p++;
    if (strncmp(p, "<![CDATA[", 9) == 0) p += 9;
    while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n') p++;
    return p;
}

/* The first lobby address ("ip:port") of the group's row in the SERVER_ADDR table. */
static int lobby_address(const char *xml, const char *group, char *host, size_t host_room, unsigned short *port)
{
    const char *block = xml;
    size_t group_length = strlen(group);
    while ((block = strstr(block, "<DATA>")) != NULL)
    {
        const char *end = strstr(block, "</DATA>");
        const char *id, *list;
        if (end == NULL) return 0;
        id = strstr(block, "<ID>");
        list = strstr(block, "<ADDR_LIST>");
        if (id != NULL && id < end && list != NULL && list < end)
        {
            const char *name = skip_open(id + 4);
            char after = name[group_length];
            if (strncmp(name, group, group_length) == 0 && (after == '<' || after == ']' || after == ' ' || after == '\r' || after == '\n' || after == '\t'))
            {
                const char *address = skip_open(list + 11);
                size_t n = 0;
                while (*address && *address != ':' && *address != ';' && *address != '<' && n + 1 < host_room) host[n++] = *address++;
                host[n] = 0;
                if (*address != ':' || n == 0) return 0;
                *port = (unsigned short)atoi(address + 1);
                return *port != 0;
            }
        }
        block = end + 7;
    }
    return 0;
}

static int exchange(SOCKET s, const struct sockaddr_in *to, const char *request, int request_size, char *reply, int reply_room, char expected)
{
    struct sockaddr_in from;
    int from_size = sizeof from, got;
    if (sendto(s, request, request_size, 0, (const struct sockaddr *)to, sizeof *to) != request_size) return -1;
    got = recvfrom(s, reply, reply_room, 0, (struct sockaddr *)&from, &from_size);
    if (got < 5 || memcmp(reply, MAGIC, 4) != 0 || reply[4] != expected) return -1;
    return got;
}

/* One proof. Returns 1 when the server answered (whatever its verdict), 0 when it could not be reached. */
static int prove(const char *data_path)
{
    unsigned long size = 0;
    unsigned char *archive = read_file(data_path, &size);
    unsigned char digest[HASH_SIZE], proof[HASH_SIZE];
    char *config = NULL, group[64], host[64], request[5 + HASH_SIZE], reply[64];
    unsigned short port = 0;
    struct sockaddr_in to;
    DWORD timeout = 2000;
    SOCKET s;
    int answered = 0;

    if (archive == NULL) return 0;
    if (!digest_archive(archive, size, digest, &config)) memset(digest, 0, sizeof digest);
    free(archive);
    if (config == NULL || !server_group(group, sizeof group) || !lobby_address(config, group, host, sizeof host, &port))
    {
        free(config);
        return 0;
    }
    free(config);

    memset(&to, 0, sizeof to);
    to.sin_family = AF_INET;
    to.sin_port = htons(port);
    if (inet_pton(AF_INET, host, &to.sin_addr) != 1) return 0;

    s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s == INVALID_SOCKET) return 0;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char *)&timeout, sizeof timeout);

    memcpy(request, MAGIC, 4);
    request[4] = 1;
    if (exchange(s, &to, request, 5, reply, sizeof reply, 2) >= 5 + NONCE_SIZE
        && sha256_3(reply + 5, NONCE_SIZE, digest, HASH_SIZE, SALT, sizeof SALT - 1, proof))
    {
        request[4] = 3;
        memcpy(request + 5, proof, HASH_SIZE);
        answered = exchange(s, &to, request, 5 + HASH_SIZE, reply, sizeof reply, 4) >= 6;
    }
    closesocket(s);
    return answered;
}

static DWORD WINAPI worker(LPVOID unused)
{
    char path[MAX_PATH * 2];
    char *slash;
    WSADATA wsa;
    (void)unused;

    if (GetModuleFileNameA(NULL, path, MAX_PATH) == 0) return 0;
    slash = strrchr(path, '\\');
    if (slash == NULL) return 0;
    strcpy(slash, "\\..\\Data\\xmandb.bus");

    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return 0;
    make_key();
    for (;;) Sleep(prove(path) ? 60000 : 5000);
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        HANDLE thread;
        DisableThreadLibraryCalls(module);
        thread = CreateThread(NULL, 0, worker, NULL, 0, NULL);
        if (thread != NULL) CloseHandle(thread);
    }
    return TRUE;
}
