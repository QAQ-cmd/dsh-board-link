# 验证记录

测试时间：2026-10-19  
Windows：Windows 10.0.19045，PowerShell 7，.NET SDK 10.0.203  
DSH：0.2.1-rc.2（Desktop/Web）  
开发板：RK3576 Buildroot 2024.02，Linux 6.1.118，aarch64，adb shell 为 root  
串口：`COM26`（Silicon Labs CP210x，`\Device\Silabser0`），`COM1` 为主板串口

## 1. 编译

```powershell
dotnet build src\DshLink.Agent\DshLink.Agent.csproj -v minimal
```

结果：`0` warning，`0` error。Agent 没有第三方 NuGet 依赖；串口通过 Win32 comm API 直接实现。

## 2. Agent HTTP

启动：

```powershell
dsh-link run --config .run\config.json
```

验证：

- 无 token 请求 `/v1/health` → `401`。
- 带 token 请求 → `ok=true`，返回 `COM1`、`COM26`、ADB 1.0.41、设备 `53fd7a1130c3bf7a`。
- `/v1/config` 不返回 token。

## 3. 插件包安装

- 独立 `.tgz` 用 `dsh plugin --profile plugintest add file:/.../dsh-board-link-0.1.0.tgz` 安装成功，`dsh.profile.bundles` 自动加入 `dsh-board-link`。
- 独立 `.zip` 解压后 `link:` 安装成功。
- 完整发布 ZIP 中确认存在 `plugin/dsh-board-link/package.json`；服务器报 `ENOENT .../package.json` 时优先改用 `.tgz`，避免旧 ZIP/解压路径问题。

## 4. 插件注册

用真实 DSH profile 安装：

```bash
dsh plugin --profile plugintest add link:.../plugin/dsh-board-link
dsh plugintest --port 60575 --no-open
```

`dsh plugin add` 自动把 `dsh-board-link` 加入 `dsh.profile.bundles`。启动日志：

```text
dsh web: http://127.0.0.1:60575/?token=...
[board-link] registered 24 board tools for http://127.0.0.1:18765
```

说明 24 个工具全部通过 DSH 的 parameter/output schema 校验并完成注册。

## 5. 插件工具对真实硬件的调用

```powershell
node plugin\dsh-board-link\test\smoke.mjs
```

结果：

- `board_status`：在线，列出两个串口和 1 个 ADB 设备。
- `board_serial_open COM26 115200` → `sessionId=ser-...`。
- `board_serial_write` 写 `\r\n`，`board_serial_read` 读回：
  ```text
  root@rk3576-buildroot:/#
  ```
- `board_adb_shell command="uname -a"` → `Linux rk3576-buildroot 6.1.118 ... aarch64 GNU/Linux`。
- `board_adb_logcat lines=5` 自动识别为 `linux`，读取 `/var/log/messages`。
- `board_adb_push` 推送 33 字节到 `/tmp/dsh-link-push-test.txt`。
- `board_adb_pull` 拉回并写入 artifact 文件，内容逐字节一致。

## 6. SSH 双方向转发

为避免动系统 sshd，测试使用仅监听 `127.0.0.1:2222` 的临时 sshd 配置和临时主机/客户端密钥。

Windows Agent 配置：

```json
{
  "target": "yx@127.0.0.1",
  "port": 2222,
  "forwards": [
    { "direction": "local",  "listenPort": 16074, "targetPort": 60573 },
    { "direction": "remote", "listenPort": 18766, "targetPort": 8766 }
  ]
}
```

结果：

- 本地转发：`http://127.0.0.1:16074/` 得到 DSH 的 `401` 响应（说明 TCP/HTTP 已到 DSH，只是没有 URL token）。
- 反向转发：`http://127.0.0.1:18766/v1/health` 返回 `ok=true`，`agent=127.0.0.1:8766`。
- 杀掉 SSH 子进程：Agent 在 2 秒内重启 SSH，`restarts=1`，反向端口恢复。
- `POST /v1/tunnel/stop`：SSH 子进程结束，`18766` 关闭，返回 `state=stopped`。
- `POST /v1/tunnel/start`：重新建立，反向转发恢复。

## 7. Windows 控制

Agent 配置 `exec.enabled=true`、`allowShell=true`，对 `cmd`、PowerShell、direct 三种模式做了实测：

- `cmd`：`echo hello & ver` 返回 `hello` 和正确的 `Microsoft Windows [版本 10.0.19045.7725]`。
- PowerShell：`Write-Output '中文测试'` 返回正确中文；默认自动设置 UTF-8 输出。
- direct：`cmd.exe /c echo direct` 成功。
- 后台任务：启动 PowerShell 循环输出、轮询到 `tick 1/2/3`、`windows_job_kill` 终止。
- 超时：`timeoutMs=1200` 的 `ping` 任务变为 `timed_out`，exit code `-1`。
- stdin：向 `[Console]::ReadLine()` 任务写入 `hello-stdin`，返回 `got:hello-stdin`。
- 安全闸门：
  - `exec.enabled=false` → HTTP 403；
  - `allowShell=false` 的 `cmd` → 403，但 direct `cmd.exe` 仍可用；
  - 不在白名单的 `whoami.exe` → 403；
  - `cwd=C:\Windows` 且 `allowedWorkingRoots` 限制在项目目录 → 403。
- 文件传输：写入 19 字节文件、读回内容一致、目录列表包含该文件；`overwrite=false` → 409；读取 `C:\Windows\win.ini`（在 `allowedWorkingRoots` 外）→ 403。
- 插件 `windows_write_file` / `windows_read_file` / `windows_list_dir` 端到端往返成功。

## 8. 图形化控制台

- `GET /ui` 返回内嵌控制台 HTML（约 60 KB），无 token 也能打开登录页；`/ui/api/*`、`/v1/*` 仍返回 401。
- `?token=...` 自动登录，并把 token 从地址栏移除（保留 `#tab` 锚点）。
- 用 Edge headless + CDP 实测：
  - 概览、隧道、串口、ADB、Windows 控制、文件、DSH 插件、日志八个标签均可切换。
  - 在 Windows 控制页执行 `echo cdp-ok`，输出 `exit 0 · succeeded · 49 ms`。
  - 保存执行设置，toast 显示“执行设置已保存”。
  - 串口页打开 `COM26`、显示 session、关闭成功。
  - 文件页列出 `.run` 目录 30 项。
  - 隧道页保存配置成功。
  - 浏览器 console 无 JS 异常。
- 插件 ZIP 下载接口返回 8 个文件：`package.json`、`cordis.patch.yml`、`lib/index.js`、`lib/client.js`、`lib/ui-client.js`、两份 README、LICENSE。
- 双击/`--gui` 模式：隐藏控制台，WinForms + WebView2 原生窗口打开控制台；任务栏和托盘同时出现。
- `POST /v1/shutdown` 受控停止 Agent，并关闭原生控制台；进程以 exit code 0 退出。
- 控制台左侧「日志」下面新增「串口 / ADB 日志」页：只显示 serial/adb 行，1.5s 自动刷新，支持关键字过滤、暂停/继续、清空；不再打开独立窗口。
- 串口/ADB 输出栏：打开 `COM26 @ 115200`，写 `AT\r\n`，读回 77 字节；`/ui/api/device-io` 返回 `串口 INFO OPEN`、`串口 TX AT`、`串口 RX AT ... -bash: AT: command not found ...`，UI 按 `RX/TX` 原样打印；串口 ANSI 控制符在显示时过滤。
- ADB 实际命令会记录 `ADB CMD` 与 `ADB OUT/ERR`（`shell/logcat/push/pull/install/reboot` 等）；设备未就绪时不会产生空输出。
- 保存「Windows 控制」配置只写 `exec`，`/ui/api/config` 返回 `tunnelChanged=false`，隧道 PID/状态不变；只有隧道字段变化才重启 SSH，因此不会断掉本地 60573 的 DSH 转发。
- `--gui --no-ui` 静默启动 Agent，实测 8783 健康检查 200，且没有 Edge 进程/窗口。
- Edge app 加 `--disable-sync`、`--disable-extensions` 等参数后，CDP 页面列表中没有 `edge://sync-confirmation-dialog`。
- 可执行文件嵌入自定义 DS 图标；桌面快捷方式 `IconLocation=exe,0`。
- 本地控制台改为 WinForms + WebView2 原生窗口（WebView2 SDK 1.0.2592.51 随包）：窗口和任务栏图标都是 DS，不再出现 Edge 图标/账号同步弹窗。
- 单文件发布使用 `IncludeAllContentForSelfExtract=true`；`install.ps1` 的 VBS 把 `DOTNET_BUNDLE_EXTRACT_BASE_DIR` 指到 `%LOCALAPPDATA%\DshLink\bundle`，避免临时目录权限问题。
- 托盘：NotifyIcon 使用 DS 图标；Windows 默认把新图标放进 `^` 溢出区，首次启动有气泡提示，需用户固定。
- 托盘：`--gui --no-ui` 启动后 Agent 健康检查 200，无 Edge 进程；NotifyIcon 使用 exe 的 DS 图标，菜单含打开控制台/复制 token/退出服务；通过 `/v1/shutdown` 退出时进程 exit code 0。
- 截图：[控制台概览](dashboard-overview.png)、[Windows 控制](dashboard-windows.png)。

### DSH Web 客户端 UI

- 插件包包含 `lib/ui-client.js`，`package.json` 的 `dsh.client` 声明 `platform: web`，`exports["./client"]` 指向该文件。
- 在独立临时 DSH profile 中安装插件并启动 DSH Web；DSH 启动 HTML 的模块清单包含 `dsh-board-link/client.js`：
  - `<script>__DSH_BOOT__ ... id: "dsh-board-link" ...</script>`
  - `plugins/??dsh-board-link/client.js`
- 用 Edge CDP 实测：
  - 左侧导航出现 **板端控制台**；
  - 点击后 `main` 面板挂载 iframe `http://127.0.0.1:<dsh-port>/board-link/ui`；
  - iframe 内自动登录并渲染完整控制台（概览、串口、ADB 等），无需再输入 token；
  - iframe 内点击「串口」标签，端口下拉框显示 COM1/COM26，导航高亮同步；
  - DSH 页面与 iframe 均无 JS 异常。
- Host 代理：
  - `GET /board-link/ui` → 200，CSP 被改写为 `frame-ancestors 'self'`；
  - `GET /board-link/v1/health` → 200，服务端注入 token；
  - 非 loopback 来源 → 403；
  - Agent 未启动时返回 502 的 HTML 提示页（包含 Windows/服务器排查命令）。
- 截图：[DSH 内的板端控制台](dsh-panel.png)。

## 9. 已知限制

- Windows 上同一 COM 口只能被一个进程打开；测试时不能同时开 PuTTY。
- 测试板是 Buildroot，不是 Android；`android` 分支的逻辑按标准 `logcat -d` 实现，未在本机 Android 设备上单独跑过。
- 未测试：Windows 服务模式、托盘 UI、USB/IP 整体透传、WebSocket 串口流。
- 临时 sshd 和测试 profile 仅用于验证，不随产品部署。
