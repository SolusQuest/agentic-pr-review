#include <fcntl.h>
#include <unistd.h>
#ifndef PROBE_VALUE
#define PROBE_VALUE 7001
#endif
#ifdef POISON_MARKER
__attribute__((constructor)) static void poison_loaded(void) {
  int fd = open(POISON_MARKER, O_WRONLY | O_CREAT | O_TRUNC, 0600);
  if (fd >= 0) { (void)write(fd, "executed", 8); (void)close(fd); }
}
#endif
int apr_probe_identity(void) { return PROBE_VALUE; }
