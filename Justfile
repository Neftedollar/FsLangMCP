set shell := ["zsh", "-cu"]

solution := "FsLangMcp.slnx"
test_project := "tests/FsLangMcp.Tests/FsLangMcp.Tests.fsproj"
tool_source := "nupkg"

default:
    just --list

restore:
    dotnet restore {{solution}}

tool-restore:
    dotnet tool restore

build:
    dotnet build {{solution}} --no-restore

test:
    dotnet test {{test_project}} --no-restore

check: build test

analyze: tool-restore
    dotnet msbuild FsLangMcp.fsproj /t:AnalyzeFSharpProject
    dotnet msbuild {{test_project}} /t:AnalyzeFSharpProject

pack:
    dotnet pack FsLangMcp.fsproj --no-restore -c Debug

install-local: pack
    dotnet tool update -g fslangmcp --add-source {{tool_source}}
