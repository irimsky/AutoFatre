# AutoFatre

本项目目前只在国服客户端上维护，欢迎支持本地化。

This project is currently maintained only for the Chinese client. Contributions for localization are welcome.

Repo install URL:
```text
https://raw.githubusercontent.com/irimsky/DalamudPlugins/refs/heads/main/manifest.json
```

AutoFatre 是一个面向《最终幻想 XIV》的 Dalamud 自动化插件，用于自动执行战斗型 FATE 流程：选择目标、跨地图传送、地面或飞行导航、触发 FATE、等级同步、战斗目标管理、FATE 结算和异常恢复。

插件负责流程控制、移动和目标管理，不负责施放战斗技能。请同时运行能够处理当前目标的战斗或职业循环插件。

## 功能

- 在当前地图持续循环可执行的 FATE。
- 指定一个或多个同地图 FATE 为优先目标；目标尚未出现时，可以等待或继续处理其他 FATE。
- 按地图顺序执行自定义预设，并根据 FATE 完成次数、背包物品数量和指定 FATE 等条件切换地图。
- 支持普通怪物消灭、讨伐 BOSS、收集、防御和护送 FATE。
- 自动处理跨地图传送、都市传送网中转、坐骑、飞行、禁飞区和地面导航。
- 处理等级同步、战斗陆行鸟伙伴、战后清场和导航失败恢复。
- 支持死亡后接受其他玩家复活，或等待超时后返回复活点并恢复任务。

## 依赖

| 插件 | 用途 |
| --- | --- |
| **vnavmesh** | 地面导航、飞行导航和路径查询 |
| **Lifestream** | 以太之光和都市传送网传送 |
| **BossMod** 或 **BossMod Reborn** | 战斗目标和战斗行为处理 |
| **Wrath Combo** 或 **Rotation Solver Reborn** | 职业技能循环和实际技能施放 |

[TextAdvance](https://github.com/NightmareXIV/TextAdvance) 是可选依赖，可协助推进 FATE 开启和收集交付对话。未安装时，插件会使用原生对话流程。

战斗相关需要各安装一类插件：前一类负责战斗中的目标和行为，后一类负责当前职业的技能循环。

## 快速开始

1. 在 Dalamud 的自定义插件仓库中添加上方的 Repo install URL。
2. 确认 vnavmesh、Lifestream、目标/战斗行为插件和职业循环插件均已加载并正常运行。
3. 使用 `/autofatre` 或 `/af` 打开主窗口。
4. 在“选择模式”中选择运行模式，并完成地图或 FATE 配置。
5. 在“设置”中按需要启用飞行、战斗陆行鸟、死亡恢复、音效提醒和 FATE 黑名单。
6. 选择“开始”，先在低风险场景观察传送、导航、落地、等级同步和战斗目标是否符合预期。

## 命令

命令前缀支持 `/autofatre` 和简写 `/af`。

| 命令 | 作用 |
| --- | --- |
| `/autofatre`、`/autofatre ui`、`/autofatre config` | 打开主控制面板 |
| `/autofatre start` | 启动自动化 |
| `/autofatre pause` | 暂停自动化并停止插件发起的移动 |
| `/autofatre retry` | 从暂停或可恢复故障状态重新校验并继续 |
| `/autofatre stop` | 停止自动化 |

## 运行模式

### 单地图循环

在指定地图持续选择并完成可用 FATE。

### 指定 FATE

选择一个或多个同地图 FATE 作为目标。插件会按配置等待目标出现或处理其他符合条件的 FATE；完成指定目标后结束当前目标流程。

### 自定义预设序列

按顺序执行多个地图。每张地图可以设置 FATE 完成次数、背包物品数量和指定 FATE 等停止条件，列表可以在完成后停止，也可以循环执行。

预设地图还可以配置进入地图后自动召唤的宠物物品和自动装备的武器物品。

## FATE 处理

| 类型 | 处理方式 |
| --- | --- |
| 消灭普通怪物 | 按主动拉怪上限分批处理目标，战斗技能由外部战斗插件执行。 |
| 讨伐 BOSS | 持续优先选择当前 FATE 中生命值最高的可攻击目标。 |
| 收集物品 | 导航到收集对象，取得物品后前往交付目标；受到攻击时先处理战斗目标。 |
| 防御 | 以 FATE 中心作为安全范围，持续扫描受到攻击的防御目标；优先处理正在攻击这些友方目标的敌人，其余时间按普通 FATE 规则接战范围内的敌人，不会跟随防御目标离开区域。 |
| 护送 | 追踪护送目标的位置；优先处理正在攻击护送目标的敌人。没有可攻击目标时，保持在护送目标附近，距离超过约 10 yalms 开始追踪。 |

FATE 完成后，插件会先清理仍以本地玩家为目标的战斗 NPC，脱战后再前往下一个目标。

## 传送、导航与恢复

- 跨地图时，插件会从角色已解锁的入口中选择默认目标，并交给 Lifestream 执行传送。
- 目标地图没有合适的直达入口时，会尝试通过都市传送网中转。
- 启用飞行后，插件会在满足条件时上坐骑并飞行导航；接近 FATE 后会停止导航、确认落地，再下坐骑并进行等级同步。
- 禁飞区会强制使用地面导航；离开禁飞区后，只有满足稳定离开条件才会恢复飞行。
- 导航无有效位移、依赖暂时不可用或传送超时等问题会进入恢复流程；多次恢复失败后会暂时跳过当前目标。
- 若战斗状态残留但没有可见目标，插件会先跑离并等待脱战，再继续后续流程。
- 角色死亡后，可以自动接受其他玩家复活；启用自动返回时，等待时间结束后会返回复活点并重新验证任务。

## 注意事项

- 这是一个会自动移动、传送、交互和参与战斗的插件。首次使用或修改配置后，请先观察完整流程。
- 请确保角色拥有目标地图的传送权限、坐骑和飞行权限，以及足够的传送和伙伴相关资源。
- AutoFatre 不替代战斗相关插件。没有可用的目标/战斗行为插件或职业循环插件时，角色可能无法建立仇恨或完成 FATE。
- FATE 支持范围取决于当前客户端数据和插件实现；不支持的类型会显示排除原因并跳过。
- 游戏更新可能改变 Dalamud、IPC、对象识别或 UI 行为。

## 发布

- 本地构建会自动探测常见的 Dalamud 开发运行时目录；也可以按优先级使用命令行 `-DalamudLibPath`、`AUTOFATRE_DALAMUD_LIB_PATH`、`DALAMUD_LIB_PATH` 或 `DALAMUD_HOME` 指定包含 `Dalamud.dll` 的目录。推荐通用环境变量：`$env:DALAMUD_LIB_PATH = "C:\你的Dalamud开发目录"`。
- 本地发布前检查：`powershell -File .\release.ps1 -Version 0.2.0 -Restore`。脚本只检查版本、Release 构建和插件包是否生成，不会修改或推送任何仓库；网络不可用时可增加 `-SkipRemoteTagCheck`。需要在 main 且工作区干净时，再增加 `-RequireMainBranch -RequireCleanWorkspace`。
- 将已提交版本创建为三段或四段版本号 tag（例如 `v0.2.0` 或 `0.2.0.0`）后，`.github/workflows/release.yml` 会在干净环境构建、运行单元测试并创建 GitHub Release。也可以通过 `workflow_dispatch` 重新处理一个已有 tag，并选择国服或国际服 Dalamud runtime。
- Release 默认只附带用户安装所需的 `latest.zip`。checksum、发布清单和自定义 release notes 可由项目自行生成，不是插件安装器的强制要求。
- Manifest PR 是可选的维护动作。手动运行 workflow 时选择 `manifest_update=pr` 才会访问外部 Manifest 仓库，并需要 Secrets 中的 `DALAMUD_MANIFEST_TOKEN`；普通 tag 发布不需要这个 Token。仓库变量 `AUTOFATRE_MANIFEST_REPOSITORY` 可覆盖默认的 `irimsky/MyDalamudPlugins`。

## 许可证

[MIT License](LICENSE)
