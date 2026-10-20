// Test-only handshake; readiness never replaces the actual Host result.
export function nextHostBridge(bridge) {
  return new Promise((resolve, reject) => {
    let settled = false;
    const timer = setTimeout(() => finish(false), 20_000);
    const finish = (ready) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      bridge.off('message', message);
      bridge.off('exit', died);
      bridge.off('error', died);
      if (ready) resolve();
      else reject(new Error('bridge_host_lifetime_failed'));
    };
    const message = (value) => finish(value === 'ready');
    const died = () => finish(false);
    bridge.once('message', message);
    bridge.once('exit', died);
    bridge.once('error', died);
    if (!bridge.connected) return finish(false);
    try {
      bridge.send('next-host', (error) => {
        if (error) finish(false);
      });
    } catch {
      finish(false);
    }
  });
}
