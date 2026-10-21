import assert from 'node:assert/strict'
import http from 'node:http'
import { apply, rewriteDashboardHtml } from '../lib/index.js'
import { AgentClient } from '../lib/client.js'

const tools = new Map()
const ctx = {
  tools: { register: (definition) => tools.set(definition.name, definition) },
  logger: { warn: () => {} },
}

apply(ctx, { baseUrl: 'http://127.0.0.1:59999', token: 'unit-test-token', timeoutMs: 800 })
assert.equal(tools.size, 24)
assert.equal(tools.get('board_status').parameters.type, 'object')
assert.equal(tools.get('board_adb_shell').parameters.required[0], 'command')
assert.equal(tools.get('windows_exec').parameters.required[0], 'command')
assert.equal(tools.get('windows_job_start').parameters.required[0], 'command')
assert.equal(tools.get('windows_job_status').parameters.required[0], 'jobId')
assert.equal(tools.get('windows_write_file').parameters.required[0], 'localPath')

for (const [name, tool] of tools) {
  assert.equal(typeof tool.description, 'string', `${name} description`)
  assert.equal(tool.parameters.type, 'object', `${name} parameters type`)
  assert.equal(tool.parameters.additionalProperties, false, `${name} parameters additionalProperties`)
  assert.equal(tool.output.schema.type, 'object', `${name} output type`)
  assert.equal(typeof tool.output.render, 'function', `${name} render`)
  assert.equal(typeof tool.execute, 'function', `${name} execute`)
}

await assert.rejects(
  () => tools.get('board_status').execute({}, { signal: new AbortController().signal }),
  (error) => {
    assert.match(error.message, /cannot reach dsh-link/)
    assert.match(error.message, /Hint:/)
    return true
  },
)

await assert.rejects(
  () => tools.get('board_serial_open').execute({ port: 'COM9' }, { signal: new AbortController().signal }),
  (error) => {
    assert.match(error.message, /cannot reach dsh-link/)
    return true
  },
)

const oldHtml = `<script>api('/v1/health'); fetch('/ui/api/session');</script>`
const rewritten = rewriteDashboardHtml(oldHtml)
assert.match(rewritten, /api\('\/board-link\/v1\/health'\)/)
assert.match(rewritten, /fetch\('\/board-link\/ui\/api\/session'\)/)
const newHtml = `<script>api('v1/health'); api('ui/api/session');</script>`
assert.equal(rewriteDashboardHtml(newHtml), newHtml)

let runtimeToken = ''
const mock = http.createServer((req, res) => {
  if (req.headers.authorization !== `Bearer ${runtimeToken}`) {
    res.writeHead(401, { 'content-type': 'application/json' })
    res.end(JSON.stringify({ error: 'unauthorized' }))
    return
  }
  res.writeHead(200, { 'content-type': 'application/json' })
  res.end(JSON.stringify({ ok: true }))
})
await new Promise((resolve) => mock.listen(0, '127.0.0.1', resolve))
const port = mock.address().port
const dynamic = new AgentClient({ baseUrl: `http://127.0.0.1:${port}`, getToken: () => runtimeToken })
runtimeToken = 'captured-from-panel'
assert.equal((await dynamic.health()).ok, true)
await new Promise((resolve) => mock.close(resolve))

console.log('UNIT OK: 24 tools registered, schemas shaped, proxy compatibility rewritten')
