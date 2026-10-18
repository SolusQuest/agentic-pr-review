const internet = /\b(?:socket|connect|sendto|sendmsg|sendmmsg)\(.*\bAF_INET6?\b/u;

// SocketProtocolSupportPal initializes all three families on the first Unix
// Connect. Its capability checks create datagram sockets and immediately close
// them, without binding or addressing an endpoint. Admit that exact sequence
// once per launched process; every other Internet-family operation is activity.
export function auditNetwork(traces) {
  let capabilityProbes = 0;
  let activity = false;
  for (const trace of traces) {
    const lines = trace.split('\n').map((line) => line.trim());
    for (let index = 0; index < lines.length; index++) {
      const families = ['AF_INET6', 'AF_INET', 'AF_UNIX'];
      const probe = families.every((family, offset) => {
        const protocol = family === 'AF_UNIX' ? '0' : 'IPPROTO_IP';
        const socket = lines[index + offset * 2]?.match(
          new RegExp(
            `^socket\\(${family}, SOCK_DGRAM\\|SOCK_CLOEXEC, ${protocol}\\)\\s+= (\\d+)$`,
            'u',
          ),
        );
        return (
          socket &&
          new RegExp(`^close\\(${socket[1]}\\)\\s+= 0$`, 'u').test(
            lines[index + offset * 2 + 1] || '',
          )
        );
      });
      if (probe) {
        capabilityProbes++;
        index += 5;
      } else if (internet.test(lines[index])) activity = true;
    }
  }
  return { activity: activity || capabilityProbes > 1, capabilityProbes };
}
