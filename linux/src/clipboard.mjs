import { execFile, spawn } from 'node:child_process';
import { promisify } from 'node:util';
const run = promisify(execFile);

export async function readClipboard() {
  const { stdout } = await run('xclip', ['-selection', 'clipboard', '-out', '-target', 'UTF8_STRING'], { encoding: 'utf8', timeout: 3000, maxBuffer: 1024 * 1024 });
  return stdout;
}
export async function writeClipboard(text) {
  await new Promise((resolve, reject) => {
    const child = spawn('xclip', ['-selection', 'clipboard', '-in', '-target', 'UTF8_STRING'], { stdio: ['pipe', 'ignore', 'ignore'] });
    const timer = setTimeout(() => { child.kill(); reject(new Error('A área de transferência não respondeu')); }, 3000);
    child.on('error', error => { clearTimeout(timer); reject(error); });
    // xclip forks its selection owner; wait for the parent, not its inherited
    // streams. No clipboard text is written to a log or persistent file.
    child.on('exit', code => { clearTimeout(timer); if (code === 0) resolve(); else reject(new Error('Não foi possível atualizar a área de transferência')); });
    child.stdin.on('error', error => { clearTimeout(timer); reject(error); });
    child.stdin.end(text, 'utf8');
  });
}
