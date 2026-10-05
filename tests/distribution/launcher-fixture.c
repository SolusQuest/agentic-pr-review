/* Credential-free synthetic ELF; deliberately not the .NET production runtime. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

int main(void) {
    unsigned char header[4];
    if (fread(header, 1, 4, stdin) != 4) return 90;
    unsigned int size = ((unsigned int)header[0] << 24) | ((unsigned int)header[1] << 16) |
                        ((unsigned int)header[2] << 8) | header[3];
    char body[4096];
    if (size >= sizeof(body) || fread(body, 1, size, stdin) != size) return 91;
    body[size] = 0;
    if (getenv("GITHUB_TOKEN") || getenv("PROVIDER_API_KEY") || getenv("STATE_KEY")) return 92;
    if (strstr(body, "hold")) { for (;;) pause(); }
    const char *result = "{\"fixture\":\"original\"}";
    size = (unsigned int)strlen(result);
    header[0] = (unsigned char)(size >> 24); header[1] = (unsigned char)(size >> 16);
    header[2] = (unsigned char)(size >> 8); header[3] = (unsigned char)size;
    if (fwrite(header, 1, 4, stdout) != 4 || fwrite(result, 1, size, stdout) != size) return 93;
    return 0;
}
