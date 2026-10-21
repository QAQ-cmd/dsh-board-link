# dsh-board-link

DeepSeek Harness plugin that exposes serial and ADB hardware attached to a Windows PC through the [dsh-link](../../README.md) agent.

The plugin has no npm runtime dependencies. It registers 24 native tools on `ctx.tools` (serial, ADB, and optional Windows command/job/file control) and imports nothing from `@deepseek-ai/*`, so installing it does not put a second copy of the DSH packages into the profile. It also contributes a **Board console** page to the DSH Web sidebar.

## Install

On the DSH server, the recommended form is the standalone tarball:

```bash
dsh plugin --profile web add file:/absolute/path/dsh-board-link-0.1.0.tgz
```

Or install from a source directory:

```bash
dsh plugin --profile web add link:/absolute/path/to/plugin/dsh-board-link
```

Restart the DSH Web process after installing. The sidebar then shows **Board console**; the host `/board-link` proxy embeds the Windows dashboard, accepts loopback requests only, and injects the agent token server-side.

Or add a row manually to the profile's `cordis.patch.yml`:

```yaml
- id: board-link
  name: 'dsh-board-link'
  config:
    baseUrl: 'http://127.0.0.1:18765'
    timeoutMs: 15000
```

Make the DSH process see the agent token:

```bash
export DSH_LINK_TOKEN='same value as agent.token on Windows'
```

## Configuration

| Field | Default | Meaning |
|---|---|---|
| `baseUrl` | `http://127.0.0.1:18765` | Server-side SSH remote-forward port |
| `token` | empty | Falls back to `DSH_LINK_TOKEN` |
| `tokenEnv` | `DSH_LINK_TOKEN` | Token environment variable |
| `timeoutMs` | `15000` | Normal HTTP timeout |
| `artifactDir` | `~/.dsh/board-link/artifacts` | Screenshot/pull output directory |
| `maxTransferBytes` | 64 MiB | Local push/install file limit |

## Tools

`board_status`, `board_serial_list/open/read/write/close/pulse`, `board_adb_devices/shell/logcat/screenshot/push/pull/install/reboot`, `windows_exec/job_start/job_status/job_input/job_kill/jobs/write_file/read_file/list_dir`.

The Windows agent must be running and its reverse tunnel must expose `127.0.0.1:18765` on the DSH server.

`windows_*` requires `exec.enabled=true` on the agent; `cmd`/`powershell` modes additionally require `exec.allowShell=true`. Use `windows_job_start` for flashing or builds that take minutes, then poll `windows_job_status`. `windows_write_file` copies a server-built firmware image to Windows; `windows_read_file` copies logs back to the DSH server artifact directory.

## Security

The agent is loopback-only and token-authenticated. The DSH `/board-link` proxy is also loopback-only and injects the agent token server-side; it rejects non-loopback clients with 403. `board_adb_shell` and serial writes are intentionally remote-execution capabilities; constrain them with DSH tool permissions. `board_adb_push`/`board_adb_install` read model-specified files on the DSH server, so do not expose them to untrusted models; preferably run DSH under a dedicated low-privilege account. `windows_*` is Windows remote code execution and is disabled by default; enable it only for trusted models and keep the executable/working-directory allowlists tight.
