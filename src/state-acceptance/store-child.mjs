import { pathToFileURL } from 'node:url';

const [, , bundlePath, command, payloadText] = process.argv;
const payload = JSON.parse(payloadText);
const { ReferenceStateStore } = await import(pathToFileURL(bundlePath).href);

const hooks =
  command === 'register-kill-after-temp'
    ? {
        afterRegistrationTempWrite: () => {
          process.kill(process.pid, 'SIGKILL');
        },
      }
    : undefined;
const store = new ReferenceStateStore(payload.root, hooks);
await store.close();

let result;
switch (command) {
  case 'init':
    result = { kind: 'initialized' };
    break;
  case 'register':
  case 'register-kill-after-temp':
    result = await store.registerCandidate(payload.draft);
    break;
  case 'snapshot': {
    const snapshot = await store.createAcceptanceSnapshot(
      payload.expectedObservedSelectorRevision,
      payload.competingScope,
      payload.selectionSnapshotId,
    );
    result = { cutoff: snapshot.cutoff, registrations: snapshot.registrations.length };
    break;
  }
  case 'marker':
    result = await store.writeMarker(payload.marker);
    break;
  case 'cas':
    result = await store.casSelector(payload.expectedRevision, payload.selector);
    break;
  case 'read-selector': {
    const read = await store.readSelector(payload.stateKey);
    result = { hasBytes: read.bytes !== null, selector: read.selector };
    break;
  }
  default:
    throw new Error(`unknown child command: ${command}`);
}

process.stdout.write(`${JSON.stringify(result)}\n`);
