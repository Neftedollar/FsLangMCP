set shell := ["zsh", "-cu"]

solution := "FsLangMcp.slnx"
test_project := "tests/FsLangMcp.Tests/FsLangMcp.Tests.fsproj"
tool_source := "nupkg"

default:
    just --list

restore:
    dotnet restore {{solution}} --locked-mode

# Update packages.lock.json after changing a PackageReference (or to pull new pins).
restore-update:
    dotnet restore {{solution}} --force-evaluate

tool-restore:
    dotnet tool restore

build:
    dotnet build {{solution}} --no-restore

test:
    dotnet test {{test_project}} --no-restore

check: build test

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
