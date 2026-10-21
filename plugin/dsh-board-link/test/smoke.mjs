import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { apply } from '../lib/index.js'

const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(here, '../../..')
const configPath = process.env.DSH_LINK_CONFIG || path.join(root, '.run', 'config.json')
let config
try {
  config = JSON.parse((await fs.readFile(configPath, 'utf8')).replace(/^\uFEFF/, ''))
} catch (error) {
  console.error(`Cannot read agent config at ${configPath}.`)
  console.error('Start the agent first, or set DSH_LINK_CONFIG to its config.json.')
  console.error(`Cause: ${error.message}`)
  process.exit(2)
}

const token = process.env.DSH_LINK_TOKEN || config.agent?.token
const agentPort = Number(process.env.DSH_LINK_PORT || config.agent?.port || 8765)
const serialPort = process.env.DSH_LINK_SERIAL_PORT || 'COM26'
const tools = new Map()
const ctx = {
  tools: { register: (definition) => tools.set(definition.name, definition) },
  logger: { warn: (message) => console.error(message) },
}

apply(ctx, {
  baseUrl: `http://127.0.0.1:${agentPort}`,
  token,
  timeoutMs: 20000,
  artifactDir: path.join(root, '.run', 'artifacts'),
})

assert.equal(tools.size, 15)
for (const [name, tool] of tools) {
  assert.equal(typeof tool.execute, 'function', `${name} execute`)
  assert.equal(tool.parameters.type, 'object', `${name} parameters object`)
  assert.equal(tool.output.schema.type, 'object', `${name} output object`)
}

const call = async (name, args = {}) => {
  const tool = tools.get(name)
  assert.ok(tool, `missing tool ${name}`)
  return tool.execute(args, { signal: new AbortController().signal })
}

const status = await call('board_status')
console.log('--- board_status ---\n' + status.summary)
assert.match(status.summary, /ONLINE/)

const ports = await call('board_serial_list')
console.log('\n--- serial ports ---\n' + ports.summary)
assert.match(ports.summary, new RegExp(serialPort))
try {
  const listed = JSON.parse(ports.details || '{}')
  const openSessions = listed.sessions?.sessions || listed.sessions || []
  for (const session of openSessions.filter((item) => item.port === serialPort)) {
    console.log(`Closing stale session ${session.sessionId} for ${serialPort}`)
    await call('board_serial_close', { sessionId: session.sessionId })
  }
} catch {
  // A cleanup failure is reported by the open call below.
}

const devices = await call('board_adb_devices')
console.log('\n--- adb devices ---\n' + devices.summary)
let adbReady = false
try {
  const parsed = JSON.parse(devices.details || '{}')
  adbReady = (parsed.devices || []).some((device) => device.state === 'device')
} catch {
  adbReady = false
}
if (process.env.DSH_LINK_REQUIRE_ADB === '1') assert.ok(adbReady, 'no ready ADB device')
if (!adbReady) console.log('\n(ADB device is not ready; skipping shell/logcat/push/pull. Set DSH_LINK_REQUIRE_ADB=1 to fail instead.)')

if (adbReady) {
  const shell = await call('board_adb_shell', { command: 'uname -a' })
  console.log('\n--- adb shell ---\n' + shell.summary)
  assert.match(shell.summary, /Linux|Android/)

  const logs = await call('board_adb_logcat', { lines: 5 })
  console.log('\n--- logcat ---\n' + logs.summary)
  assert.match(logs.summary, /linux|android/i)
}

let sessionId
try {
  const opened = await call('board_serial_open', { port: serialPort, baudRate: 115200 })
  console.log('\n--- serial open ---\n' + opened.summary)
  sessionId = /sessionId: (ser-[a-z0-9-]+)/.exec(opened.summary)?.[1]
  assert.ok(sessionId, 'session id')

  const read = await call('board_serial_read', { sessionId, timeoutMs: 300 })
  console.log('\n--- serial read ---\n' + read.summary)
  const write = await call('board_serial_write', { sessionId, text: '', newline: 'crlf' })
  console.log('\n--- serial write ---\n' + write.summary)
  const read2 = await call('board_serial_read', { sessionId, timeoutMs: 1000 })
  console.log('\n--- serial read after write ---\n' + read2.summary)
  assert.match(read2.summary, /Read \d+ bytes|No serial data/)
} finally {
  if (sessionId) await call('board_serial_close', { sessionId }).catch(() => {})
}

if (adbReady) {
  const local = path.join(root, '.run', 'push-test.txt')
  const content = `dsh-link push test ${Date.now()}\n`
  await fs.mkdir(path.dirname(local), { recursive: true })
  await fs.writeFile(local, content)
  const push = await call('board_adb_push', { localPath: local, remotePath: '/tmp/dsh-link-push-test.txt' })
  console.log('\n--- adb push ---\n' + push.summary)
  assert.match(push.summary, /Pushed/)
  const pull = await call('board_adb_pull', { remotePath: '/tmp/dsh-link-push-test.txt' })
  console.log('\n--- adb pull ---\n' + pull.summary)
  const pulledPath = /to:\n(.+)$/m.exec(pull.summary)?.[1]?.trim()
  assert.ok(pulledPath, 'pulled path')
  assert.equal(await fs.readFile(pulledPath, 'utf8'), content)
}

console.log(adbReady ? '\nSMOKE OK' : '\nSMOKE OK (serial only; ADB device not ready)')
