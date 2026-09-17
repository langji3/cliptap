# Windows 安装向导

使用 Inno Setup 6.7.3 构建单文件 `ClipTap-win-x64-setup.exe`。欢迎、选择安装位置、确认安装、进度、完成页面；不创建桌面/开始菜单快捷方式。完成页默认勾选后台启动，静默安装不启动程序。

安装仅限当前用户，无需管理员权限；内置 .NET 运行时。稳定 AppId 支持覆盖升级及 Windows“已安装的应用”卸载。卸载保留 `%LOCALAPPDATA%\ClipTap` 的用户库。

`ChineseSimplified.isl` 来自 Inno Setup 官方仓库的 [6.7.3 简体中文翻译](https://github.com/jrsoftware/issrc/blob/is-6_7_3/Files/Languages/Unofficial/ChineseSimplified.isl)，保留原文件署名。编译器由构建脚本下载官方发行文件并验证固定 SHA-256，以便携模式放入忽略的 `artifacts/tools`。

`-TestInstaller` 使用独立 AppId、不启动应用，供隔离安装/升级/卸载测试；不修改真实 ClipTap 卸载登记。
