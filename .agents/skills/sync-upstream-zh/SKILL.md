---
name: sync-upstream-zh
description: 上游（BillyNabil/nabyscape）发布更新后，同步代码到本 fork（YangAoLib/nabyscape），为新增或变更的 UI 文案补全简体中文翻译，校验本地化目录与默认偏好清单，推送并用 GitHub Actions 构建发布新版本。当用户要求「同步上游」「更新上游代码」「完善中文」「发布新版本」时使用。
---

# 上游同步 + 中文完善 + 发布

本仓库是 `BillyNabil/nabyscape` 的 fork，维护中文界面与国内天气数据源（NMC / 和风）。上游更新后按本流程同步、翻译、发布。

## 关键背景（改动映射）

本地化架构（上游更新后必须逐一核对的位置）：

| 文件 | 作用 | 中文化要求 |
|---|---|---|
| `src/NabyScape/UI/Shared/LocalizationCatalog.cs` | 目录：430+ 条目，值为 `(Spanish, Indonesian, Chinese)` 三元组 | 每条必须有三元组格式；上游新增条目补中文 |
| `src/NabyScape/UI/Shared/Localization.cs` | `Text()` 语言分支 | switch 需含 `"zh" when entry.Chinese.Length > 0` |
| `src/NabyScape/Application/LanguageAccessibilitySettings.cs` | `NormalizeLanguageAccessibility` | `UiLanguage is "es" or "id" or "zh"` |
| `src/NabyScape/UI/Settings/SettingsLanguageAccessibility.cs` | 语言下拉 | `codes` 含 `"zh"`、显示名含 `"中文"` |
| `src/NabyScape/UI/Settings/SettingsScene.cs` | 场景预览语言名 | `s.UiLanguage=="zh"?"中文"` 分支 |
| `src/NabyScape/Tests/ProductionFoundationTests.cs` | 归一化测试 | 语言数组 `{"es","id","zh","en"}` |
| `src/NabyScape/Diagnostics/ProductionUiDiagnostics.cs` | UI 诊断 | 代码数组 `{"en","es","id","zh"}` |
| `docs/presets/default-preferences.json` | 嵌入资源：出厂偏好 | **必须与 `Settings` 全部公开属性一一对应**，缺一个字段就挂 `Fresh profiles use the complete bundled preferences` 测试（真实踩坑） |

本项目独有改动（rebase 冲突时重点保护）：

- `src/NabyScape/Features/Weather/WeatherService.cs`：NMC（免 Key）/ QWeather（Key）/ Open-Meteo 三源；`WeatherPlace.StationCode`、`ConditionText`、`Num()` 字符串数值解析、`NmcToCode`/`QwToCode` 图标映射、`NmcProvinces` 省份表
- `src/NabyScape/Features/Weather/WeatherView.cs`：数据源下拉、和风 Key 输入行、`UpdateCredit()`
- `src/NabyScape/Application/FlexibleExperience.cs`：`WeatherSource`（默认 `"nmc"`）/`WeatherStationCode`/`WeatherApiKey` 及归一化
- `src/NabyScape/Application/PreferenceReset.cs`：Weather 重置字段组
- `src/NabyScape/Diagnostics/ShowcaseDiagnostics.cs`：合成天气需显式 `WeatherSource="open-meteo"`
- AI 语音对话语言的 `"zh"`/`"Chinese"`（`AssistantSpeechSetup`/`AssistantOwnSpeechSettings`/`AssistantRuntime`）

## 流程

### 阶段 1：同步上游

1. 确认工作区干净：`git status --short` 必须为空
2. 配置上游 remote（仅需一次，fetch 用 HTTPS 无需认证）：
   `git remote add upstream https://github.com/BillyNabil/nabyscape.git`（已有则跳过）
3. `git fetch upstream`，然后评估差异：`git log --oneline main..upstream/main` 与 `git diff --stat main...upstream/main`
4. 变基保持历史线性：`git rebase upstream/main`（不使用 merge）。冲突处理原则：
   - `LocalizationCatalog.cs`：保留本项目的三元组结构和全部中文；把上游新增的英文键按上游顺序插入，逐条补中文
   - 天气相关文件：保留 NMC/QWeather 分支；若上游重构了天气服务，把三源逻辑移植到新结构上
   - 拿不准的冲突先看上游该提交的意图（`git show <sha>`）
5. 变基完成后立刻跑「阶段 2 校验」再继续

### 阶段 2：完善中文

1. 运行校验脚本（本 skill 自带）：
   `pwsh -File .agents/skills/sync-upstream-zh/scripts/check-localization.ps1`
   覆盖：三元组格式完整、Settings 公开属性 vs `default-preferences.json` 一致、`zh` 注册点齐全（6 处）
2. 找上游新增但未进目录的 UI 文案：在 `src` 里 grep `Localization.Text(`、`Localization.Format(`、`UI.Text(`、`UI.Button(`、`UI.Caption(`、`UI.CaptionButton(`，新英文串若无对应目录键，先补键再补翻译
3. 翻译规范：
   - 简体中文，桌面软件 UI 惯用语（Cancel=取消、Save=保存、dock 保留不译、island 译「岛」）
   - `{0}`/`{1}` 占位符原样保留；省略号用 `…`，中文引号用 `“ ”`
   - 品牌/协议名（NabyScape、Hermes、Open-Meteo、SHA-256）不译
   - 顺带补齐该条的西语/印尼语空缺（若上游也没写）
4. `switch` 表达式映射表（天气图标码等）检查无重复分支——重复分支报 CS8510 编译错误（真实踩坑）
5. 本机不装 .NET SDK，**编译与测试验证一律交给 GitHub Actions**

### 阶段 3：发布

1. 中文 commit message 提交全部改动
2. `git push origin main`（remote origin 已是 SSH：`github:YangAoLib/nabyscape.git`；凭据对话不可用，勿用 HTTPS push）
3. GitHub Actions `Windows build` 自动触发（push 到 main 或 `workflow_dispatch`）：
   - 查询：`gh run list --repo YangAoLib/nabyscape --limit 3`
   - 监控：`gh run watch <run-id> --repo YangAoLib/nabyscape --exit-status`
   - 失败排查：`gh run view <run-id> --repo YangAoLib/nabyscape --log-failed`
4. 成功后 artifact `NabyScape-windows-x64`（自包含单文件 exe + 许可文件）可在 run 页面下载，保留 14 天
5. 常见失败对照：
   - `Fresh profiles use the complete bundled preferences…` → `default-preferences.json` 缺新 Settings 字段
   - `CS8510 pattern is unreachable` → switch 重复分支
   - `Logic tests failed` → 下载 run 的测试报告或看 log 中 `FAIL` 行
6. 正式 tag 发版（已配置好）：fork 用「上游 tag + `-cn`」发布，例如上游 `v2.26.1` → 本 fork `v2.26.1-cn`：
   - 前置：`release.yml` 条件已支持 fork 的 `-cn` tag（上游原仓库的普通 tag 流程不受影响）；`tools/Build-Release.ps1` 已接受 `vX.Y.Z-cn` 并按去掉 `-cn` 的版本号校验 `NabyScape.csproj` 的 `<Version>`
   - 同步上游后确认 `<Version>` 与上游最新 tag 一致（上游 bump 版本时跟着改）
   - 发布步骤：
     ```powershell
     git fetch upstream --tags
     git tag --sort=-v:refname | Select-Object -First 3   # 确认上游最新 tag
     git tag v2.26.1-cn && git push origin v2.26.1-cn     # 示例：上游 v2.26.1
     gh run watch --repo YangAoLib/nabyscape $(gh run list --repo YangAoLib/nabyscape --workflow "Draft official release" --limit 1 --json databaseId --jq '.[0].databaseId') --exit-status
     ```
   - workflow 产出 **draft release**（不自动发布）：x64/ARM64 的 Setup.exe + Portable.zip、`NabyScape-Notices.zip`、`SHA256SUMS`
   - 到 GitHub Releases 页面检查 draft 的说明与产物，手动 Publish 即为正式发版；tag 名与产物文件名保留 `-cn` 标识，与上游 release 区分

## 注意事项

- fork 默认禁用 Actions；新 fork 需先启用（网页 Settings → Actions，或 `gh api -X PUT repos/YangAoLib/nabyscape/actions/permissions -f enabled=true -f allowed_actions=all`）
- 上游可能调整架构（文件移动、目录键改元组结构、语言机制换成 .resx 等）：发现与上表路径不符时，先探索上游实现再迁移中文，不要凭记忆硬套
- 翻译量大时优先保证：设置窗口、通知中心、天气面板、更新提示等高频界面；低频诊断文案可最后补
- 始终用中文回复用户与写 commit message（项目约定见用户 AGENTS.md）
