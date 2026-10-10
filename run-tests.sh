#!/usr/bin/env bash
# 跑单元测试。用 xunit v3 的独立可执行文件，不依赖 vstest 的 testhost。
set -euo pipefail
cd "$(dirname "$0")"

dotnet build tests/Translator.Core.Tests -c Debug -v q --nologo
./tests/Translator.Core.Tests/bin/Debug/net9.0/Translator.Core.Tests.exe "$@"
