#!/usr/bin/env bash
# AI-Tool 一键构建脚本（Linux/macOS/Git Bash）
# 用法：./build.sh
# 效果：构建前端 → 构建后端，产物输出到 src/AITool.Web/wwwroot
set -e

echo "=== 1. 构建前端 ==="
cd frontend
if [ ! -d node_modules ]; then
    echo "未检测到 node_modules，执行 npm install..."
    npm install
fi
npm run build
cd ..
echo "前端构建完成，产物已输出到 src/AITool.Web/wwwroot"

echo ""
echo "=== 2. 构建后端 ==="
dotnet build src/AITool.Web/AITool.Web.csproj
echo "后端构建完成"

echo ""
echo "=== 构建全部完成 ==="
echo "运行：cd src/AITool.Web && dotnet run"
echo "访问：http://localhost:5029"
