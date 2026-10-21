import assert from 'node:assert/strict'
import { apply } from '../lib/index.js'

const baseUrl = process.env.DSH_LINK_URL || 'http://127.0.0.1:8770'
const token = process.env.DSH_LINK_TOKEN || 'exec-test-token-0123456789abcdef'
const tools = new Map()
apply({ tools: { register: (tool) => tools.set(tool.name, tool) }, logger: { warn: () => {} } }, {
  baseUrl,
  token,
  timeoutMs: 20000,
  artifactDir: '.run/artifacts',
})
assert.equal(tools.size, 24)
const call = (name, args = {}) => tools.get(name).execute(args, { signal: new AbortController().signal })

const status = await call('board_status')
console.log('--- status ---\n' + status.summary)
assert.match(status.summary, /windows exec: enabled/)

const exec = await call('windows_exec', { mode: 'cmd', command: 'echo plugin-exec & cd' })
console.log('\n--- windows_exec ---\n' + exec.summary)
assert.match(exec.summary, /plugin-exec/)

const started = await call('windows_job_start', {
  mode: 'powershell',
  command: '1..20 | ForEach-Object { Write-Output "tick $_"; Start-Sleep -Milliseconds 200 }',
  timeoutMs: 30000,
})
console.log('\n--- job start ---\n' + started.summary)
const jobId = /job-[a-z0-9]+/.exec(started.summary)?.[0]
assert.ok(jobId, 'job id')
await new Promise((resolve) => setTimeout(resolve, 900))
const polled = await call('windows_job_status', { jobId, tailChars: 4000 })
console.log('\n--- job status ---\n' + polled.summary)
assert.match(polled.summary, /tick/)

const killed = await call('windows_job_kill', { jobId })
console.log('\n--- job kill ---\n' + killed.summary)

const list = await call('windows_jobs')
console.log('\n--- jobs ---\n' + list.summary)
assert.match(list.summary, new RegExp(jobId))

console.log('\nWINDOWS SMOKE OK')
