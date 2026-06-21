#!/usr/bin/env bash
# install.sh —— 在新环境一键构建 endfield-dump 并安装为全局命令
#
# 用法：
#   ./install.sh              # 构建并安装到 ~/.local/bin
#   ./install.sh /usr/local   # 构建并安装到 /usr/local/bin（需要 sudo）
#   ./install.sh --skip-build # 只生成 wrapper，跳过构建（已构建过的情况）
#
# 前置条件：
#   - .NET 9 SDK（dotnet --version 输出 9.x）
#   - Linux x64（Texture2D 解码依赖 native .so）
#
set -e

# ── 解析参数 ──
PREFIX="${HOME}/.local"
SKIP_BUILD=false
for arg in "$@"; do
    case "$arg" in
        --skip-build) SKIP_BUILD=true ;;
        -h|--help)
            sed -n '2,12p' "$0"
            exit 0
            ;;
        *)
            PREFIX="$arg"
            ;;
    esac
done

# ── 定位项目根目录（脚本所在目录） ──
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# ── 检查 dotnet ──
if ! command -v dotnet &>/dev/null; then
    echo "错误：未找到 dotnet 命令。请先安装 .NET 9 SDK：" >&2
    echo "  https://dotnet.microsoft.com/download/dotnet/9.0" >&2
    exit 1
fi

DOTNET_VERSION=$(dotnet --version 2>/dev/null | cut -d. -f1)
if [ "$DOTNET_VERSION" -lt 9 ]; then
    echo "错误：需要 .NET 9 SDK，当前是 $DOTNET_VERSION.x" >&2
    echo "  https://dotnet.microsoft.com/download/dotnet/9.0" >&2
    exit 1
fi
echo "✓ dotnet $(dotnet --version)"

# ── 构建 ──
if [ "$SKIP_BUILD" = false ]; then
    echo "构建 endfield-dump（Release）..."
    dotnet build AnimeStudio.Endfield.Cli/AnimeStudio.Endfield.Cli.csproj -c Release -r linux-x64
fi

# ── 定位构建产物 ──
DLL="$SCRIPT_DIR/AnimeStudio.Endfield.Cli/bin/Release/net9.0/linux-x64/endfield-dump.dll"
if [ ! -f "$DLL" ]; then
    # 回退到非 RID 路径
    DLL="$SCRIPT_DIR/AnimeStudio.Endfield.Cli/bin/Release/net9.0/endfield-dump.dll"
fi
if [ ! -f "$DLL" ]; then
    echo "错误：找不到构建产物 endfield-dump.dll" >&2
    echo "  请先运行：dotnet build AnimeStudio.Endfield.Cli -c Release" >&2
    exit 1
fi
echo "✓ 构建产物：$DLL"

# ── 安装 wrapper ──
BIN_DIR="$PREFIX/bin"
mkdir -p "$BIN_DIR"

DOTNET_PATH="$(command -v dotnet)"
cat > "$BIN_DIR/endfield-dump" << EOF
#!/usr/bin/env bash
exec "$DOTNET_PATH" exec "$DLL" "\$@"
EOF
chmod +x "$BIN_DIR/endfield-dump"

echo "✓ 已安装：$BIN_DIR/endfield-dump"

# ── 检查 PATH ──
case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *)
        echo ""
        echo "⚠ $BIN_DIR 不在 PATH 中，请添加到 shell 配置："
        echo "  echo 'export PATH=\"$BIN_DIR:\$PATH\"' >> ~/.bashrc"
        echo "  source ~/.bashrc"
        ;;
esac

# ── 验证 ──
echo ""
echo "验证："
"$BIN_DIR/endfield-dump" --help 2>&1 | head -5 || true
echo ""
echo "安装完成。用法："
echo "  endfield-dump list --vfs /path/to/StreamingAssets"
echo "  endfield-dump extract --vfs /path/to/StreamingAssets --out ./out --asset-name '^pic_\\d+_chr_'"
