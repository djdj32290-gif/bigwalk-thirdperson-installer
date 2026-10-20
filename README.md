# Big Walk 第三人称安装器

Windows x64 桌面工具，提供 ThirdPersonComfort 的安装与卸载。软件内置 BepInEx IL2CPP 加载器及 mod，不需要另外安装 mod 管理器。

## 下载与使用

成品位于 [GitHub Releases](https://github.com/djdj32290-gif/bigwalk-thirdperson-installer/releases/latest)，文件名为 `BigWalk-ThirdPerson-Setup-v1.0.2.exe`。下载后双击运行。文件校验值见 [`outputs/SHA256SUMS.txt`](outputs/SHA256SUMS.txt)。

1. 退出 Big Walk。
2. 打开 EXE，确认自动识别的游戏目录；也可手动选择 `Big Walk.exe` 所在目录。
3. 点击“安装第三人称”，完成后从 Steam 启动游戏。
4. **F4** 切换第一/第三人称；鼠标滚轮调整镜头距离，滚到最近会进入第一人称。
5. 默认勾选“交互时也保持第三人称”；取消后会保留 mod 的交互拉近行为。
6. 卸载前先退出游戏，再打开同一个 EXE 点击“卸载 mod”。

首次启动会下载对应 Unity 支持库并生成 IL2CPP 互操作程序集，可能需要等待数分钟。后续一般会复用生成文件，游戏更新后可能重新生成。

安装器只负责安装/卸载，玩游戏时可以关闭。它不会修改游戏原始程序、添加开机自启动或修改系统安全设置。

## 卸载与保护

安装记录保存在游戏目录下的 `.bigwalk-thirdperson/`；原有 mod 配置会备份。卸载会按记录恢复配置并移除本工具管理的文件。

如果检测到其他 mod、未知文件或加载器被更新，为保护这些内容可能保留共用加载器。安装记录损坏时会停止操作，不能把“没有安装记录”当作干净卸载。不要手动删除安装记录后再要求自动恢复。

Steam 卸载游戏不会可靠地同步清理额外的 mod 文件；建议先卸载 mod，再卸载游戏。游戏和存档不属于本工具的清理目标。

## 构建

需要 Windows、Python 3（仅标准库）及系统自带的 .NET Framework C# 编译器。无需安装新的 .NET SDK 或 Python 依赖包。

```powershell
python src/build.py
```

构建脚本自动将固定版本的依赖包下载到项目 `work/downloads/`，验证固定 SHA-256，嵌入单个 EXE，输出到 `outputs/`。缓存和临时文件留在项目目录，不依赖开发机器的绝对路径。

```powershell
python src/build.py --tests
.\work\build\InstallerTests.exe "$PWD\work\tests-check"
```

测试目录必须是当前项目 `work/` 下尚不存在的 `tests-` 开头目录。测试不会使用真实游戏目录。

## 检查状态与限制

- 15 项安装/卸载测试通过，覆盖原文件保护、配置恢复、共享加载器、失败回滚、并发文件改动、损坏记录与目录越界。
- 已修复安装记录不完整时误报成功、回滚冲突时丢失恢复记录，以及生成目录内未知文件可能被误删的边界问题。
- 安装器未做代码签名。源码检查及包内容一致性核对不等同于杀毒认证；开发机器的 Defender 未运行，扫描尝试失败，**没有“杀毒扫描通过”的结论**。
- 已观察到本机首次启动成功下载 Unity 支持库并生成互操作程序集；尚未完成第三人称实际游玩、联机与过场的完整验证。
- 无法保证第三方 mod 对未来游戏更新保持兼容。

## 第三方组件

| 组件 | 固定版本 | 来源 / 许可 |
| --- | --- | --- |
| ThirdPersonComfort | 1.1.0 | [Thunderstore](https://thunderstore.io/c/big-walk/p/Ranats/ThirdPersonComfort/)，[源码](https://github.com/Ranats/bigwalk-third-person-comfort)，MIT |
| BepInExPack IL2CPP | 6.0.788 | [Thunderstore](https://thunderstore.io/c/big-walk/p/BepInEx/BepInExPack_IL2CPP/)，[BepInEx 源码](https://github.com/BepInEx/BepInEx/tree/5b766a3b7f6c164d4798924a93f3acf4db769d06)，LGPL-2.1 |
| .NET runtime | 由 BepInExPack 携带的 6.0.7 | [源码及许可](https://github.com/dotnet/runtime/tree/v6.0.7) |

`third_party/` 保留本次参考的 ThirdPersonComfort 源码、提交记录和第三方许可。第三方组件保留各自许可，安装器自身源码见根目录 `LICENSE`。不包含 Big Walk 游戏程序、素材、存档或本机运行日志。
