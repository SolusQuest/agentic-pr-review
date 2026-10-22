import { writeFile } from 'node:fs/promises';
import { generateSdkUploadBundle } from '../../../scripts/build-action.mjs';
const [output, build] = process.argv.slice(2);
await writeFile(output, (await generateSdkUploadBundle(process.cwd(), build)).bytes);
