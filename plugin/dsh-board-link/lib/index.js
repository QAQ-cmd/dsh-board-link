import fs from 'node:fs/promises'
import { chmodSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import http from 'node:http'
import os from 'node:os'
import path from 'node:path'
import { AgentClient, AgentError } from './client.js'

/**
 * DSH plugin for a Windows board lab.
 *
 * The plugin deliberately imports nothing from @deepseek-ai/*: tools are registered with
 * the stable raw-JSON-Schema shape, so installation does not need a second copy of DSH
 * packages in the profile's node_modules.
 */

export const name = 'board-link'
export const inject = ['tools']

const DEFAULT_BASE_URL = 'http://127.0.0.1:18765'
const DEFAULT_TIMEOUT_MS = 15000
const DEFAULT_MAX_TRANSFER_BYTES = 64 * 1024 * 1024

export function apply(ctx, config = {}) {
  const baseUrl = config.baseUrl || process.env.DSH_LINK_URL || DEFAULT_BASE_URL
  const tokenEnv = config.tokenEnv || 'DSH_LINK_TOKEN'
  const configuredToken = config.token || process.env[tokenEnv] || ''
  const storedToken = readStoredToken()
  const token = configuredToken || storedToken
  let runtimeToken = token
  const timeoutMs = positive(config.timeoutMs, DEFAULT_TIMEOUT_MS)
  const maxTransferBytes = positive(config.maxTransferBytes, DEFAULT_MAX_TRANSFER_BYTES)
  const artifactDir = path.resolve(config.artifactDir || path.join(os.homedir(), '.dsh', 'board-link', 'artifacts'))
  const client = new AgentClient({ baseUrl, token, timeoutMs, getToken: () => runtimeToken })

  if (!token) {
    ctx.logger?.warn?.(`[board-link] no agent token yet: sign in once to the DSH board panel (or set ${tokenEnv})`)
  }

  let registered = 0
  const define = ({ parameters, ...definition }) => {
    ctx.tools.register({
      ...definition,
      parameters: objectSchema(parameters.properties, parameters.required),
      output: textOutput,
    })
    registered += 1
  }

  define({
    name: 'board_status',
    description: 'Check the Windows DSH Link agent: SSH tunnel state, serial ports and open sessions, ADB devices, and version information. Call this first when board access is unavailable.',
    parameters: {
      properties: {
        verbose: { type: 'boolean', description: 'Include the full /v1/health JSON in the result.' },
      },
      required: [],
    },
    isConcurrencySafe: () => true,
    async execute(args, exec) {
      try {
        const health = await client.health(exec.signal)
        return statusResult(health, args.verbose === true, { baseUrl, tokenConfigured: Boolean(token) })
      } catch (error) {
        throw new Error(statusFailure(error, baseUrl))
      }
    },
  })

  define({
    name: 'board_serial_list',
    description: 'List serial ports available on the Windows PC, plus serial sessions currently open in the DSH Link agent.',
    parameters: {
      properties: {
        includeSessions: { type: 'boolean', description: 'Include open sessions. Default true.' },
      },
      required: [],
    },
    isConcurrencySafe: () => true,
    async execute(args, exec) {
      const [ports, sessions] = await Promise.all([
        client.serialPorts(exec.signal),
        args.includeSessions === false ? Promise.resolve({ sessions: [] }) : client.serialSessions(exec.signal),
      ])
      const lines = []
      lines.push(ports.ports?.length ? `Serial ports (${ports.ports.length}):` : 'No serial ports detected by the Windows agent.')
      for (const port of ports.ports || []) {
        lines.push(`- ${port.name}${port.device ? ` (${port.device})` : ''}`)
      }
      if (args.includeSessions !== false) {
        lines.push('', sessions.sessions?.length ? `Open sessions (${sessions.sessions.length}):` : 'No open serial sessions.')
        for (const session of sessions.sessions || []) {
          lines.push(`- ${session.sessionId}: ${session.port} @ ${session.baudRate} ${session.dataBits}${parityShort(session.parity)}${stopShort(session.stopBits)}, in=${session.totalBytesIn}B, buffered=${session.bufferedBytes}B${session.faulted ? ' FAULTED' : ''}`)
        }
      }
      return result(lines.join('\n'), clip(JSON.stringify({ ports, sessions }, null, 2), 20000))
    },
  })

  define({
    name: 'board_serial_open',
    description: 'Open a Windows serial port through the DSH Link agent. The session stays open across tool calls until board_serial_close. Common embedded settings: 115200 baud, 8N1, handshake none.',
    parameters: {
      properties: {
        port: { type: 'string', description: 'Windows port name, for example COM26.' },
        baudRate: { type: 'integer', description: 'Baud rate, default 115200.' },
        dataBits: { type: 'integer', enum: [5, 6, 7, 8], description: 'Data bits, default 8.' },
        parity: { type: 'string', enum: ['none', 'even', 'odd', 'mark', 'space'], description: 'Parity, default none.' },
        stopBits: { type: 'string', enum: ['one', 'onepointfive', 'two'], description: 'Stop bits, default one.' },
        handshake: { type: 'string', enum: ['none', 'hardware', 'xonxoff'], description: 'Flow control, default none.' },
        dtr: { type: 'boolean', description: 'Raise DTR after opening. Default true; set false for boards that reset on DTR.' },
        rts: { type: 'boolean', description: 'Raise RTS after opening. Default true.' },
      },
      required: ['port'],
    },
    async execute(args, exec) {
      const session = await client.serialOpen({
        port: args.port,
        baudRate: args.baudRate,
        dataBits: args.dataBits,
        parity: args.parity,
        stopBits: args.stopBits,
        handshake: args.handshake,
        dtr: args.dtr,
        rts: args.rts,
      }, exec.signal)
      return result(
        `Opened ${session.port} @ ${session.baudRate} ${session.dataBits}${parityShort(session.parity)}${stopShort(session.stopBits)} (${session.handshake}).\n` +
        `sessionId: ${session.sessionId}\nUse board_serial_read with this id. Close it with board_serial_close when finished.`,
        clip(JSON.stringify(session, null, 2), 12000),
      )
    },
  })

  define({
    name: 'board_serial_read',
    description: 'Read buffered bytes from an open Windows serial session. Waits up to timeoutMs for new data. Text is returned when printable; otherwise base64 is returned.',
    parameters: {
      properties: {
        sessionId: { type: 'string', description: 'Session id returned by board_serial_open.' },
        timeoutMs: { type: 'integer', description: 'How long to wait for data, 0-60000 ms. Default 1000.' },
        maxBytes: { type: 'integer', description: 'Maximum bytes to drain, 1-262144. Default 8192.' },
        peek: { type: 'boolean', description: 'Return data without consuming it from the agent buffer.' },
      },
      required: ['sessionId'],
    },
    timeoutMs: 70000,
    async execute(args, exec) {
      const read = await client.serialRead({
        sessionId: args.sessionId,
        timeoutMs: args.timeoutMs ?? 1000,
        maxBytes: args.maxBytes ?? 8192,
        peek: args.peek ?? false,
      }, exec.signal)
      const details = JSON.stringify({
        bytes: read.bytes,
        totalBytesIn: read.totalBytesIn,
        droppedBytes: read.droppedBytes,
        bufferedBytes: read.bufferedBytes,
        truncated: read.truncated,
        faulted: read.faulted,
        error: read.error,
        modem: read.modem,
      }, null, 2)
      if (!read.bytes) {
        const why = read.faulted ? `\nSerial reader is faulted: ${read.error || 'unknown error'}` : ''
        return result(`No serial data within ${args.timeoutMs ?? 1000} ms (session ${read.port}).${why}`, details)
      }
      const printable = /^[\t\n\r\x20-\x7e\u00a0-\uffff]*$/.test(read.text || '')
      if (printable) {
        return result(`Read ${read.bytes} bytes from ${read.port} (session ${args.sessionId}):\n${read.text}`, details)
      }
      return result(
        `Read ${read.bytes} binary bytes from ${read.port} (session ${args.sessionId}); base64:\n${read.base64}\n` +
        `(Use board_serial_read with peek=false to consume the buffer.)`,
        details,
      )
    },
  })

  define({
    name: 'board_serial_write',
    description: 'Write text or base64 bytes to an open Windows serial session. Useful for console commands, bootloader input, and AT commands.',
    parameters: {
      properties: {
        sessionId: { type: 'string', description: 'Session id returned by board_serial_open.' },
        text: { type: 'string', description: 'UTF-8 text to write. Provide exactly one of text or base64.' },
        base64: { type: 'string', description: 'Base64 bytes to write. Provide exactly one of text or base64.' },
        newline: { type: 'string', enum: ['none', 'lf', 'crlf'], description: 'Append a newline to text. Default none.' },
      },
      required: ['sessionId'],
    },
    async execute(args, exec) {
      const write = await client.serialWrite({
        sessionId: args.sessionId,
        text: args.text,
        base64: args.base64,
        newline: args.newline,
      }, exec.signal)
      return result(`Wrote ${write.bytesWritten} bytes to session ${args.sessionId}.`, clip(JSON.stringify(write, null, 2), 4000))
    },
  })

  define({
    name: 'board_serial_close',
    description: 'Close an open Windows serial session and release the COM port.',
    parameters: {
      properties: {
        sessionId: { type: 'string', description: 'Session id returned by board_serial_open.' },
      },
      required: ['sessionId'],
    },
    async execute(args, exec) {
      try {
        await client.serialClose({ sessionId: args.sessionId }, exec.signal)
        return result(`Closed serial session ${args.sessionId}.`)
      } catch (error) {
        if (error instanceof AgentError && error.status === 404) return result(`Serial session ${args.sessionId} was already closed.`)
        throw error
      }
    },
  })

  define({
    name: 'board_serial_pulse',
    description: 'Pulse DTR or RTS on an open serial session, for example to reset an MCU or enter a bootloader. The line returns high after the pulse.',
    parameters: {
      properties: {
        sessionId: { type: 'string', description: 'Session id returned by board_serial_open.' },
        line: { type: 'string', enum: ['dtr', 'rts'], description: 'Control line to pulse. Default dtr.' },
        durationMs: { type: 'integer', description: 'Pulse width in milliseconds, 10-5000. Default 100.' },
      },
      required: ['sessionId'],
    },
    async execute(args, exec) {
      const pulse = await client.serialPulse({
        sessionId: args.sessionId,
        line: args.line || 'dtr',
        durationMs: args.durationMs ?? 100,
      }, exec.signal)
      return result(`Pulsed ${pulse.line} for ${pulse.durationMs} ms on session ${pulse.sessionId}.`)
    },
  })

  define({
    name: 'board_adb_devices',
    description: 'List ADB devices connected to the Windows PC, including state and model when available. Works for Android and for Buildroot/Linux boards exposing adb shell.',
    parameters: { properties: { refresh: { type: 'boolean', description: 'Bypass the agent cache.' } }, required: [] },
    isConcurrencySafe: () => true,
    async execute(args, exec) {
      const status = await client.adbStatus(exec.signal, args.refresh === true)
      const lines = []
      if (!status.available) {
        lines.push(`ADB is not available: ${status.error || 'unknown error'}`)
        lines.push(status.path ? `agent adb path: ${status.path}` : '')
      } else {
        lines.push(`ADB ${status.version || ''}`.trim())
        if (!status.devices?.length) lines.push('No devices. Check USB debugging / adb authorization on the board.')
        for (const device of status.devices || []) {
          const attrs = [device.state, device.model, device.product, device.device].filter(Boolean).join(', ')
          lines.push(`- ${device.serial}: ${attrs}`)
        }
      }
      return result(lines.filter(Boolean).join('\n'), clip(JSON.stringify(status, null, 2), 16000))
    },
  })

  define({
    name: 'board_adb_shell',
    description: 'Run a shell command on the ADB device through the Windows PC and return stdout/stderr/exit code. The device sees the command exactly as adb shell receives it. Works on Android and Buildroot Linux boards.',
    parameters: {
      properties: {
        command: { type: 'string', description: 'Command to run on the board, for example: uname -a, dmesg | tail -30, cat /proc/cmdline.' },
        serial: { type: 'string', description: 'ADB serial. Omit when exactly one device is connected.' },
        timeoutMs: { type: 'integer', description: 'Command timeout in milliseconds. Default 30000.' },
      },
      required: ['command'],
    },
    timeoutMs: 70000,
    async execute(args, exec) {
      const r = await client.adbShell({
        command: args.command,
        serial: args.serial,
        timeoutMs: args.timeoutMs ?? 30000,
      }, exec.signal)
      const head = `adb shell (${args.serial || 'auto'}) -> exit ${r.exitCode}${r.timedOut ? ' [TIMEOUT]' : ''} in ${r.durationMs} ms`
      const out = r.stdout ? `\n[stdout]\n${clip(r.stdout, 24000)}` : ''
      const err = r.stderr ? `\n[stderr]\n${clip(r.stderr, 8000)}` : ''
      return result(head + out + err, clip(JSON.stringify(r, null, 2), 30000))
    },
  })

  define({
    name: 'board_adb_logcat',
    description: 'Read recent board logs over ADB. Auto-detects Android logcat vs Buildroot/Linux (tail of /var/log/messages or dmesg). For custom log files use board_adb_shell.',
    parameters: {
      properties: {
        lines: { type: 'integer', description: 'Number of lines, 1-5000. Default 200.' },
        serial: { type: 'string', description: 'ADB serial. Omit when exactly one device is connected.' },
        filter: { type: 'string', description: 'Android logcat filter spec, or a fixed substring for Linux logs.' },
        platform: { type: 'string', enum: ['auto', 'android', 'linux'], description: 'Log backend. Default auto.' },
        timeoutMs: { type: 'integer', description: 'Timeout in milliseconds. Default 30000.' },
      },
      required: [],
    },
    timeoutMs: 70000,
    isConcurrencySafe: () => true,
    async execute(args, exec) {
      const r = await client.adbLogcat({
        lines: args.lines ?? 200,
        serial: args.serial,
        filter: args.filter,
        platform: args.platform || 'auto',
        timeoutMs: args.timeoutMs ?? 30000,
      }, exec.signal)
      const head = `Board logs (${r.platform || 'auto'}) -> exit ${r.exitCode}${r.timedOut ? ' [TIMEOUT]' : ''}`
      const body = r.stdout ? `\n${clip(r.stdout, 30000)}` : '\n(no output)'
      const err = r.stderr ? `\n[stderr]\n${clip(r.stderr, 8000)}` : ''
      return result(head + body + err, clip(JSON.stringify({ ...r, stdout: undefined }, null, 2), 12000))
    },
  })

  define({
    name: 'board_adb_screenshot',
    description: 'Capture a PNG screenshot from the Android device through the Windows PC and save it on the DSH server. Returns the local artifact path.',
    parameters: {
      properties: { serial: { type: 'string', description: 'ADB serial. Omit when exactly one device is connected.' } },
      required: [],
    },
    timeoutMs: 90000,
    async execute(args, exec) {
      const shot = await client.adbScreenshot({ serial: args.serial }, exec.signal)
      if (!shot.dataBase64) throw new Error('agent returned an empty screenshot')
      const bytes = Buffer.from(shot.dataBase64, 'base64')
      if (bytes.length === 0) throw new Error('screenshot is empty')
      const file = await saveArtifact(artifactDir, `screenshot-${safeName(args.serial || shot.serial || 'device')}-${stamp()}.png`, bytes)
      return result(
        `Saved screenshot from ${shot.serial || args.serial || 'device'} to:\n${file}\n(${bytes.length} bytes)`,
        `PNG saved on the DSH server at ${file}. Use the present tool or the file viewer to inspect it.`,
      )
    },
  })

  define({
    name: 'board_adb_push',
    description: 'Push a file from the DSH server to the board over ADB. Reads localPath on the DSH server, sends it through the Windows agent, and runs adb push.',
    parameters: {
      properties: {
        localPath: { type: 'string', description: 'Absolute path of the file on the DSH server.' },
        remotePath: { type: 'string', description: 'Destination path on the board, for example /tmp/app or /data/local/tmp/app.' },
        serial: { type: 'string', description: 'ADB serial. Omit when exactly one device is connected.' },
      },
      required: ['localPath', 'remotePath'],
    },
    timeoutMs: 180000,
    async execute(args, exec) {
      const stat = await fs.stat(args.localPath)
      if (!stat.isFile()) throw new Error(`localPath is not a regular file: ${args.localPath}`)
      if (stat.size > maxTransferBytes) throw new Error(`local file is ${stat.size} bytes, above maxTransferBytes=${maxTransferBytes}`)
      const data = await fs.readFile(args.localPath)
      const r = await client.adbPush({
        serial: args.serial,
        remotePath: args.remotePath,
        dataBase64: data.toString('base64'),
      }, exec.signal)
      return result(`Pushed ${args.localPath} (${data.length} bytes) to ${args.remotePath}.\n${r.stdout || ''}${r.stderr ? `\n${r.stderr}` : ''}`)
    },
  })

  define({
    name: 'board_adb_pull',
    description: 'Pull a file from the board to the DSH server over ADB and save it under the plugin artifact directory. Returns the local path.',
    parameters: {
      properties: {
        remotePath: { type: 'string', description: 'File path on the board.' },
        serial: { type: 'string', description: 'ADB serial. Omit when exactly one device is connected.' },
        maxBytes: { type: 'integer', description: 'Maximum file size to transfer. Default 64 MiB.' },
      },
      required: ['remotePath'],
    },
    timeoutMs: 180000,
    async execute(args, exec) {
      const r = await client.adbPull({
        serial: args.serial,
        remotePath: args.remotePath,
        maxBytes: args.maxBytes ?? maxTransferBytes,
      }, exec.signal)
      const bytes = Buffer.from(r.dataBase64, 'base64')
      const base = safeName(path.basename(args.remotePath) || 'pulled.bin')
      const file = await saveArtifact(artifactDir, `${stamp()}-${base}`, bytes)
      return result(`Pulled ${args.remotePath} (${bytes.length} bytes) to:\n${file}`)
    },
  })

  define({
    name: 'board_adb_install',
    description: 'Install an APK from the DSH server onto the Android device through the Windows agent (adb install -r).',
    parameters: {
      properties: {
        apkPath: { type: 'string', description: 'Absolute path of the APK on the DSH server.' },
        serial: { type: 'string', description: 'ADB serial. Omit when exactly one device is connected.' },
      },
      required: ['apkPath'],
    },
    timeoutMs: 240000,
    async execute(args, exec) {
      const stat = await fs.stat(args.apkPath)
      if (!stat.isFile()) throw new Error(`apkPath is not a regular file: ${args.apkPath}`)
      if (stat.size > maxTransferBytes) throw new Error(`APK is ${stat.size} bytes, above maxTransferBytes=${maxTransferBytes}`)
      const data = await fs.readFile(args.apkPath)
      const r = await client.adbInstall({ serial: args.serial, apkBase64: data.toString('base64') }, exec.signal)
      const ok = r.exitCode === 0 && !r.timedOut
      return result(`${ok ? 'Installed' : 'Install failed'}: ${args.apkPath} (exit ${r.exitCode})\n${r.stdout || ''}${r.stderr ? `\n${r.stderr}` : ''}`)
    },
  })

  define({
    name: 'board_adb_reboot',
    description: 'Reboot the board into the system, bootloader, recovery, sideload, or fastboot over ADB.',
    parameters: {
      properties: {
        serial: { type: 'string', description: 'ADB serial. Omit when exactly one device is connected.' },
        mode: { type: 'string', enum: ['system', 'bootloader', 'recovery', 'sideload', 'fastboot'], description: 'Reboot target. Default system.' },
      },
      required: [],
    },
    async execute(args, exec) {
      const r = await client.adbReboot({ serial: args.serial, mode: args.mode }, exec.signal)
      return result(`Reboot command sent${args.mode ? ` (${args.mode})` : ''}. exit=${r.exitCode}${r.stderr ? `\n${r.stderr}` : ''}`)
    },
  })

  const execProperties = {
    mode: { type: 'string', enum: ['cmd', 'powershell', 'direct'], description: 'cmd: cmd.exe /c; powershell: powershell -Command; direct: executable + args. Default cmd.' },
    command: { type: 'string', description: 'Command line for cmd/powershell, or executable path for direct.' },
    args: { type: 'array', items: { type: 'string' }, description: 'Arguments for direct mode.' },
    cwd: { type: 'string', description: 'Working directory on the Windows PC.' },
    timeoutMs: { type: 'integer', description: 'Timeout in milliseconds. Foreground default 120s; use a job for long flashing.' },
    encoding: { type: 'string', enum: ['auto', 'utf8', 'oem', 'utf16'], description: 'Output decoding. auto uses OEM for cmd and UTF-8 otherwise.' },
  }

  define({
    name: 'windows_exec',
    description: 'Run a short command on the Windows PC and wait for it (cmd, PowerShell, or a direct executable). Requires exec.enabled on the Windows agent. For long-running flashing/compilation use windows_job_start.',
    parameters: {
      properties: execProperties,
      required: ['command'],
    },
    timeoutMs: 70000,
    async execute(args, exec) {
      const r = await client.exec({
        mode: args.mode || 'cmd',
        command: args.command,
        args: args.args,
        cwd: args.cwd,
        timeoutMs: args.timeoutMs ?? 30000,
        encoding: args.encoding,
      }, exec.signal)
      const head = `Windows ${r.state} exit=${r.exitCode ?? '?'} in ${r.durationMs} ms${r.timedOut ? ' [TIMEOUT]' : ''}`
      const out = r.stdout ? `\n[stdout]\n${clip(r.stdout, 24000)}` : ''
      const err = r.stderr ? `\n[stderr]\n${clip(r.stderr, 12000)}` : ''
      return result(head + out + err, clip(JSON.stringify({ ...r, stdout: undefined, stderr: undefined }, null, 2), 8000))
    },
  })

  define({
    name: 'windows_job_start',
    description: 'Start a long-running command on the Windows PC (flash tool, build, installer, server) and return a job id immediately. Poll with windows_job_status. Requires exec.enabled.',
    parameters: {
      properties: {
        ...execProperties,
        stdin: { type: 'string', description: 'Optional initial stdin text.' },
        closeStdin: { type: 'boolean', description: 'Close stdin after writing the initial text.' },
      },
      required: ['command'],
    },
    async execute(args, exec) {
      const job = await client.startJob({
        mode: args.mode || 'cmd',
        command: args.command,
        args: args.args,
        cwd: args.cwd,
        timeoutMs: args.timeoutMs,
        encoding: args.encoding,
        stdin: args.stdin,
        closeStdin: args.closeStdin,
      }, exec.signal)
      return result(
        `Started Windows job ${job.jobId} (${job.state}, pid=${job.processId ?? '?'}).\n` +
        `Poll with windows_job_status jobId="${job.jobId}" tailChars=8000.`,
        clip(JSON.stringify({ ...job, stdoutTail: undefined, stderrTail: undefined }, null, 2), 8000),
      )
    },
  })

  define({
    name: 'windows_job_status',
    description: 'Read a Windows background job status and the newest stdout/stderr. Poll this while a flash/build runs.',
    parameters: {
      properties: {
        jobId: { type: 'string', description: 'Job id returned by windows_job_start.' },
        tailChars: { type: 'integer', description: 'How many trailing characters of each stream to return. Default 8000.' },
      },
      required: ['jobId'],
    },
    isConcurrencySafe: () => true,
    async execute(args, exec) {
      const job = await client.job(args.jobId, args.tailChars ?? 8000, exec.signal)
      const head = `Job ${job.jobId}: ${job.state} exit=${job.exitCode ?? '?'} ${job.durationMs} ms${job.timedOut ? ' [TIMEOUT]' : ''}${job.killed ? ' [KILLED]' : ''}`
      const out = job.stdoutTail ? `\n[stdout]\n${clip(job.stdoutTail, 24000)}` : ''
      const err = job.stderrTail ? `\n[stderr]\n${clip(job.stderrTail, 12000)}` : ''
      const error = job.error ? `\n[error] ${job.error}` : ''
      return result(head + out + err + error, clip(JSON.stringify({ ...job, stdoutTail: undefined, stderrTail: undefined }, null, 2), 8000))
    },
  })

  define({
    name: 'windows_job_input',
    description: 'Write text to a running Windows job stdin, optionally closing it. Useful for interactive flash tools or confirmations.',
    parameters: {
      properties: {
        jobId: { type: 'string', description: 'Job id returned by windows_job_start.' },
        text: { type: 'string', description: 'Text to write. Add \\n for Enter.' },
        close: { type: 'boolean', description: 'Close stdin after writing.' },
      },
      required: ['jobId'],
    },
    async execute(args, exec) {
      await client.jobInput(args.jobId, { text: args.text || '', close: args.close ?? false }, exec.signal)
      return result(`Wrote ${args.text?.length || 0} characters to job ${args.jobId}${args.close ? ' and closed stdin' : ''}.`)
    },
  })

  define({
    name: 'windows_job_kill',
    description: 'Kill a Windows background job and its process tree.',
    parameters: { properties: { jobId: { type: 'string', description: 'Job id returned by windows_job_start.' } }, required: ['jobId'] },
    async execute(args, exec) {
      const job = await client.killJob(args.jobId, exec.signal)
      return result(`Kill requested for job ${job.jobId}; state=${job.state}.`, clip(JSON.stringify({ ...job, stdoutTail: undefined, stderrTail: undefined }, null, 2), 4000))
    },
  })

  define({
    name: 'windows_jobs',
    description: 'List Windows background jobs known to the agent, including running and recently completed jobs.',
    parameters: { properties: {}, required: [] },
    isConcurrencySafe: () => true,
    async execute(_args, exec) {
      const list = await client.jobs(exec.signal)
      const jobs = list.jobs || []
      if (!jobs.length) return result('No Windows jobs.')
      const lines = [`Windows jobs (${jobs.length}):`]
      for (const job of jobs) {
        lines.push(`- ${job.jobId}: ${job.state} exit=${job.exitCode ?? '?'} ${job.durationMs}ms ${job.mode} ${clip(job.command || '', 160)}`)
      }
      return result(lines.join('\n'), clip(JSON.stringify(list, null, 2), 12000))
    },
  })

  define({
    name: 'windows_write_file',
    description: 'Copy a file from the DSH server to the Windows PC (for example a firmware image built on the server). Requires exec.enabled; paths are checked against exec.allowedWorkingRoots.',
    parameters: {
      properties: {
        localPath: { type: 'string', description: 'Absolute path of the source file on the DSH server.' },
        windowsPath: { type: 'string', description: 'Destination path on the Windows PC.' },
        overwrite: { type: 'boolean', description: 'Overwrite an existing file. Default true.' },
        createDirectories: { type: 'boolean', description: 'Create the parent directory. Default true.' },
      },
      required: ['localPath', 'windowsPath'],
    },
    timeoutMs: 180000,
    async execute(args, exec) {
      const stat = await fs.stat(args.localPath)
      if (!stat.isFile()) throw new Error(`localPath is not a regular file: ${args.localPath}`)
      if (stat.size > maxTransferBytes) throw new Error(`local file is ${stat.size} bytes, above maxTransferBytes=${maxTransferBytes}`)
      const data = await fs.readFile(args.localPath)
      const r = await client.writeFile({
        path: args.windowsPath,
        dataBase64: data.toString('base64'),
        overwrite: args.overwrite ?? true,
        createDirectories: args.createDirectories ?? true,
      }, exec.signal)
      return result(`Copied ${args.localPath} (${data.length} bytes) to Windows: ${r.path} (${r.bytes} bytes).`)
    },
  })

  define({
    name: 'windows_read_file',
    description: 'Copy a file from the Windows PC to the DSH server artifact directory (logs, captures, generated binaries). Requires exec.enabled.',
    parameters: {
      properties: {
        windowsPath: { type: 'string', description: 'File path on the Windows PC.' },
        maxBytes: { type: 'integer', description: 'Maximum bytes to read. Default 64 MiB.' },
      },
      required: ['windowsPath'],
    },
    timeoutMs: 180000,
    async execute(args, exec) {
      const r = await client.readFile({
        path: args.windowsPath,
        maxBytes: args.maxBytes ?? maxTransferBytes,
      }, exec.signal)
      const bytes = Buffer.from(r.dataBase64, 'base64')
      const file = await saveArtifact(artifactDir, `${stamp()}-${safeName(path.basename(args.windowsPath) || 'windows-file')}`, bytes)
      return result(`Copied Windows file ${r.path} (${bytes.length} bytes) to:\n${file}`)
    },
  })

  define({
    name: 'windows_list_dir',
    description: 'List a directory on the Windows PC. Requires exec.enabled.',
    parameters: {
      properties: { path: { type: 'string', description: 'Directory path on the Windows PC.' } },
      required: ['path'],
    },
    isConcurrencySafe: () => true,
    async execute(args, exec) {
      const r = await client.listFiles(args.path, exec.signal)
      const entries = r.entries || []
      const lines = [`${r.path} (${entries.length} entries):`]
      for (const entry of entries) {
        lines.push(`- ${entry.isDirectory ? '[dir] ' : ''}${entry.name}${entry.isDirectory ? '' : ` (${entry.length} bytes)`}`)
      }
      return result(lines.join('\n'), clip(JSON.stringify(r, null, 2), 30000))
    },
  })

  registerUiProxy(ctx, baseUrl, () => runtimeToken, (value) => {
    // A manual sign-in in the embedded panel also authorizes tool calls.
    // Static config/env tokens always win; otherwise remember it across restarts.
    if (!configuredToken && value) {
      runtimeToken = value
      storeToken(value)
    }
  })

  if (process.env.DSH_BOARD_LINK_SELFTEST) {
    console.error(`[board-link] registered ${registered} board tools for ${baseUrl}`)
  }
}

/**
 * Expose the Windows dashboard through the DSH web origin so the browser client
 * can embed it in a main panel. The proxy is loopback-only and injects the agent
 * token server-side; the browser never sees the token.
 */
function registerUiProxy(ctx, baseUrl, getToken, setToken) {
  if (typeof ctx.inject !== 'function') return

  // webServer is absent from non-web profiles (CLI runs); mounting this child
  // fiber only when the service appears keeps the tool plugin profile-agnostic.
  ctx.inject(['webServer'], (webCtx) => {
    const webServer = webCtx.webServer
    if (!webServer?.register) return
    const target = new URL(baseUrl)
    const handler = (req, res) => proxyDashboard(req, res, target, getToken, setToken)
    webCtx.effect(
      () => webServer.register({ kind: 'prefix', path: '/board-link', handler }),
      'board-link: dashboard proxy',
    )
    webCtx.logger?.info?.(`[board-link] dashboard available at /board-link/ui (upstream ${baseUrl})`)
  })
}

function proxyDashboard(req, res, target, getToken, setToken) {
  const remote = req.socket?.remoteAddress || ''
  if (!['127.0.0.1', '::1', '::ffff:127.0.0.1'].includes(remote)) {
    res.writeHead(403, { 'content-type': 'text/plain; charset=utf-8' })
    res.end('board-link dashboard proxy is loopback only')
    return
  }

  const incoming = new URL(req.url || '/', 'http://board-link')
  let upstreamPath = incoming.pathname
  if (upstreamPath === '/board-link' || upstreamPath === '/board-link/') {
    upstreamPath = '/ui'
  } else if (upstreamPath === '/board-link/ui/') {
    // Keep one canonical iframe URL; relative API paths are resolved from it.
    res.writeHead(302, { location: '/board-link/ui' + incoming.search })
    res.end()
    return
  } else if (upstreamPath.startsWith('/board-link/')) {
    upstreamPath = upstreamPath.slice('/board-link'.length)
  } else {
    res.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' })
    res.end('not found')
    return
  }

  const headers = { ...req.headers }
  delete headers.host
  delete headers.cookie
  delete headers['accept-encoding']
  headers.host = target.host

  // A manual sign-in in the panel carries the same agent token. Remember it so
  // board_status and the other host tools can use it without DSH_LINK_TOKEN.
  if (typeof headers.authorization === 'string' && headers.authorization.startsWith('Bearer ')) {
    const entered = headers.authorization.slice('Bearer '.length).trim()
    if (entered) setToken?.(entered)
  }

  const token = getToken?.() || ''
  // Auto-login for the embedded panel, but let a manually entered token win.
  if (token && !headers.authorization) headers.authorization = `Bearer ${token}`

  const upstream = http.request({
    hostname: target.hostname,
    port: target.port || 80,
    method: req.method,
    path: upstreamPath + incoming.search,
    headers,
  }, (upstreamRes) => {
    const responseHeaders = { ...upstreamRes.headers }
    delete responseHeaders['content-security-policy']
    delete responseHeaders['x-frame-options']
    const isHtml = String(upstreamRes.headers['content-type'] || '').includes('text/html')
    if (isHtml) {
      responseHeaders['content-security-policy'] =
        "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; connect-src 'self'; base-uri 'none'; frame-ancestors 'self'"
    }

    if (!isHtml) {
      res.writeHead(upstreamRes.statusCode || 502, responseHeaders)
      upstreamRes.pipe(res)
      return
    }

    delete responseHeaders['transfer-encoding']
    const chunks = []
    upstreamRes.on('data', (chunk) => chunks.push(chunk))
    upstreamRes.on('end', () => {
      const html = rewriteDashboardHtml(Buffer.concat(chunks).toString('utf8'))
      const body = Buffer.from(html, 'utf8')
      responseHeaders['content-length'] = String(body.length)
      res.writeHead(upstreamRes.statusCode || 502, responseHeaders)
      res.end(body)
    })
  })

  upstream.on('error', (error) => {
    if (res.headersSent) {
      res.destroy(error)
      return
    }
    const body = `<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><title>DSH Link 未连接</title>
<style>body{font:14px/1.6 system-ui,"Microsoft YaHei",sans-serif;background:#f4f6fa;color:#17202e;margin:0;padding:48px}
main{max-width:680px;margin:0 auto;background:#fff;border:1px solid #d9e0ea;border-radius:16px;padding:28px;box-shadow:0 16px 40px rgba(30,45,70,.08)}
h1{font-size:22px;margin:0 0 8px}code{background:#eef1f7;border-radius:6px;padding:2px 6px}.muted{color:#5e6d82}
pre{background:#0f1620;color:#cfe1ff;border-radius:10px;padding:14px;overflow:auto}</style></head>
<body><main><h1>DSH Link 没有连上</h1>
<p class="muted">DSH 服务器无法访问 Windows Agent（${escapeHtml(error.message)}）。</p>
<p>请确认 Windows 上 <code>dsh-link.exe</code> 正在运行，并且服务器的 <code>127.0.0.1:18765</code> 反向转发已建立。</p>
<pre>On Windows:  dsh-link status
On server:   ss -ltnp | grep 18765</pre>
<p class="muted">修好后刷新这个面板即可。</p></main></body></html>`
    res.writeHead(502, {
      'content-type': 'text/html; charset=utf-8',
      'content-security-policy': "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'self'",
    })
    res.end(body)
  })
  req.pipe(upstream)
}

/**
 * Compatibility with an older Windows Agent UI that used absolute API paths:
 * keep those calls under the DSH proxy instead of hitting DSH 404s. The current
 * UI already uses relative paths and is left byte-for-byte unchanged.
 */
export function rewriteDashboardHtml(html) {
  return html
    .replaceAll('"/ui/api/', '"/board-link/ui/api/')
    .replaceAll("'/ui/api/", "'/board-link/ui/api/")
    .replaceAll('"/v1/', '"/board-link/v1/')
    .replaceAll("'/v1/", "'/board-link/v1/")
}

function tokenFilePath() {
  return path.join(os.homedir(), '.dsh', 'board-link', 'token')
}

function readStoredToken() {
  try {
    return readFileSync(tokenFilePath(), 'utf8').trim()
  } catch {
    return ''
  }
}

function storeToken(value) {
  try {
    const file = tokenFilePath()
    mkdirSync(path.dirname(file), { recursive: true })
    writeFileSync(file, `${value}\n`, { mode: 0o600 })
    chmodSync(file, 0o600)
  } catch {
    /* Best effort; the in-memory token still works for this process. */
  }
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, (ch) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  })[ch])
}

const textOutput = {
  schema: {
    type: 'object',
    additionalProperties: false,
    properties: {
      summary: { type: 'string' },
      details: { type: 'string' },
    },
    required: ['summary'],
  },
  render: (_args, value) => [{
    type: 'text',
    text: value.details ? `${value.summary}\n\n${value.details}` : value.summary,
  }],
}

function result(summary, details) {
  return details ? { summary, details } : { summary }
}

function objectSchema(properties, required = []) {
  const schema = {
    type: 'object',
    additionalProperties: false,
    properties: properties || {},
  }
  if (required?.length) schema.required = required
  return schema
}

function positive(value, fallback) {
  const parsed = Number(value)
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback
}

function clip(value, max = 12000) {
  if (typeof value !== 'string') return value
  if (value.length <= max) return value
  return `${value.slice(0, max)}\n...[truncated ${value.length - max} chars]`
}

function statusResult(health, verbose, options) {
  const tunnel = health.tunnel || {}
  const serial = health.serial || {}
  const adb = health.adb || {}
  const lines = []
  lines.push(`dsh-link ${health.version || '?'} on ${health.hostname || 'Windows'} — ${health.ok ? 'ONLINE' : 'OFFLINE'}`)
  lines.push(`agent: ${health.agent?.listen || options.baseUrl} (pid ${health.processId || '?'}, uptime ${formatDuration(health.uptimeSeconds)})`)
  lines.push(`token: ${options.tokenConfigured ? 'configured' : 'MISSING'}`)
  const forwardText = (tunnel.forwards || []).map(f => `${f.name}: ${f.direction} ${f.listenHost}:${f.listenPort} -> ${f.targetHost}:${f.targetPort}`).join('; ')
  lines.push(`tunnel: ${tunnel.state || 'unknown'}${tunnel.pid ? ` pid=${tunnel.pid}` : ''}${tunnel.target ? ` target=${tunnel.target}` : ''}${tunnel.restarts ? ` restarts=${tunnel.restarts}` : ''}`)
  if (forwardText) lines.push(`forwards: ${forwardText}`)
  if (tunnel.lastError) lines.push(`tunnel error: ${clip(tunnel.lastError, 2000)}`)

  const ports = serial.ports || []
  lines.push(ports.length ? `serial ports: ${ports.map(p => p.name).join(', ')}` : 'serial ports: none')
  const sessions = serial.sessions || []
  if (sessions.length) {
    lines.push(`open serial sessions (${sessions.length}):`)
    for (const s of sessions) {
      lines.push(`- ${s.sessionId}: ${s.port} @ ${s.baudRate}, in=${s.totalBytesIn}B buffered=${s.bufferedBytes}B${s.faulted ? ' FAULTED' : ''}`)
    }
  } else {
    lines.push('open serial sessions: none')
  }

  if (!adb.available) {
    lines.push(`adb: unavailable${adb.error ? ` (${clip(adb.error, 1000)})` : ''}`)
  } else {
    const devices = adb.devices || []
    lines.push(`adb: ${adb.version || 'available'}${adb.path ? ` at ${adb.path}` : ''}; ${devices.length} device(s)`)
    for (const device of devices) {
      const attrs = [device.state, device.model, device.product].filter(Boolean).join(', ')
      lines.push(`- ${device.serial}: ${attrs}`)
    }
  }

  const winExec = health.exec || {}
  lines.push(`windows exec: ${winExec.enabled ? 'enabled' : 'disabled'} (allowAny=${Boolean(winExec.allowAny)}, allowShell=${Boolean(winExec.allowShell)}, jobs=${winExec.runningJobs || 0}/${winExec.maxJobs || 0})`)
  if (winExec.enabled) lines.push('Use windows_exec for short commands, windows_job_start for flashing/builds.')

  lines.push('', 'Use board_serial_* for COM ports and board_adb_* for the board ADB connection.')
  return result(lines.join('\n'), verbose ? clip(JSON.stringify(health, null, 2), 30000) : undefined)
}

function statusFailure(error, baseUrl) {
  const base = error?.message || String(error)
  return `${base}\n\nHint: start dsh-link on the Windows PC and check that the server-side remote forward is active:\n` +
    `  server 127.0.0.1:18765 -> Windows 127.0.0.1:8765\n` +
    `Expected plugin baseUrl: ${baseUrl}\n` +
    `On Windows: dsh-link status   (or \`dsh-link doctor\`)\n` +
    `If the tunnel is down: dsh-link run --config <config.json>`
}

function formatDuration(seconds) {
  const value = Number(seconds)
  if (!Number.isFinite(value) || value < 0) return '?'
  if (value < 60) return `${Math.floor(value)}s`
  if (value < 3600) return `${Math.floor(value / 60)}m${Math.floor(value % 60)}s`
  return `${Math.floor(value / 3600)}h${Math.floor((value % 3600) / 60)}m`
}

function parityShort(value) {
  return ({ none: 'N', even: 'E', odd: 'O', mark: 'M', space: 'S' })[String(value).toLowerCase()] || '?'
}

function stopShort(value) {
  return ({ one: '1', onepointfive: '1.5', two: '2' })[String(value).toLowerCase()] || '?'
}

function stamp() {
  return new Date().toISOString().replace(/[:.]/g, '-')
}

function safeName(value) {
  return String(value).replace(/[^A-Za-z0-9._-]+/g, '_').slice(0, 120) || 'artifact'
}

async function saveArtifact(directory, filename, bytes) {
  await fs.mkdir(directory, { recursive: true })
  const file = path.join(directory, filename)
  await fs.writeFile(file, bytes)
  return file
}
