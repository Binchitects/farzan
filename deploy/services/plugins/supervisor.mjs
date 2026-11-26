// The plugin runtime: runs each plugin's server code (plugin.yaml's server:) as a worker of its own, apart from the
// app and from every other plugin, and relays between them. The app drives it over one socket; each worker speaks the
// framework's protocol (JSON-RPC 2.0, a message a line, on its descriptor 3).
//
// Isolation, per worker:
//   - its own uid and gid (FIRST_UID + n, kept by plugin name in state/uids.json), no other plugin shares them;
//   - its code copied into /work/<name>/code, root's and its group's to read (it cannot change it), and a home of its
//     own (0700) on the container's tmpfs; nothing of the app's volume is readable to it;
//   - rlimits: processes (per uid, so a fork bomb stops at its own), open files, file size, no core files, CPU
//     seconds as a backstop; a V8 heap cap from its memoryMb, and a watch on its resident memory and its CPU;
//   - the container has no network (compose: network_mode: none), a read-only root, no capabilities but those to
//     switch users, own files and kill, and no Docker socket;
//   - a deadline on every call: past it the call fails, and the worker is ended when it does not answer a ping
//     either (its event loop is stuck); a crash fails only its own calls, and it is started again on the next call,
//     waiting longer each time it crashes again soon (at most a minute).
//
// The app's socket (one connection a replica), a JSON message a line:
//   app -> here  {type: "start", plugin, version, files, entry, limits, init}    start (or restart) a worker: files are the
//                                                                              package's (path -> base64), checked by the app
//                                                                              already; written under packages/, root's only
//                {type: "stop", plugin}
//                {type: "call", id, plugin, method, params, timeoutMs, token}   a call to the worker
//                {type: "host.result", hid, result | error}                    the answer to a host API call
//                {type: "status", id}
//   here -> app  {type: "result", id, result | error: {code, message, kind}}   kind: timeout crash protocol memory
//                                                                              cpu busy error off
//                {type: "host", hid, plugin, method, params, token}            a worker's host API call
//                {type: "exit", plugin, reason, code, signal}
//                {type: "log", plugin, line}
//                {type: "status", id, plugins: [...]}
//
// PLUGINS_LISTEN: unix:/plugin-runtime/run/host.sock (compose: the socket's group is the app's) or tcp:0.0.0.0:7700
// with PLUGINS_KEY (Kubernetes: the first line a connection sends is {"type":"hello","key":...}).

import { spawn } from 'node:child_process'
import { timingSafeEqual } from 'node:crypto'
import fs from 'node:fs'
import net from 'node:net'
import path from 'node:path'

const env = process.env
const LISTEN = env.PLUGINS_LISTEN ?? 'unix:/plugin-runtime/run/host.sock'
// The key for tcp: from a file (PLUGINS_KEY_FILE, a secret mounted read-only) or the environment.
const KEY = (env.PLUGINS_KEY_FILE ? fs.readFileSync(env.PLUGINS_KEY_FILE, 'utf8') : env.PLUGINS_KEY ?? '').trim()
const ROOT = env.PLUGINS_ROOT ?? '/plugin-runtime'
const WORK = env.PLUGINS_WORK ?? '/work'
const FIRST_UID = Number(env.PLUGINS_FIRST_UID ?? 40000)
const MOST_PLUGINS = Number(env.PLUGINS_MOST ?? 200)
const SOCKET_GID = Number(env.PLUGINS_SOCKET_GID ?? 1000)
const NODE = env.PLUGINS_NODE ?? process.execPath
// A worker's line at most; the app's may carry a package's files (its start: 64 MB of files, base64).
const MOST_LINE = 4 * 1024 * 1024
const MOST_APP_LINE = 96 * 1024 * 1024
const WATCH_MS = Number(env.PLUGINS_WATCH_MS ?? 250)
const AS_ROOT = process.getuid?.() === 0
// Bounds an app's request can never go past, whatever a manifest or an admin says.
const CEILING = { memoryMb: Number(env.PLUGINS_MOST_MEMORY_MB ?? 1024), processes: 64, openFiles: 1024, fileMb: 64, cpuSeconds: 86400, timeoutMs: 300000, concurrency: 64 }

const log = (...a) => process.stderr.write(`[plugins] ${a.join(' ')}\n`)
const validName = (n) => typeof n === 'string' && /^[a-z0-9][a-z0-9-]{0,49}$/.test(n)
const clamp = (v, lo, hi, d) => (Number.isFinite(v) ? Math.min(hi, Math.max(lo, Math.floor(v))) : d)

// ---- uids, kept by name -------------------------------------------------------------------------------------------

const stateDir = path.join(ROOT, 'state')
const uidsFile = path.join(stateDir, 'uids.json')
let uids = {}
function loadUids() {
  try {
    fs.mkdirSync(stateDir, { recursive: true, mode: 0o700 })
    if (AS_ROOT) fs.chmodSync(stateDir, 0o700)
    uids = JSON.parse(fs.readFileSync(uidsFile, 'utf8'))
  } catch {
    uids = {}
  }
}
function uidOf(name) {
  if (uids[name] === undefined) {
    const used = new Set(Object.values(uids))
    let n = 0
    while (used.has(FIRST_UID + n)) n++
    if (n >= MOST_PLUGINS) throw new Error(`more than ${MOST_PLUGINS} plugins`)
    uids[name] = FIRST_UID + n
    const tmp = uidsFile + '.tmp'
    fs.writeFileSync(tmp, JSON.stringify(uids), { mode: 0o600 })
    fs.renameSync(tmp, uidsFile)
  }
  return uids[name]
}

// ---- files --------------------------------------------------------------------------------------------------------

/** Copies the package's files into the worker's code folder: root's, readable by its group only, never writable. */
function copyCode(source, dest, gid) {
  fs.rmSync(dest, { recursive: true, force: true })
  const walk = (from, to) => {
    fs.mkdirSync(to, { mode: 0o750 })
    if (AS_ROOT) fs.chownSync(to, 0, gid)
    fs.chmodSync(to, 0o750)
    for (const entry of fs.readdirSync(from, { withFileTypes: true })) {
      const a = path.join(from, entry.name)
      const b = path.join(to, entry.name)
      if (entry.isDirectory()) walk(a, b)
      else if (entry.isFile()) {
        fs.copyFileSync(a, b)
        if (AS_ROOT) fs.chownSync(b, 0, gid)
        fs.chmodSync(b, 0o640)
      }
      // Links and anything else: never followed, never copied.
    }
  }
  walk(source, dest)
}

function freshHome(dir, uid) {
  fs.rmSync(dir, { recursive: true, force: true })
  fs.mkdirSync(dir, { mode: 0o700 })
  if (AS_ROOT) fs.chownSync(dir, uid, uid)
  fs.chmodSync(dir, 0o700)
}

/** Every process of a uid ended (what a worker started and left behind). */
function killUid(uid) {
  if (!AS_ROOT) return
  for (const pid of fs.readdirSync('/proc').filter((p) => /^\d+$/.test(p))) {
    try {
      const status = fs.readFileSync(`/proc/${pid}/status`, 'utf8')
      const m = /^Uid:\s+(\d+)/m.exec(status)
      if (m && Number(m[1]) === uid) process.kill(Number(pid), 'SIGKILL')
    } catch {
      // Gone already.
    }
  }
}

function readProc(pid) {
  try {
    const status = fs.readFileSync(`/proc/${pid}/status`, 'utf8')
    const stat = fs.readFileSync(`/proc/${pid}/stat`, 'utf8')
    const fields = stat.slice(stat.lastIndexOf(')') + 2).split(' ')
    return { rssKb: Number(/VmRSS:\s+(\d+)/.exec(status)?.[1] ?? 0), cpuTicks: Number(fields[11]) + Number(fields[12]) }
  } catch {
    return null
  }
}

/** A path a package may hold: relative, forward slashes, no "." or "..", no empty parts. */
function safePath(name) {
  return typeof name === 'string' && name.length > 0 && name.length <= 512 && !name.startsWith('/') && !name.includes('\\') && !/[\u0000-\u001f]/.test(name)
    && name.split('/').every((p) => p !== '' && p !== '.' && p !== '..')
}

/** The package's files written under packages/<plugin>/<version>/ (root's, 0700): the copy each worker's code is made from. */
function writePackage(plugin, version, files) {
  const dir = path.join(ROOT, 'packages', plugin, version)
  const tmp = `${dir}.tmp-${process.pid}-${Date.now()}`
  let total = 0
  fs.rmSync(tmp, { recursive: true, force: true })
  fs.mkdirSync(tmp, { recursive: true, mode: 0o700 })
  for (const [name, b64] of Object.entries(files)) {
    if (!safePath(name)) throw new Error(`${name} is not a path a package may hold`)
    const data = Buffer.from(String(b64), 'base64')
    total += data.length
    if (total > 64 * 1024 * 1024) throw new Error('the package is larger than 64 MB')
    const file = path.join(tmp, name)
    fs.mkdirSync(path.dirname(file), { recursive: true, mode: 0o700 })
    fs.writeFileSync(file, data, { mode: 0o600 })
  }
  fs.rmSync(dir, { recursive: true, force: true })
  fs.mkdirSync(path.dirname(dir), { recursive: true, mode: 0o700 })
  fs.renameSync(tmp, dir)
  return dir
}

// ---- a line-delimited JSON channel --------------------------------------------------------------------------------

function lines(stream, onMessage, onBroken, most = MOST_LINE) {
  let buffer = ''
  stream.setEncoding('utf8')
  stream.on('data', (chunk) => {
    buffer += chunk
    if (buffer.length > most && !buffer.includes('\n')) return onBroken('a line longer than allowed')
    let nl
    while ((nl = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, nl)
      buffer = buffer.slice(nl + 1)
      if (!line.trim()) continue
      if (line.length > most) return onBroken('a line longer than allowed')
      let m
      try {
        m = JSON.parse(line)
      } catch {
        return onBroken('a line that is not JSON')
      }
      if (typeof m !== 'object' || m === null) return onBroken('a message that is not an object')
      onMessage(m)
    }
  })
}

// ---- workers ------------------------------------------------------------------------------------------------------

const plugins = new Map() // name -> Plugin
const apps = new Set() // connections
const tokens = new Map() // token -> connection that minted it (host calls go back there)
let nextHid = 1
const hostCalls = new Map() // hid -> { worker, id }

class Plugin {
  constructor(spec) {
    this.update(spec)
    this.worker = null
    this.starting = null
    this.queue = []
    this.running = 0
    this.crashes = 0 // ended in a row, with no call answered between
    this.notBefore = 0
    this.restarts = 0
  }

  update(spec) {
    const l = spec.limits ?? {}
    this.name = spec.plugin
    this.version = String(spec.version ?? '0')
    this.source = spec.source
    this.entry = spec.entry
    this.init = spec.init ?? {}
    this.limits = {
      memoryMb: clamp(l.memoryMb, 16, CEILING.memoryMb, 128),
      processes: clamp(l.processes, 1, CEILING.processes, 16),
      openFiles: clamp(l.openFiles, 16, CEILING.openFiles, 256),
      fileMb: clamp(l.fileMb, 1, CEILING.fileMb, 16),
      cpuSeconds: clamp(l.cpuSeconds, 1, CEILING.cpuSeconds, 3600),
      timeoutMs: clamp(l.timeoutMs, 100, CEILING.timeoutMs, 10000),
      concurrency: clamp(l.concurrency, 1, CEILING.concurrency, 4),
      // A worker busy on the CPU this share of a core for this long, between or during calls, is ended.
      busyShare: 0.95,
      busyMs: clamp(l.busyMs, 1000, 600000, 30000),
    }
  }

  alive() {
    const w = this.worker
    return w && !w.exited && !w.killedFor && !w.broken
  }

  ensure() {
    if (this.alive()) return Promise.resolve(this.worker)
    const wait = this.notBefore - Date.now()
    if (wait > 0) return Promise.reject(Object.assign(new Error(`it crashed again soon; it starts again in ${Math.ceil(wait / 1000)} s`), { kind: 'crash' }))
    if (!this.starting) this.starting = this.spawn().finally(() => { this.starting = null })
    return this.starting
  }

  async spawn() {
    if (this.restarts++ > 0) log(`${this.name}: starting again (${this.restarts - 1})`)
    const uid = uidOf(this.name)
    const base = path.join(WORK, this.name)
    fs.mkdirSync(base, { recursive: true, mode: 0o711 })
    fs.chmodSync(base, 0o711)
    const code = path.join(base, 'code')
    const home = path.join(base, 'home')
    killUid(uid)
    copyCode(this.source, code, uid)
    freshHome(home, uid)
    const entry = path.join(code, this.entry)
    if (!entry.startsWith(code + '/') || !fs.existsSync(entry)) throw Object.assign(new Error(`its entry ${this.entry} is not in the package`), { kind: 'error' })
    const l = this.limits
    // busybox's ulimit: -u processes, -n files, -f blocks of 512 bytes, -t CPU seconds, -c core files.
    const ulimit = `ulimit -u ${l.processes} -n ${l.openFiles} -f ${l.fileMb * 2048} -t ${l.cpuSeconds} -c 0`
    const child = spawn('/bin/sh', ['-c', `${ulimit} && exec "$0" --max-old-space-size=${l.memoryMb} --max-semi-space-size=8 "$1"`, NODE, entry], {
      cwd: code,
      env: { PATH: '/usr/local/bin:/usr/bin:/bin', HOME: home, TMPDIR: home, NODE_ENV: 'production', ARENA_PLUGIN_PROTOCOL: '1' },
      stdio: ['ignore', 'pipe', 'pipe', 'pipe'],
      ...(AS_ROOT ? { uid, gid: uid } : {}),
    })
    const w = { child, uid, pending: new Map(), next: 1, exited: false, killedFor: null, broken: null, cpu: [], watch: null, logged: { at: 0, n: 0 } }
    this.worker = w
    const channel = child.stdio[3]
    const send = (m) => {
      if (!w.exited && !w.broken) channel.write(JSON.stringify(m) + '\n')
    }
    w.send = send
    w.call = (method, params) => {
      const id = w.next++
      const promise = new Promise((resolve, reject) => {
        w.pending.set(id, { resolve, reject })
        send({ jsonrpc: '2.0', id, method, params })
      })
      promise.id = id
      return promise
    }
    lines(channel, (m) => this.fromWorker(w, m), (why) => this.kill(w, 'protocol', why))
    channel.on('error', () => {})
    const logLines = (stream) => {
      let rest = ''
      stream.setEncoding('utf8')
      stream.on('data', (chunk) => {
        rest += chunk
        let nl
        while ((nl = rest.indexOf('\n')) >= 0) {
          this.log(w, rest.slice(0, nl))
          rest = rest.slice(nl + 1)
        }
        if (rest.length > 4000) {
          this.log(w, rest)
          rest = ''
        }
      })
    }
    logLines(child.stdout)
    logLines(child.stderr)
    child.on('error', (e) => this.kill(w, 'crash', e.message))
    child.on('exit', (code, signal) => {
      w.exited = true
      clearInterval(w.watch)
      killUid(uid)
      const reason = w.killedFor ? `${w.killedFor}${w.why ? `: ${w.why}` : ''}` : signal ? `ended by ${signal}` : `exited with ${code}`
      const kind = w.killedFor ?? (signal === 'SIGXCPU' ? 'cpu' : signal === 'SIGXFSZ' ? 'error' : 'crash')
      for (const [, p] of w.pending) p.reject(Object.assign(new Error(`the worker ended (${reason})`), { kind }))
      w.pending.clear()
      // Ended three times with no call answered between: it is crashing in a loop, and waits before it starts again
      // (1 s, 2 s, 4 s ... at most a minute). A call answered ends the loop.
      if (kind !== 'off' && !this.stopping) {
        this.crashes++
        if (this.crashes >= 3) this.notBefore = Date.now() + Math.min(60000, 1000 * 2 ** (this.crashes - 3))
      }
      broadcast({ type: 'exit', plugin: this.name, reason, code, signal })
      this.pump()
    })
    w.watch = setInterval(() => this.watch(w), WATCH_MS)
    const init = await this.deadline(w, w.call('initialize', { ...this.init, limits: { memoryMb: l.memoryMb, timeoutMs: l.timeoutMs, concurrency: l.concurrency } }), l.timeoutMs, 'initialize')
    w.initialized = init
    return w
  }

  watch(w) {
    const p = readProc(w.child.pid)
    if (!p || w.exited) return
    // Resident memory past the heap's cap and what Node itself needs (buffers outside the heap).
    if (p.rssKb > (this.limits.memoryMb + 96) * 1024) return this.kill(w, 'memory', `${Math.round(p.rssKb / 1024)} MB resident, more than ${this.limits.memoryMb + 96} MB`)
    const now = Date.now()
    w.cpu.push({ at: now, ticks: p.cpuTicks })
    while (w.cpu.length > 2 && now - w.cpu[1].at >= this.limits.busyMs) w.cpu.shift()
    const first = w.cpu[0]
    if (now - first.at >= this.limits.busyMs) {
      const share = (p.cpuTicks - first.ticks) / 100 / ((now - first.at) / 1000)
      if (share >= this.limits.busyShare) this.kill(w, 'cpu', `busy on the CPU ${Math.round(share * 100)}% for ${Math.round((now - first.at) / 1000)} s`)
    }
  }

  log(w, line) {
    const now = Date.now()
    if (now - w.logged.at > 1000) w.logged = { at: now, n: 0 }
    if (++w.logged.n > 100) return // at most 100 lines a second
    broadcast({ type: 'log', plugin: this.name, line: line.slice(0, 2000) })
  }

  kill(w, kind, why) {
    if (w.exited || w.killedFor) return
    w.killedFor = kind
    w.why = why
    try {
      w.child.kill('SIGKILL')
    } catch {
      // It is gone.
    }
    killUid(w.uid)
  }

  fromWorker(w, m) {
    if (m.jsonrpc !== '2.0') return this.kill(w, 'protocol', 'a message that is not JSON-RPC 2.0')
    if (typeof m.method === 'string') {
      if (m.id === undefined || m.id === null) return // notifications: logs go on stderr
      if (!m.method.startsWith('arena.')) return w.send({ jsonrpc: '2.0', id: m.id, error: { code: -32601, message: `There is no method ${m.method}.` } })
      const params = typeof m.params === 'object' && m.params !== null ? m.params : {}
      const token = typeof params.$token === 'string' ? params.$token : null
      delete params.$token
      const app = (token && tokens.get(token)) || [...apps][0]
      if (!app) return w.send({ jsonrpc: '2.0', id: m.id, error: { code: -32004, message: 'Arena is not connected.' } })
      const hid = nextHid++
      hostCalls.set(hid, { w, id: m.id })
      app.send({ type: 'host', hid, plugin: this.name, method: m.method.slice('arena.'.length), params, token })
      return
    }
    const p = w.pending.get(m.id)
    if (!p) return // late, or never asked
    w.pending.delete(m.id)
    if (m.error) p.reject(Object.assign(new Error(String(m.error.message ?? 'error')), { kind: 'error', code: m.error.code }))
    else p.resolve(m.result ?? null)
  }

  /** A call past its deadline fails; the worker is ended too when a ping does not come back (its loop is stuck). */
  deadline(w, promise, ms, what) {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(async () => {
        if (promise.id !== undefined) w.pending.delete(promise.id)
        reject(Object.assign(new Error(`${what} did not answer within ${ms} ms`), { kind: 'timeout' }))
        const pong = await Promise.race([w.call('ping', {}).then(() => true, () => false), new Promise((r) => setTimeout(() => r(false), 1000))])
        if (!pong) this.kill(w, 'timeout', `${what} did not answer within ${ms} ms, nor a ping`)
      }, ms)
      promise.then((v) => { clearTimeout(timer); resolve(v) }, (e) => { clearTimeout(timer); reject(e) })
    })
  }

  call(m, app) {
    if (this.queue.length + this.running >= this.limits.concurrency * 4) {
      return app.send({ type: 'result', id: m.id, error: { code: -32003, message: 'The plugin has too many calls waiting.', kind: 'busy' } })
    }
    this.queue.push({ m, app })
    this.pump()
  }

  pump() {
    while (this.running < this.limits.concurrency && this.queue.length > 0) {
      const { m, app } = this.queue.shift()
      this.running++
      if (typeof m.token === 'string') tokens.set(m.token, app)
      const ms = clamp(m.timeoutMs, 100, this.limits.timeoutMs, this.limits.timeoutMs)
      this.ensure()
        .then((w) => this.deadline(w, w.call(m.method, m.params ?? {}), ms, m.method))
        .then(
          (result) => {
            this.crashes = 0
            app.send({ type: 'result', id: m.id, result })
          },
          (e) => app.send({ type: 'result', id: m.id, error: { code: e.code ?? -32603, message: e.message, kind: e.kind ?? 'error' } }),
        )
        .finally(() => {
          this.running--
          if (typeof m.token === 'string') tokens.delete(m.token)
          this.pump()
        })
    }
  }

  async stop() {
    this.stopping = true
    const w = this.worker
    for (const q of this.queue) q.app.send({ type: 'result', id: q.m.id, error: { code: -32004, message: 'The plugin was stopped.', kind: 'off' } })
    this.queue = []
    if (w && !w.exited) {
      const ended = new Promise((r) => w.child.once('exit', r))
      w.call('shutdown', {}).catch(() => {})
      const t = setTimeout(() => this.kill(w, 'off', 'stopped'), 2000)
      await ended
      clearTimeout(t)
    }
    this.stopping = false
  }

  status() {
    const w = this.worker
    const p = w && !w.exited ? readProc(w.child.pid) : null
    return {
      plugin: this.name, version: this.version, running: !!(w && !w.exited), pid: w?.child.pid ?? null, uid: uids[this.name] ?? null,
      rssKb: p?.rssKb ?? null, restarts: Math.max(0, this.restarts - 1), calls: this.running, waiting: this.queue.length,
      backoffMs: Math.max(0, this.notBefore - Date.now()),
    }
  }
}

// ---- the app's connections ----------------------------------------------------------------------------------------

function broadcast(m) {
  for (const a of apps) a.send(m)
}

async function fromApp(app, m) {
  switch (m.type) {
    case 'start': {
      if (!validName(m.plugin) || !/^[0-9A-Za-z.+-]{1,64}$/.test(String(m.version ?? ''))) {
        return app.send({ type: 'result', id: m.id, error: { code: -32602, message: 'not a plugin name and version', kind: 'error' } })
      }
      if (typeof m.entry !== 'string' || !safePath(m.entry) || typeof m.files !== 'object' || m.files === null || !(m.entry in m.files)) {
        return app.send({ type: 'result', id: m.id, error: { code: -32602, message: 'the package\'s files, with its entry among them, are needed', kind: 'error' } })
      }
      // Another replica's start of the version running already: answered with it, the worker left as it is.
      const running = plugins.get(m.plugin)
      if (running && running.version === String(m.version) && running.alive() && running.worker.initialized) {
        return app.send({ type: 'result', id: m.id, result: running.worker.initialized })
      }
      let source
      try {
        source = writePackage(m.plugin, String(m.version), m.files)
        delete m.files
      } catch (e) {
        return app.send({ type: 'result', id: m.id, error: { code: -32602, message: e.message, kind: 'error' } })
      }
      let p = plugins.get(m.plugin)
      if (p) {
        await p.stop()
        p.update({ ...m, source })
        p.notBefore = 0
        p.crashes = 0
      } else {
        p = new Plugin({ ...m, source })
        plugins.set(m.plugin, p)
      }
      try {
        const w = await p.ensure()
        return app.send({ type: 'result', id: m.id, result: w.initialized })
      } catch (e) {
        return app.send({ type: 'result', id: m.id, error: { code: -32603, message: e.message, kind: e.kind ?? 'error' } })
      }
    }
    case 'stop': {
      const p = plugins.get(m.plugin)
      if (p) {
        plugins.delete(m.plugin)
        await p.stop()
      }
      return m.id !== undefined && app.send({ type: 'result', id: m.id, result: { stopped: !!p } })
    }
    case 'call': {
      const p = plugins.get(m.plugin)
      if (!p) return app.send({ type: 'result', id: m.id, error: { code: -32004, message: `${m.plugin} is not started here`, kind: 'off' } })
      return p.call(m, app)
    }
    case 'host.result': {
      const h = hostCalls.get(m.hid)
      if (!h) return
      hostCalls.delete(m.hid)
      h.w.send(m.error ? { jsonrpc: '2.0', id: h.id, error: m.error } : { jsonrpc: '2.0', id: h.id, result: m.result ?? null })
      return
    }
    case 'status':
      return app.send({ type: 'status', id: m.id, plugins: [...plugins.values()].map((p) => p.status()) })
  }
}

function accept(socket) {
  let authed = !LISTEN.startsWith('tcp:')
  const app = {
    send: (m) => {
      if (!socket.destroyed) socket.write(JSON.stringify(m) + '\n')
    },
  }
  lines(socket, (m) => {
    if (!authed) {
      const given = Buffer.from(String(m.key ?? ''))
      const want = Buffer.from(KEY)
      if (m.type !== 'hello' || KEY.length < 32 || given.length !== want.length || !timingSafeEqual(given, want)) {
        socket.destroy()
        return
      }
      authed = true
      apps.add(app)
      return
    }
    void fromApp(app, m).catch((e) => log(`a request failed: ${e.message}`))
  }, (why) => {
    log(`the app's connection broke: ${why}`)
    socket.destroy()
  }, MOST_APP_LINE)
  if (authed) apps.add(app)
  socket.on('close', () => {
    apps.delete(app)
    for (const [t, a] of tokens) if (a === app) tokens.delete(t)
  })
  socket.on('error', () => {})
}

// ---- start --------------------------------------------------------------------------------------------------------

loadUids()
fs.mkdirSync(WORK, { recursive: true })
fs.chmodSync(WORK, 0o711)
// The packages: written here from what the app sends, root's only; no worker may list or read them.
const packagesDir = path.join(ROOT, 'packages')
fs.mkdirSync(packagesDir, { recursive: true, mode: 0o700 })
if (AS_ROOT) {
  fs.chownSync(packagesDir, 0, 0)
}
fs.chmodSync(packagesDir, 0o700)
const server = net.createServer(accept)
if (LISTEN.startsWith('unix:')) {
  const file = LISTEN.slice('unix:'.length)
  const dir = path.dirname(file)
  fs.mkdirSync(dir, { recursive: true })
  if (AS_ROOT) {
    fs.chownSync(dir, 0, SOCKET_GID)
    fs.chmodSync(dir, 0o750)
  }
  fs.rmSync(file, { force: true })
  server.listen(file, () => {
    if (AS_ROOT) fs.chownSync(file, 0, SOCKET_GID)
    fs.chmodSync(file, 0o660)
    log(`listening on ${file}`)
  })
} else if (LISTEN.startsWith('tcp:')) {
  if (KEY.length < 32) {
    log('PLUGINS_KEY must be at least 32 characters for tcp')
    process.exit(78)
  }
  const [host, port] = LISTEN.slice(4).split(':')
  server.listen(Number(port), host, () => log(`listening on ${host}:${port}`))
} else {
  log(`PLUGINS_LISTEN ${LISTEN} is not unix:<path> or tcp:<host>:<port>`)
  process.exit(78)
}
for (const sig of ['SIGTERM', 'SIGINT']) {
  process.on(sig, async () => {
    await Promise.all([...plugins.values()].map((p) => p.stop()))
    process.exit(0)
  })
}
