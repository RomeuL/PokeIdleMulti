import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { EventEmitter } from 'node:events';

export class JsonProcess extends EventEmitter {
  #next = 0;
  #pending = new Map();
  constructor(command, args, options = {}) {
    super();
    this.child = spawn(command, args, { ...options, stdio: ['pipe', 'pipe', 'pipe'] });
    this.child.stderr.on('data', data => process.stderr.write(data));
    this.child.on('error', error => this.#fail(error));
    this.child.on('exit', code => {
      this.#fail(new Error(`${command} exited (${code})`));
      this.emit('exit', code);
    });
    createInterface({ input: this.child.stdout }).on('line', line => {
      let message;
      try { message = JSON.parse(line); } catch { return; }
      if (message.eventName) return this.emit(message.eventName, message);
      const request = this.#pending.get(message.id);
      if (!request) return;
      this.#pending.delete(message.id);
      clearTimeout(request.timer);
      if (message.error) request.reject(new Error(message.error));
      else request.resolve(message.result);
    });
  }
  call(method, args = {}, timeout = 180_000) {
    const id = ++this.#next;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.#pending.delete(id);
        reject(new Error(`Tempo esgotado: ${method}`));
      }, timeout);
      this.#pending.set(id, { resolve, reject, timer });
      this.child.stdin.write(JSON.stringify({ id, method, args }) + '\n', error => {
        if (error) {
          clearTimeout(timer);
          this.#pending.delete(id);
          reject(error);
        }
      });
    });
  }
  #fail(error) {
    for (const request of this.#pending.values()) {
      clearTimeout(request.timer);
      request.reject(error);
    }
    this.#pending.clear();
  }
  close() { this.child.stdin.end(); }
}
