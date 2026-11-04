(function (root, factory) {
  const runtime = factory();
  if (typeof module === "object" && module.exports) module.exports = runtime;
  else root.HandlerDashboardRuntime = runtime;
})(globalThis, function () {
  "use strict";

  function createApiClient({ fetch: send, readTimeoutMs = 12000, timeoutSignal = ms => AbortSignal.timeout(ms) }) {
    const pendingReads = new Map();
    function request(path, options = {}) {
      const method = (options.method || "GET").toUpperCase();
      // Only plain, identical reads share work. Writes and reads with a caller's
      // cancellation or other custom options keep their independent semantics.
      const share = method === "GET" && Object.keys(options).every(key => key === "method");
      if (share && pendingReads.has(path)) return pendingReads.get(path);
      const execute = async () => {
        const signal = options.signal || (method === "GET" ? timeoutSignal(readTimeoutMs) : undefined);
        let response, text;
        try {
          response = await send(path, {
            ...options, signal,
            headers: { "Content-Type": "application/json", ...(options.headers || {}) }
          });
          text = await response.text();
        } catch (error) {
          if (options.signal?.aborted) throw error;
          const unavailable = new Error(signal?.aborted
            ? "The Handler took too long to respond. Try again in a moment."
            : "The dashboard cannot reach your Handler. Open John's Twitch Handler from your Desktop or Start Menu, then try again.");
          unavailable.code = signal?.aborted ? "handler-timeout" : "handler-unavailable";
          throw unavailable;
        }
        let data = null;
        try { data = text ? JSON.parse(text) : null; } catch { data = text || null; }
        if (!response.ok) throw new Error(data?.error || `The request could not be completed (${response.status}).`);
        return data;
      };
      const operation = execute();
      if (!share) return operation;
      const shared = operation.finally(() => pendingReads.delete(path));
      pendingReads.set(path, shared);
      return shared;
    }
    return { request };
  }

  function createPoller({ setTimeout: schedule, clearTimeout: cancel, paused = false, onError = () => {} }) {
    const jobs = new Set();
    let suspended = paused;
    let stopped = false;
    function arm(job, delay) {
      if (stopped || suspended || job.stopped || job.running || job.timer !== null) return;
      job.timer = schedule(() => tick(job), delay);
    }
    async function tick(job) {
      job.timer = null;
      if (stopped || suspended || job.stopped || job.running) return;
      job.running = true;
      try { if (job.active()) await job.task(); }
      catch (error) { onError(error); }
      finally { job.running = false; arm(job, job.delay); }
    }
    function add(task, delay, active = () => true) {
      if (!(delay > 0)) throw new Error("Polling delay must be positive.");
      const job = { task, delay, active, timer: null, running: false, stopped: false };
      jobs.add(job); arm(job, delay);
      return () => { job.stopped = true; if (job.timer !== null) cancel(job.timer); jobs.delete(job); };
    }
    function setPaused(value) {
      suspended = Boolean(value);
      for (const job of jobs) {
        if (suspended) { if (job.timer !== null) cancel(job.timer); job.timer = null; }
        else arm(job, 0);
      }
    }
    function stop() { stopped = true; setPaused(true); jobs.clear(); }
    return { add, setPaused, stop };
  }
  return { createApiClient, createPoller };
});
