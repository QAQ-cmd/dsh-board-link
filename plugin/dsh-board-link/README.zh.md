# dsh-board-link

DSH 插件：通过 Windows 上的 [dsh-link](https://github.com/deepseek-ai/deepseek-harness) Agent 使用串口和 ADB。

插件本身不依赖任何 npm 包，也不要求 profile 里再安装一套 `@deepseek-ai/*`。它向 `ctx.tools` 注册 24 个原生工具（串口、ADB、Windows 命令/任务/文件），同时给 DSH Web 增加一个 **板端控制台** 页面。

## 安装

在运行 DSH 的服务器上，推荐直接安装 `.tgz`：

```bash
dsh plugin --profile web add file:/absolute/path/dsh-board-link-0.1.0.tgz
```

也可以用源码目录：

```bash
dsh plugin --profile web add link:/absolute/path/to/plugin/dsh-board-link
```

安装后**重启 DSH Web**，左侧导航出现 **板端控制台**。它通过 DSH host 的 `/board-link` 代理嵌入 Windows 控制台；代理仅接受 loopback 来源，并在服务端注入 agent token。

如果 `DSH_LINK_TOKEN` 未设置，可以在面板里手动登录一次；Host 会把 token 保存到 `~/.dsh/board-link/token`（0600），之后的 `board_status` 等工具也会使用它，DSH 重启后仍然有效。写进 profile 的 `token` 则显式覆盖这份缓存。

如果输入 token 后出现 404，通常是 Windows 端还在运行使用绝对路径的旧 Agent。新版 host 代理会把旧 UI 的 `/ui/api/*`、`/v1/*` 自动重写到 `/board-link` 下；更新并重启 Windows Agent 后即可自动登录。

或者在 profile 的 `cordis.patch.yml` 中手动加入：

```yaml
- id: board-link
  name: 'dsh-board-link'
  config:
    baseUrl: 'http://127.0.0.1:18765'
    timeoutMs: 15000
```

然后让 DSH 进程看到 agent token：

```bash
export DSH_LINK_TOKEN='与 Windows agent.token 相同的值'
```

也可以把 `token` 直接写进 profile patch。该配置会整体覆盖 bundle 默认值，所以请同时写上 `baseUrl` 和 `timeoutMs`。

## 配置

| 字段 | 默认 | 说明 |
|---|---|---|
| `baseUrl` | `http://127.0.0.1:18765` | 服务器侧 SSH 反向转发端口 |
| `token` | 空 | 为空时读取 `DSH_LINK_TOKEN` |
| `tokenEnv` | `DSH_LINK_TOKEN` | token 环境变量名 |
| `timeoutMs` | `15000` | 普通 HTTP 请求超时 |
| `artifactDir` | `~/.dsh/board-link/artifacts` | 截图/pull 文件保存目录 |
| `maxTransferBytes` | 64 MiB | push/install 本地文件上限 |

## 工具

`board_status`、`board_serial_list/open/read/write/close/pulse`、`board_adb_devices/shell/logcat/screenshot/push/pull/install/reboot`、`windows_exec/job_start/job_status/job_input/job_kill/jobs/write_file/read_file/list_dir`。

调用示例：

```text
board_status
board_serial_open port="COM26" baudRate=115200
board_serial_read sessionId="ser-..." timeoutMs=2000
board_adb_shell command="uname -a"
board_adb_logcat lines=100
board_adb_push localPath="/home/me/app" remotePath="/tmp/app"
board_adb_pull remotePath="/var/log/messages"

windows_exec mode=cmd command="build.bat" cwd="C:\work\board"
windows_job_start mode=cmd command="flash.bat" cwd="C:\work\board" timeoutMs=600000
windows_job_status jobId="job-..." tailChars=8000
windows_job_kill jobId="job-..."
windows_write_file localPath="/home/me/build/firmware.bin" windowsPath="C:\work\board\firmware.bin"
windows_read_file windowsPath="C:\work\board\flash.log"
```

`windows_*` 需要 Windows 配置里 `exec.enabled=true`，并且 `cmd`/`powershell` 模式还要 `exec.allowShell=true`。详见主 README 的「控制 Windows」一节。

## 行为说明

- 串口会话保存在 Windows Agent 进程内，跨工具调用有效；Agent 重启会关闭所有会话。
- `board_adb_logcat` 会自动区分 Android 和 Buildroot；Buildroot 读取 `/var/log/messages` 或 `dmesg`。
- `board_serial_read` 有数据就返回，不保证一次返回完整行。
- 离线/401/隧道断开时，错误信息会给出 Windows 侧排查命令。

## 安全

- Agent 默认只监听 Windows `127.0.0.1:8765`，SSH 反向转发也只绑定服务器 `127.0.0.1`。
- DSH Web 的 `/board-link` 代理只接受本机请求，token 不写入浏览器 URL；如果 DSH 直接暴露到局域网，该入口返回 403。
- token 是唯一认证；不要把 token 写进 Git。
- `board_adb_shell` 和串口写入是设计上的远程执行能力，请用 DSH 的工具权限策略限制。
- `board_adb_push` / `board_adb_install` 会读取 DSH 服务器上模型指定的本地文件；不要在不可信模型上开放，建议 DSH 使用专用低权限账号。
- `windows_*` 是 Windows 远程代码执行能力，默认关闭；开启后按 Windows 用户权限运行。请只对可信模型开启，并配置 `allowedExecutables`、`allowedWorkingRoots`。
