module FsLangMcp.Tests.CreateFilePlanTests

// ─── #66: fcs_create_file_plan — "where should this new .fs file go?" ─────────────
//
// PLANNING ONLY: FcsBridge.CreateFilePlan loads a project's resolved <Compile> order
// (SourceFiles), recommends an insertion index, and infers the namespace/module house
// convention from neighbouring files. It creates nothing and edits no .fsproj.
//
// Fixture: ONE project with three modules under namespace MyApp, in compile order
// Domain.fs → Services.fs → Program.fs. As with CompileOrderTests we only RESTORE (not
// build): Ionide.ProjInfo needs obj/ assets to resolve SourceFiles, not a successful
// compile. The sources are deliberately trivial — the planner reads their top
// declaration line, it never type-checks them.

open System
open System.IO
open System.Diagnostics
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Xunit.Abstractions
open FsLangMcp.Types
open FsLangMcp.FcsBridge

// ── Shared sources: three single-module files under one namespace root ────────────

let private moduleFile (name: string) =
    String.concat "\n" [ $"module MyApp.{name}"; ""; $"let {name.ToLowerInvariant()}Value = 1"; "" ]

let private projectFile =
    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
          "  <ItemGroup>"
          "    <Compile Include=\"Domain.fs\" />"
          "    <Compile Include=\"Services.fs\" />"
          "    <Compile Include=\"Program.fs\" />"
          "  </ItemGroup>"
          "</Project>" ]

// ── Class fixture: written + restored ONCE, shared across the class ───────────────

type CreateFilePlanFixture() =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_fileplan_{runId}")

    let write (rel: string) (content: string) =
        let full = Path.Combine(root, rel)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, content)
        full

    let fsproj = write "App/App.fsproj" projectFile
    do write "App/Domain.fs" (moduleFile "Domain") |> ignore
    do write "App/Services.fs" (moduleFile "Services") |> ignore
    do write "App/Program.fs" (moduleFile "Program") |> ignore

    // Restore (not build): resolves SourceFiles without needing a successful compile.
    let restoreOnce () =
        let psi = ProcessStartInfo("dotnet", $"restore \"{fsproj}\" -nologo")
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.Environment["MSBUILDDISABLENODEREUSE"] <- "1"
        psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] <- "1"
        use p = Process.Start(psi)
        let stdout = p.StandardOutput.ReadToEnd()
        let stderr = p.StandardError.ReadToEnd()
        p.WaitForExit()
        p.ExitCode, stdout + stderr

    let rec restoreWithRetry attempt =
        let code, log = restoreOnce ()

        if code = 0 || attempt >= 3 then
            code, log
        else
            System.Threading.Thread.Sleep 1500
            restoreWithRetry (attempt + 1)

    let restoreExit, restoreLog = restoreWithRetry 1

    member _.Fsproj = fsproj
    member _.RestoreExit = restoreExit
    member _.RestoreLog = restoreLog

    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists root then
                try
                    Directory.Delete(root, true)
                with _ ->
                    ()

// ── JSON helpers ─────────────────────────────────────────────────────────────────

let private gi (node: JsonNode) (key: string) = node[key].GetValue<int>()
let private gs (node: JsonNode) (key: string) = node[key].GetValue<string>()

/// compileIndex of the first nearbyFiles entry whose file ends with `suffix`.
let private nearbyIndexOf (result: JsonNode) (suffix: string) : int =
    let arr = result["nearbyFiles"] :?> JsonArray
    let mutable found = None

    for i in 0 .. arr.Count - 1 do
        let n = arr[i]

        if found.IsNone && (n["file"].GetValue<string>()).EndsWith(suffix) then
            found <- Some(n["compileIndex"].GetValue<int>())

    match found with
    | Some idx -> idx
    | None -> failwith $"{suffix} not present in nearbyFiles"

let private planArgs (projectPath: string) (fileName: string) : FcsCreateFilePlanArgs =
    { fileName = fileName
      afterFile = None
      namespaceOrModule = None
      projectPath = Some projectPath }

// ─────────────────────────────────────────────────────────────────────────────────

type CreateFilePlanTests(fx: CreateFilePlanFixture, output: ITestOutputHelper) =
    interface IClassFixture<CreateFilePlanFixture>

    [<Fact>]
    member _.``afterFile lands the new file at the index right after it``() : Task =
        task {
            Assert.True((fx.RestoreExit = 0), $"Fixture restore failed (exit {fx.RestoreExit}):\n{fx.RestoreLog}")
            let bridge = FcsBridge()

            let! result = bridge.CreateFilePlan({ planArgs fx.Fsproj "Validation.fs" with afterFile = Some "Domain.fs" })

            Assert.Equal("succeeded", gs result "status")

            // Recommended index is exactly afterFile's compile index + 1 (robust to any
            // generated source prefixes a toolchain might inject ahead of Domain.fs).
            let domainIdx = nearbyIndexOf result "Domain.fs"
            let recommended = gi result "recommendedCompileIndex"
            output.WriteLine($"domain compileIndex={domainIdx}, recommended={recommended}")
            Assert.Equal(domainIdx + 1, recommended)

            // The sibling bracketing is reported relative to the recommended slot.
            Assert.EndsWith("Domain.fs", gs result "insertAfter")
            Assert.EndsWith("Services.fs", gs result "insertBefore")

            // The exact .fsproj edit names the new file and the sibling it follows.
            Assert.Contains("Validation.fs", gs result "fsprojOp")
            Assert.Contains("Domain.fs", gs result "fsprojOp")
        }

    [<Fact>]
    member _.``namespace convention is inferred from neighbouring files``() : Task =
        task {
            Assert.True((fx.RestoreExit = 0), $"Fixture restore failed (exit {fx.RestoreExit}):\n{fx.RestoreLog}")
            let bridge = FcsBridge()

            let! result = bridge.CreateFilePlan(planArgs fx.Fsproj "Validation.fs")

            Assert.Equal("succeeded", gs result "status")

            // All three neighbours declare `module MyApp.<X>`, so the inferred convention
            // names the shared MyApp root and the module-per-file shape.
            let convention = gs result "inferredNamespaceConvention"
            output.WriteLine($"convention={convention}")
            Assert.Contains("MyApp", convention)
            Assert.Contains("module", convention)

            // The neighbour declarations are surfaced verbatim in nearbyFiles.
            let arr = result["nearbyFiles"] :?> JsonArray
            Assert.True(arr.Count > 0, "nearbyFiles should not be empty")
            let mutable sawModuleDecl = false

            for i in 0 .. arr.Count - 1 do
                match arr[i]["topNamespaceOrModule"] with
                | null -> ()
                | decl -> if (decl.GetValue<string>()).StartsWith("module MyApp.") then sawModuleDecl <- true

            Assert.True(sawModuleDecl, "a neighbour's top declaration should be reported as `module MyApp.<X>`")
        }

    [<Fact>]
    member _.``no afterFile or namespace defaults to end-of-project insertion``() : Task =
        task {
            Assert.True((fx.RestoreExit = 0), $"Fixture restore failed (exit {fx.RestoreExit}):\n{fx.RestoreLog}")
            let bridge = FcsBridge()

            let! result = bridge.CreateFilePlan(planArgs fx.Fsproj "Helpers.fs")

            Assert.Equal("succeeded", gs result "status")

            // End insertion ⇒ recommended index equals the file count, the new file follows
            // the last existing file, and there is no following sibling.
            Assert.Equal(gi result "totalCompileFiles", gi result "recommendedCompileIndex")
            Assert.EndsWith("Program.fs", gs result "insertAfter")
            Assert.True(isNull (result["insertBefore"]), "insertBefore must be JSON null for an end insertion")
        }

    [<Fact>]
    member _.``no project context returns invalid_args``() : Task =
        task {
            let bridge = FcsBridge()

            let! result =
                bridge.CreateFilePlan(
                    { fileName = "Validation.fs"
                      afterFile = None
                      namespaceOrModule = None
                      projectPath = None }
                )

            Assert.Equal("invalid_args", gs result "status")
            Assert.Contains("project", gs result "message")
        }
