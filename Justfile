set shell := ["zsh", "-cu"]

solution := "FsLangMcp.slnx"
test_project := "tests/FsLangMcp.Tests/FsLangMcp.Tests.fsproj"
tool_source := "nupkg"
runtime_tool_path := ".runtime-tools"

default:
    just --list

restore:
    dotnet restore {{solution}} --locked-mode

# Regenerate packages.lock.json after intentionally editing an exact version.
# This does not discover or select dependency updates; Dependabot proposes those.
restore-update:
    dotnet restore {{solution}} --force-evaluate

# Restore the reviewed exact FSAC/ProjInfo/Fantomas/analyzer versions from dotnet-tools.json.
tool-restore:
    dotnet tool restore

# Materialize the supported FSAC/ProjInfo/Fantomas set on PATH-compatible shims.
runtime-tools:
    python3 scripts/runtime-toolchain.py install --tool-path {{runtime_tool_path}}

# Exercise the real MCP -> FSAC/proj-info path on the current OS.
live-fsac: restore runtime-tools
    dotnet build FsLangMcp.fsproj --configuration Release --no-restore --warnaserror
    python3 scripts/runtime-toolchain.py smoke --tool-path {{runtime_tool_path}} --server bin/Release/net10.0/FsLangMcp.dll --project FsLangMcp.fsproj

build:
    dotnet build {{solution}} --no-restore

test:
    dotnet test {{test_project}} --no-restore

check: build test

# Drive the built server over real stdio against a project pinning an absent SDK
# (#192 acceptance criterion). Skips itself when nothing is built yet.
e2e-sdk-pin: build
    FSLANGMCP_SERVER_DLL=bin/Debug/net10.0/FsLangMcp.dll python3 -m unittest -v scripts.tests.test_sdk_pin_preflight_e2e

audit-descriptions:
    python3 scripts/audit-tool-descriptions.py

analyze: tool-restore
    dotnet msbuild FsLangMcp.fsproj /t:AnalyzeFSharpProject
    dotnet msbuild {{test_project}} /t:AnalyzeFSharpProject

pack:
    dotnet pack FsLangMcp.fsproj --no-restore -c Debug

install-local: pack
    dotnet tool uninstall -g fslangmcp || true
    dotnet tool install -g --add-source {{tool_source}} FsLangMcp
