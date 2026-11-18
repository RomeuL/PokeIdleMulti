class DeckPCM extends AudioWorkletProcessor {
  constructor() {
    super(); this.buffer = new Int16Array(48000 * 2); this.read = 0; this.write = 0; this.available = 0;
    this.port.onmessage = ({ data }) => {
      const samples = new Int16Array(data);
      for (let i = 0; i < samples.length; i += 2) {
        this.buffer[this.write] = samples[i]; this.buffer[(this.write + 1) % this.buffer.length] = samples[i + 1];
        this.write = (this.write + 2) % this.buffer.length;
        this.available = Math.min(this.available + 2, this.buffer.length);
      }
      // Bound latency after a suspended tab or a slow network.
      if (this.available > 48000 / 2) { this.available = 48000 / 5; this.read = (this.write - this.available + this.buffer.length) % this.buffer.length; }
    };
  }
  process(inputs, outputs) {
    const [left, right] = outputs[0];
    for (let i = 0; i < left.length; i++) {
      if (this.available >= 2) { left[i] = this.buffer[this.read] / 32768; right[i] = this.buffer[(this.read + 1) % this.buffer.length] / 32768; this.read = (this.read + 2) % this.buffer.length; this.available -= 2; }
      else { left[i] = 0; right[i] = 0; }
    }
    return true;
  }
}
registerProcessor('deck-pcm', DeckPCM);
