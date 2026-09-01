# BepisPlugins
A collection of essential [BepInEx](https://github.com/BepInEx/BepInEx) plugins for Koikatu / Koikatsu Party, EmotionCreators, AI-Shoujo / AI-Girl, HoneySelect2, HoneyCome, SamabakeScramble / Summer Vacation Scramble, and other games by Illusion/Illgames. Check plugin descriptions below for a full list of included plugins. 

If you wish to contribute or need help, check the #help channel on the [Koikatsu discord server](https://discord.gg/hevygx6).

## Narushio Sideloader extensions

[English](#english) · [日本語](#日本語) · [简体中文](#简体中文)

### English

This fork extends the Koikatu Sideloader with runtime zipmod hot reload. It is a secondary development of BepisPlugins, focused on updating mods during development or library maintenance without restarting the game for every file change.

Added capabilities:

- Add, replace, or remove `.zipmod` and `.zip` files while Koikatu or CharaStudio is running.
- Reload manually with `Ctrl+F6`, or open the status window with `Ctrl+F7` to reload and review added, updated, removed, and failed files.
- Refresh Sideloader metadata, Universal Auto Resolver mappings, resource routes, and Maker lists that have already been initialized.
- Keep loaded AssetBundles in a configurable, content-addressed disk cache so source archives can be replaced or deleted without being locked by the game. Old cache entries are cleaned by age and size limits.
- Preserve runtime slot mappings where possible to reduce broken references after a mod update.
- Expose `ReloadZipmods()`, the backwards-compatible `ReloadNewZipmods()`, and the `ZipmodsHotReloaded` event for other plugins.

Version compatibility:

| Build | BepInEx | Extended Save | XUnity Resource Redirector |
| --- | --- | --- | --- |
| Standard Koikatu build | `5.4.22` | `21.1.2` or newer | `1.1.0` or newer |
| Legacy pack compatibility build | `5.4.19` | `18.2.0.3` or newer | `1.1.0` or newer |

This extension targets Koikatu/CharaStudio (`KK`) only; it is not a Koikatsu Sunshine (`KKS`) or other-game build. Install exactly one Sideloader build that matches the BepInEx and Extended Save generation already used by the game. Do not install the standard and legacy DLLs together, and do not mix Extended Save DLLs or patchers from different packs. SharpZipLib `0.86.0.518` is the runtime archive dependency and should be distributed beside Sideloader by the matching release package. A full game restart is required after installing or changing plugin DLLs; hot reload applies to later `.zipmod` / `.zip` changes.

Hot reload updates future loads; objects already instantiated in a character or scene are not forcibly replaced. Reload the card, coordinate, scene, or affected item when necessary. Some CharaStudio menus and third-party plugin caches may still require a restart.

License: This fork follows the original project's [LGPL-3.0 license](https://github.com/IllusionMods/BepisPlugins#LGPL-3.0-1-ov-file).

### 日本語

このフォークは、Koikatu 向け Sideloader に実行時 zipmod ホットリロードを追加した BepisPlugins の二次開発版です。MOD の開発やライブラリ整理の際に、ファイルを変更するたびゲームを再起動する手間を減らすことを目的としています。

追加機能：

- Koikatu または CharaStudio の実行中に `.zipmod` / `.zip` を追加・差し替え・削除できます。
- `Ctrl+F6` で手動リロード、`Ctrl+F7` で状態ウィンドウを開き、追加・更新・削除・失敗したファイルを確認できます。
- Sideloader のメタデータ、Universal Auto Resolver のマッピング、リソース経路、初期化済みの Maker リストを更新します。
- 使用した AssetBundle を設定可能なコンテンツアドレス方式のディスクキャッシュに保存し、ゲーム実行中でも元のアーカイブをロックせず差し替え・削除できるようにします。古いキャッシュは保存期間と容量の上限に従って自動整理されます。
- 可能な限り実行時 Slot ID を維持し、MOD 更新後の参照切れを減らします。
- 他のプラグイン向けに `ReloadZipmods()`、互換 API の `ReloadNewZipmods()`、`ZipmodsHotReloaded` イベントを提供します。

対応バージョン：

| ビルド | BepInEx | Extended Save | XUnity Resource Redirector |
| --- | --- | --- | --- |
| Koikatu 標準ビルド | `5.4.22` | `21.1.2` 以上 | `1.1.0` 以上 |
| 旧環境互換ビルド | `5.4.19` | `18.2.0.3` 以上 | `1.1.0` 以上 |

この拡張は Koikatu/CharaStudio（`KK`）専用で、Koikatsu Sunshine（`KKS`）や他ゲーム向けではありません。ゲームで使用中の BepInEx と Extended Save の世代に合う Sideloader を一つだけ導入してください。標準版と旧環境版を同時に配置したり、異なる配布パックの Extended Save DLL／パッチャーを混在させたりしないでください。実行時のアーカイブ依存は SharpZipLib `0.86.0.518` で、対応する配布パッケージから Sideloader と一緒に導入します。プラグイン DLL の導入・交換後はゲームを完全に再起動する必要があり、その後の `.zipmod` / `.zip` 変更にホットリロードを使用できます。

ホットリロードは以後の読み込みに反映されます。キャラクターやシーンですでに生成済みのオブジェクトは強制置換されないため、必要に応じてカード、コーディネート、シーン、または対象アイテムを再読み込みしてください。CharaStudio の一部メニューや他プラグイン独自のキャッシュは、再起動が必要な場合があります。

ライセンス：このフォークは原プロジェクトの [LGPL-3.0 license](https://github.com/IllusionMods/BepisPlugins#LGPL-3.0-1-ov-file) に従います。

### 简体中文

本分支是对 BepisPlugins 中 Koikatu Sideloader 的二次开发，增加了运行时 zipmod 热重载，主要用于 MOD 开发和模组库维护，减少每次修改文件后都要重启游戏的等待。

新增功能：

- 在 Koikatu 或 CharaStudio 运行期间新增、替换或删除 `.zipmod` / `.zip`。
- 使用 `Ctrl+F6` 手动重载，或使用 `Ctrl+F7` 打开状态窗口，执行重载并查看新增、更新、删除和失败的文件。
- 刷新 Sideloader 元数据、Universal Auto Resolver 映射、资源路由，以及已经初始化的 Maker 列表。
- 将实际使用过的 AssetBundle 保存到可配置的内容寻址磁盘缓存，使游戏运行时仍可替换或删除源压缩包，避免文件被长期占用；旧缓存会按保留时间和容量上限自动清理。
- 尽可能保留运行时 Slot ID，降低 MOD 更新后角色卡或服装卡引用失效的风险。
- 为其他插件提供 `ReloadZipmods()`、向后兼容的 `ReloadNewZipmods()` 和 `ZipmodsHotReloaded` 事件。

版本兼容性：

| 构建版本 | BepInEx | Extended Save | XUnity Resource Redirector |
| --- | --- | --- | --- |
| Koikatu 标准构建 | `5.4.22` | `21.1.2` 或更高 | `1.1.0` 或更高 |
| 旧整合包兼容构建 | `5.4.19` | `18.2.0.3` 或更高 | `1.1.0` 或更高 |

此扩展仅适用于 Koikatu/CharaStudio（`KK`），不适用于 Koikatsu Sunshine（`KKS`）或其他游戏。请只安装一个与游戏现有 BepInEx、Extended Save 代际匹配的 Sideloader，不能同时放置标准版和旧环境版 DLL，也不要混用不同整合包中的 Extended Save DLL 或补丁器。运行时压缩包依赖为 SharpZipLib `0.86.0.518`，应由匹配的发布包与 Sideloader 一同提供。首次安装或更换插件 DLL 后必须完整重启游戏；之后修改 `.zipmod` / `.zip` 时才可以使用热重载。

热重载会影响之后的资源加载；角色或场景中已经实例化的对象不会被强制替换。必要时请重新加载角色卡、服装卡、场景或重新选择对应物品。CharaStudio 的部分菜单以及其他插件自己的缓存仍可能需要重启后才能完全刷新。

许可证：本分支遵循原项目的 [LGPL-3.0 license](https://github.com/IllusionMods/BepisPlugins#LGPL-3.0-1-ov-file)。

### How to install
1. Install the latest version of [BepInEx](https://github.com/BepInEx/BepInEx). Make sure it is installed and working before installing BepisPlugins.
   - For HoneySelect2 and games older than it, get BepInEx 5.
   - For RoomGirl/HoneyCome and games newer than it, get BepInEx 6 (nightly build 668 or later).
2. Install the latest version of the [ConfigurationManager](https://github.com/BepInEx/BepInEx.ConfigurationManager) plugin.
3. Download the latest release archive for your game (specified by the two letter prefix, e.g. AI for AI-Girl) from the releases page (not the "Clone or download" button).
4. Extract the archive into your game directory (where the game exe and BepInEx folder are located). Replace old files if asked.

## Plugin descriptions
You can see more information about some of the plugins by checking their config files in `BepInEx\config` (or by using the in-game [ConfigurationManager plugin](https://github.com/BepInEx/BepInEx.ConfigurationManager)).

Note: Not all plugins might be available for a given game (not yet ported by anyone, or technically infeasible).

### BGMLoader
Loads custom BGMs and clips played on game startup. Stock audio is replaced during runtime by custom clips from BepInEx\BGM and BepInEx\IntroClips directories.

[Tutorial on how to replace sound clips and background music using BGMLoader.](https://github.com/IllusionMods/BepisPlugins/wiki/BGM-Loader)

### ColorCorrector
Allows configuration of some post-processing filters. (change of bloom amount, disable saturation filter)

### ExtensibleSaveFormat
Allows additional data to be saved to character, coordinate and scene cards. The cards are fully compatible with non-modded game, the additional data is lost in that case. This is used by sideloader to store used mod information.

### InputUnlocker
Allows user to input longer than normal values to InputFields. This allows longer names and other properties stored as text.

### Screencap
Creates screenshots based on settings. Can create screenshots of much higher resolution than what the game is running at. It can make screen (F9 key) or character (F11 key) screenshots.

### Sideloader
Loads mods packaged in .zip archives from the Mods directory without modifying the game files at all. You don't unzip them, just drag and drop to Mods folder in the game root.

It prevents mods from colliding with each other (i.e. 2 mods have same item IDs and can't coexist; sideloader automatically assigns correct IDs). It also makes it easy to disable/remove mods with no lasting effects on your game install (just remove the .zip, no game files are changed at any point).

> Note: Sideloader is not available for games by Illgames because of technical reasons (IL2CPP). You will have to use [SardineTail](https://github.com/MaybeSamigroup/SVS-SardineTail/wiki) for them instead.

[More information and tutorial on sideloader-compatible mod creation.](https://github.com/IllusionMods/BepisPlugins/wiki/1-Introduction-to-zipmod-format)

[Step-by-step guide for creating a simple texture mod.](https://github.com/IllusionMods/BepisPlugins/wiki/2-How-to-create-a-simple-zipmod)

[Tool for automatically converting old list mods to sideloader-compatible form.](https://github.com/IllusionMods/ZipStudio/releases)

### SliderUnlocker
Allows user to set values outside of the standard 0-100 range on all sliders in the editor.

### IMGUIModule.Il2Cpp.CoreCLR.Patcher
Fixes issues with IMGUI caused by the game being IL2CPP that prevent other plugins like ConfigurationManager from being displayed correctly.

## Removed plugins

### Configuration Manager
Moved to https://github.com/BepInEx/BepInEx.ConfigurationManager

### DeveloperConsole
Moved to https://github.com/BepInEx/DeveloperConsole

### IPALoader
Moved to https://github.com/BepInEx/IPALoaderX

### MessageCenter
Moved to https://github.com/BepInEx/MessageCenter

### ScriptEngine
Moved to https://github.com/BepInEx/BepInEx.Debug

## Obsolete plugins
### DynamicTranslationLoader
Replaced by [XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator)

### ResourceRedirector
Replaced by [XUnity.ResourceRedirector](https://github.com/bbepis/XUnity.AutoTranslator)
