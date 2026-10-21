/**
 * Tiny HTTP client for the Windows dsh-link agent. No runtime dependencies: the plugin
 * can be installed without pulling a second copy of the DSH packages into the profile.
 */

export class AgentError extends Error {
  constructor(message, options = {}) {
    super(message, options)
    this.name = 'AgentError'
    this.status = options.status
    this.code = options.code
  }
}

export class AgentClient {
  constructor({ baseUrl, token, timeoutMs = 15000, getToken } = {}) {
    this.baseUrl = String(baseUrl || 'http://127.0.0.1:18765').replace(/\/+$/, '')
    this.token = token || ''
    this.getToken = typeof getToken === 'function' ? getToken : undefined
    this.timeoutMs = Number(timeoutMs) > 0 ? Number(timeoutMs) : 15000
  }

  async request(path, { method = 'GET', body, signal, timeoutMs } = {}) {
    const url = `${this.baseUrl}${path.startsWith('/') ? path : `/${path}`}`
    const ms = Number(timeoutMs) > 0 ? Number(timeoutMs) : this.timeoutMs
    const controller = new AbortController()
    const forwardAbort = () => controller.abort(signal?.reason)
    if (signal) {
      if (signal.aborted) controller.abort(signal.reason)
      else signal.addEventListener('abort', forwardAbort, { once: true })
    }
    const timer = setTimeout(() => controller.abort(new Error(`request timed out after ${ms} ms`)), ms)
    const headers = { Accept: 'application/json', 'X-DSH-Board-Client': 'plugin' }
    if (body !== undefined) headers['Content-Type'] = 'application/json'
    const token = this.getToken?.() || this.token
    if (token) headers.Authorization = `Bearer ${token}`

    let response
    try {
      response = await fetch(url, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
        signal: controller.signal,
      })
    } catch (error) {
      const timedOut = controller.signal.aborted && !signal?.aborted
      const reason = timedOut ? `timed out after ${ms} ms` : (error?.message || String(error))
      throw new AgentError(`cannot reach dsh-link at ${this.baseUrl}: ${reason}`, {
        code: timedOut ? 'TIMEOUT' : 'UNREACHABLE',
        cause: error,
      })
    } finally {
      clearTimeout(timer)
      signal?.removeEventListener?.('abort', forwardAbort)
    }

    const text = await response.text()
    let payload
    try {
      payload = text ? JSON.parse(text) : null
    } catch {
      payload = null
    }

    if (!response.ok) {
      const detail = payload?.error || text?.slice(0, 1000) || response.statusText
      const hint = response.status === 401
        ? ' Set DSH_LINK_TOKEN on the DSH server, or sign in once to the DSH board panel with the Windows agent.token.'
        : ''
      throw new AgentError(`dsh-link returned HTTP ${response.status}: ${detail}.${hint}`, {
        status: response.status,
        code: response.status === 401 ? 'UNAUTHORIZED' : 'HTTP_ERROR',
      })
    }
    if (payload === null) {
      throw new AgentError(`dsh-link returned a non-JSON response for ${path}`, { status: response.status })
    }
    return payload
  }

  health(signal) {
    return this.request('/v1/health', { signal })
  }

  config(signal) {
    return this.request('/v1/config', { signal })
  }

  serialPorts(signal) {
    return this.request('/v1/serial/ports', { signal })
  }

  serialSessions(signal) {
    return this.request('/v1/serial/sessions', { signal })
  }

  serialOpen(body, signal) {
    return this.request('/v1/serial/open', { method: 'POST', body, signal })
  }

  serialRead(body, signal) {
    return this.request('/v1/serial/read', {
      method: 'POST',
      body,
      signal,
      timeoutMs: Math.max(this.timeoutMs, Number(body?.timeoutMs || 0) + 5000),
    })
  }

  serialWrite(body, signal) {
    return this.request('/v1/serial/write', { method: 'POST', body, signal })
  }

  serialClose(body, signal) {
    return this.request('/v1/serial/close', { method: 'POST', body, signal })
  }

  serialPulse(body, signal) {
    return this.request('/v1/serial/pulse', { method: 'POST', body, signal })
  }

  adbStatus(signal, refresh = false) {
    return this.request(`/v1/adb/status${refresh ? '?refresh=true' : ''}`, { signal })
  }

  adbShell(body, signal) {
    return this.request('/v1/adb/shell', { method: 'POST', body, signal, timeoutMs: Math.max(this.timeoutMs, Number(body?.timeoutMs || 0) + 5000) })
  }

  adbLogcat(body, signal) {
    return this.request('/v1/adb/logcat', { method: 'POST', body, signal, timeoutMs: Math.max(this.timeoutMs, Number(body?.timeoutMs || 0) + 5000) })
  }

  adbScreenshot(body, signal) {
    return this.request('/v1/adb/screenshot', { method: 'POST', body, signal, timeoutMs: Math.max(this.timeoutMs, 60000) })
  }

  adbPush(body, signal) {
    return this.request('/v1/adb/push', { method: 'POST', body, signal, timeoutMs: Math.max(this.timeoutMs, 120000) })
  }

  adbPull(body, signal) {
    return this.request('/v1/adb/pull', { method: 'POST', body, signal, timeoutMs: Math.max(this.timeoutMs, 120000) })
  }

  adbInstall(body, signal) {
    return this.request('/v1/adb/install', { method: 'POST', body, signal, timeoutMs: Math.max(this.timeoutMs, 180000) })
  }

  adbReboot(body, signal) {
    return this.request('/v1/adb/reboot', { method: 'POST', body, signal })
  }

  exec(body, signal) {
    return this.request('/v1/exec', {
      method: 'POST',
      body,
      signal,
      timeoutMs: Math.max(this.timeoutMs, Number(body?.timeoutMs || 0) + 5000),
    })
  }

  startJob(body, signal) {
    return this.request('/v1/jobs', { method: 'POST', body, signal })
  }

  job(jobId, tailChars, signal) {
    return this.request(`/v1/jobs/${encodeURIComponent(jobId)}?tailChars=${Number(tailChars) || 16384}`, { signal })
  }

  jobs(signal) {
    return this.request('/v1/jobs', { signal })
  }

  killJob(jobId, signal) {
    return this.request(`/v1/jobs/${encodeURIComponent(jobId)}/kill`, { method: 'POST', signal })
  }

  jobInput(jobId, body, signal) {
    return this.request(`/v1/jobs/${encodeURIComponent(jobId)}/stdin`, { method: 'POST', body, signal })
  }

  writeFile(body, signal) {
    return this.request('/v1/files/write', { method: 'POST', body, signal, timeoutMs: Math.max(this.timeoutMs, 120000) })
  }

  readFile(body, signal) {
    return this.request('/v1/files/read', { method: 'POST', body, signal, timeoutMs: Math.max(this.timeoutMs, 120000) })
  }

  listFiles(path, signal) {
    return this.request(`/v1/files/list?path=${encodeURIComponent(path)}`, { signal })
  }
}
