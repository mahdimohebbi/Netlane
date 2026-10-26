import { mbps } from "./core.js";

const BASE = "https://speed.cloudflare.com";
const CAP_MS = 8000;

export async function latency() {
  const url = `${BASE}/__down?bytes=0`;
  const signal = AbortSignal.timeout(CAP_MS);
  await fetch(url, { cache: "no-store", signal }).catch(() => {}); // warm up DNS/TLS
  const samples = [];
  for (let i = 0; i < 3; i++) {
    const start = performance.now();
    const response = await fetch(url, { cache: "no-store", signal });
    await response.arrayBuffer();
    samples.push(performance.now() - start);
  }
  samples.sort((a, b) => a - b);
  return samples[1];
}

/** Reads the stream until it ends or the time cap hits, then measures what arrived. */
export async function download() {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), CAP_MS);
  const start = performance.now();
  let bytes = 0;
  try {
    const response = await fetch(`${BASE}/__down?bytes=25000000`, { cache: "no-store", signal: controller.signal });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const reader = response.body.getReader();
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      bytes += value.byteLength;
    }
  } catch (e) {
    if (bytes === 0) throw e;
  } finally {
    clearTimeout(timer);
  }
  return mbps(bytes, (performance.now() - start) / 1000);
}

/** fetch() has no upload progress, so send 2 MB chunks until the cap and count the ones that completed. */
export async function upload() {
  const chunk = new Uint8Array(2_000_000);
  const deadline = performance.now() + CAP_MS;
  const start = performance.now();
  let bytes = 0;
  let lastDone = start; // time of the last completed chunk, so an aborted chunk doesn't dilute the rate
  while (bytes < 10_000_000 && performance.now() < deadline) {
    const remaining = Math.max(500, Math.round(deadline - performance.now()));
    try {
      const response = await fetch(`${BASE}/__up`, {
        method: "POST",
        body: chunk,
        headers: { "Content-Type": "application/octet-stream" },
        signal: AbortSignal.timeout(remaining),
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      bytes += chunk.byteLength;
      lastDone = performance.now();
    } catch (e) {
      if (bytes === 0) throw e;
      break;
    }
  }
  return mbps(bytes, (lastDone - start) / 1000);
}
