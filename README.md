# 自动网关切换 AutoChangeGateWay

Windows 托盘 / 服务工具（0.2.0）。选择一张网卡，持续检测主网关；主网关连续无响应且备用在线时，切换该网卡的 IPv4 默认路由。

默认主网关 `192.168.88.191`，备用网关 `192.168.88.1`，均可在界面修改。首次使用默认开启“仅检测”，不会修改网络。

升级时先从旧版的托盘菜单退出，再打开新版；同一个登录会话中只允许一个桌面实例运行。新版发布到独立版本目录，不覆盖正在运行的旧版文件。

## 运行

支持 Windows 10 / 11 的 x64 环境。普通发布版需要 .NET 8 Desktop Runtime；`standalone` 发布版包含运行时。

1. 解压发布包，双击 `AutoChangeGateWay.exe`。
2. 选择网卡和检测源 IPv4，填写同网段的主备网关。
3. 保持“仅检测”开启，点击“开始监控”，确认两台网关的探测状态符合实际。
4. 停止监控。需要实际切换时，点击“管理员重启”，取消“仅检测”，再开始监控。
5. 可以先用“切到主网关”设置主网关；自动回切默认关闭。当前已经使用备用网关时，监控会继续保持备用，直到手动切回或开启自动回切。

默认每轮检测后等待 3 秒，单台网关超时 1000 毫秒；主网关连续失败 3 次后考虑切换；切换冷却 30 秒；启用自动回切时，主网关连续成功 5 次后再考虑切回。两台网关都无响应时保留当前路由。

- 关闭窗口会收起到系统托盘，监控继续运行。
- “停止监控”只停止检测，保留当前路由。
- “恢复原配置”会先停止监控，再恢复首次实际切换前的默认路由。
- 通过“退出”或托盘“退出并恢复”离开时，程序尝试恢复原配置；恢复失败会提示，并保留恢复记录。
- 异常退出后，重新打开程序会提示待恢复记录。请先恢复，再开始新的实际切换。

本程序直接调用 Windows IP Helper API，不启动命令行修改网络。路由修改仅在本次系统运行期间生效，不修改持久网关、IP、掩码、DNS 或 IPv6；系统重启后由 Windows 的原有配置重新生成路由。

## 检测与适用范围

本工具判断的是网关是否回应 ICMP Ping，不代表网关的互联网转发一定正常。网关必须允许回应 Ping。

网卡可以自由选择，但主备网关必须位于检测源 IP 的同一子网。探测指定源 IPv4，并检查系统到网关的实际出接口；检测路径被 VPN、重叠网段等改变时，暂停切换，不把路径错误算成网关离线。

首版管理一张网卡。所选网卡初始存在多条 IPv4 默认路由、网关为 VPN 无下一跳路由或不在本地子网时，不接管该配置。其他网卡默认路由和更具体的 VPN 路由仍可能影响系统实际出网路径，程序不会更改它们的优先级。

DHCP 重新注入原默认路由时，监控可以在当前使用的目标网关在线、且所有路由均属于已知记录的条件下重新应用目标网关。发现未知默认路由、源 IP 变化、断开或网络配置冲突时，停止本轮写入并提示。换网络前应停止监控并恢复原配置。

切换会改变后续连接的路径，已有连接可能需要重连。如果 DNS 本身指向已失联的主网关，默认网关切换后仍可能无法解析域名；本版保持 DNS 原配置，需要单独配置可用 DNS。

## 自启动与 Windows 服务

在界面“运行方式”中选择模式，设置“随 Windows 自动启动”，然后点击“应用运行设置”。修改运行设置、安装和管理服务均需先点击右上角“管理员重启”，接受 Windows UAC 授权。

| 运行方式 | 勾选随 Windows 自动启动 | 不勾选 |
| --- | --- | --- |
| 桌面托盘 | 当前用户登录后自动启动，并按保存的配置开始监控 | 手动打开程序并开始监控 |
| Windows 服务 | 安装为自动延迟启动服务，开机后无需登录即可监控 | 安装为手动启动服务，可用界面启动 / 停止 |

“启动时收起到托盘”只影响桌面登录自启动。登录任务以当前用户的最高可用权限运行，不保存账户密码。登录后等待约 15 秒启动，程序随后等待所选网卡和源 IP 可用；网卡暂未就绪不会直接修改其他网卡。

选择 Windows 服务模式后，“应用运行设置”会保存服务配置、安装或更新服务，并立即启动。先保持“仅检测”开启可验证服务状态；需要实际切换时，取消“仅检测”后再次应用。更新配置时会先停止原服务、尝试恢复原路由，再重新启动。

- “启动服务”：按上次已应用的服务配置运行；尚未应用的界面修改不会自动生效。
- “停止并恢复”：停止后台监控，并尝试恢复服务首次修改前的路由。
- “卸载服务”：先停止及恢复，再删除服务注册；安装文件、配置和日志保留。
- 关闭或退出桌面界面不会停止 Windows 服务。重新打开界面可读取服务的状态和探测结果。
- 切回桌面方式并应用时，会停止服务并取消服务的开机自动启动；不会删除服务注册。
- 桌面与服务之间使用计算机范围的互斥锁，避免两者同时管理路由。服务异常重启时会先处理遗留恢复记录，再开始新的监控。

程序会将运行文件复制到 `%ProgramFiles%\AutoChangeGateWay\`，自启动任务及服务均使用该固定位置；不要在安装后直接删除它。服务名为 `AutoChangeGateWay`，使用 LocalSystem 账户运行；登录任务名为 `AutoChangeGateWay.Desktop.<当前用户 SID>`。服务配置目录仅允许管理员和 SYSTEM 修改，普通用户可读取状态。

## 配置、日志与恢复

数据存放在 `%LOCALAPPDATA%\AutoChangeGateWay\`：

- `settings.json`：网卡 GUID、源 IP、主备网关和检测参数。
- `route-session.json`：首次切换前的路由及本次程序管理过的路由。每次修改前先落盘；完成恢复后删除。
- `logs\yyyy-MM-dd.log`：运行日志，保留约 30 天。

服务独立使用 `%PROGRAMDATA%\AutoChangeGateWay\Service\` 保存上述配置、恢复记录和日志；`service-status.json` 提供最近探测结果给桌面界面。服务与桌面的恢复记录不会互相覆盖。

不要在存在待恢复记录时手动删除 `route-session.json`。原网卡断开、IP 已改变或发现外部未知路由时，恢复会拒绝覆盖当前网络；连接原网络后重试，或者重启 Windows 让临时路由消失。

## 开发与构建

使用 .NET 8 SDK。Windows 服务支持使用微软 `System.ServiceProcess.ServiceController` 8.0.1；单文件发布还需要还原微软的构建包，独立版需还原运行时包。`global.json` 固定 SDK 基线为 8.0.413，允许同主次版本的新 feature band。

```powershell
# 编译、运行模拟测试、生成单文件程序及 ZIP
.\build.ps1

# 包含运行时的独立发布包（首次需要联网还原微软运行时包）
.\build.ps1 -SelfContained

# ARM64 发布（尚需在 ARM64 实机验证）
.\build.ps1 -SelfContained -Runtime win-arm64
```

默认输出 `artifacts\AutoChangeGateWay-0.2.0-win-x64\AutoChangeGateWay.exe` 和对应 ZIP；独立版目录名追加 `-standalone`。

结构：

- `src/AutoChangeGateWay.Core`：参数校验、故障切换策略、路由事务和恢复。
- `src/AutoChangeGateWay.Windows`：Windows 路由 API、源地址 ICMP 检测、配置与日志存储。
- `src/AutoChangeGateWay.App`：WinForms 配置界面及托盘。
- `tests/AutoChangeGateWay.Tests`：不依赖测试框架的控制台测试，使用模拟网卡和故障注入，不修改系统网络。
- `tests/AutoChangeGateWay.WindowsTests`：旧配置兼容、运行设置、状态文件和互斥锁测试。可选管理员集成测试只注册临时测试任务 / 服务，启动仅写标记文件的测试入口，并在结束时删除注册项，不修改网络。

在管理员 PowerShell 中执行可选集成测试（先编译）：

```powershell
dotnet tests/AutoChangeGateWay.WindowsTests/bin/Release/net8.0-windows/AutoChangeGateWay.WindowsTests.dll artifacts/validation/runtime-integration --integration --app artifacts/AutoChangeGateWay-0.2.0-win-x64-standalone/AutoChangeGateWay.exe
```

完整验证范围及当前权限限制见 [验证记录](docs/验证记录.md)。

程序也提供只读诊断与界面渲染入口，输出路径由参数指定：

```powershell
.\AutoChangeGateWay.exe --diagnose C:\Temp\gateway-diagnostics.json
.\AutoChangeGateWay.exe --ui-smoke C:\Temp\gateway-ui.png
```

诊断输出含本机网卡和默认路由信息；仅用于本地排障。界面渲染使用示例数据，不执行网络写操作。

开源调研及选型依据见 [调研文档](docs/开源调研与技术选型.md)。
