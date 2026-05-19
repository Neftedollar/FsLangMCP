module FsLangMcp.ProjectHealth

open System
open System.IO
open System.Xml.Linq
open System.Text.Json.Nodes
open FsLangMcp.Types

type LspHealthSnapshot =
    { ProjectPath: string option
      WorkspaceRoot: string option
      WorkspaceReady: bool
      DiagnosticsFileCount: int }

type ProjectOptionsProbe = string -> Async<Result<string, string>>

let private xname localName = XName.Get(localName)

let private attr (name: string) (element: XElement) =
    match element.Attribute(xname name) with
    | null -> None
    | value -> Some value.Value

let private childValue (name: string) (doc: XDocument) =
    doc.Descendants(xname name)
    |> Seq.tryPick (fun element ->
        if String.IsNullOrWhiteSpace element.Value then
            None
        else
            Some(element.Value.Trim()))

let private boolProperty name doc =
    childValue name doc
    |> Option.bind (fun value ->
        match Boolean.TryParse(value) with
        | true, parsed -> Some parsed
        | false, _ -> None)

let private isGeneratedFile (path: string) =
    let fileName = Path.GetFileName(path)

    let normalized =
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)

    let separator: string = string Path.DirectorySeparatorChar

    normalized.Contains($"%s{separator}obj%s{separator}", StringComparison.OrdinalIgnoreCase)
    || fileName.EndsWith(".g.fs", StringComparison.OrdinalIgnoreCase)
    || fileName.EndsWith(".g.i.fs", StringComparison.OrdinalIgnoreCase)
    || fileName.EndsWith(".AssemblyInfo.fs", StringComparison.OrdinalIgnoreCase)
    || fileName.EndsWith(".Designer.fs", StringComparison.OrdinalIgnoreCase)

let private isFsFile (path: string) =
    let ext = Path.GetExtension(path)

    String.Equals(ext, ".fs", StringComparison.OrdinalIgnoreCase)
    || String.Equals(ext, ".fsi", StringComparison.OrdinalIgnoreCase)

let private tryReadProject (projectPath: string) =
    try
        Ok(XDocument.Load(projectPath))
    with ex ->
        Error ex.Message

let private resolveCompilePath (projectDir: string) (includePath: string) =
    if Path.IsPathRooted(includePath) then
        Path.GetFullPath(includePath)
    else
        Path.GetFullPath(Path.Combine(projectDir, includePath))

let private compileFiles (projectPath: string) (doc: XDocument) =
    let projectDir = Path.GetDirectoryName(projectPath)

    doc.Descendants(xname "Compile")
    |> Seq.choose (fun element ->
        attr "Include" element
        |> Option.map (fun includePath ->
            let path = resolveCompilePath projectDir includePath

            path, includePath, attr "Link" element))
    |> Seq.filter (fun (path, _, _) -> isFsFile path)
    |> Seq.toList

let private analyzerPackages (doc: XDocument) =
    doc.Descendants(xname "PackageReference")
    |> Seq.choose (fun element ->
        let includeValue =
            attr "Include" element |> Option.orElseWith (fun () -> attr "Update" element)

        let includeAssets = attr "IncludeAssets" element |> Option.defaultValue ""

        includeValue
        |> Option.filter (fun packageId ->
            packageId.Contains("Analyzer", StringComparison.OrdinalIgnoreCase)
            || includeAssets.Contains("analyzers", StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun packageId ->
            jobj
                [ "packageId", jstr packageId
                  "version", attr "Version" element |> Option.map jstr |> Option.defaultValue null
                  "includeAssets", jstr includeAssets
                  "privateAssets", attr "PrivateAssets" element |> Option.map jstr |> Option.defaultValue null ]
            :> JsonNode))
    |> Seq.toArray

let private sourceSummary (files: (string * string * string option) list) =
    let missing, unreadable =
        files
        |> List.fold
            (fun (missingAcc, unreadableAcc) (path, _, _) ->
                if not (File.Exists path) then
                    path :: missingAcc, unreadableAcc
                else
                    try
                        use stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                        missingAcc, unreadableAcc
                    with ex ->
                        missingAcc, $"%s{path}: %s{ex.Message}" :: unreadableAcc)
            ([], [])

    let signatureCount =
        files
        |> List.filter (fun (path, _, _) -> Path.GetExtension(path).Equals(".fsi", StringComparison.OrdinalIgnoreCase))
        |> List.length

    jobj
        [ "sourceFileCount", jint files.Length
          "signatureFileCount", jint signatureCount
          "missingFiles", JsonArray(missing |> List.rev |> List.map jstr |> List.toArray) :> JsonNode
          "unreadableFiles", JsonArray(unreadable |> List.rev |> List.map jstr |> List.toArray) :> JsonNode
          "hasLinkedFiles", jbool (files |> List.exists (fun (_, _, link) -> link.IsSome))
          "hasGeneratedFiles", jbool (files |> List.exists (fun (path, _, _) -> isGeneratedFile path)) ]

let private projectReferencesCurrentProject
    (referencedProject: string)
    (currentProjectPath: string)
    (projectDir: string)
    =
    let resolved =
        if Path.IsPathRooted(referencedProject) then
            referencedProject
        else
            Path.Combine(projectDir, referencedProject)
        |> Path.GetFullPath

    String.Equals(resolved, currentProjectPath, StringComparison.OrdinalIgnoreCase)

// ─── Test project detection and build metadata ────────────────────────────────
// Known test-framework package prefixes (case-insensitive).
// Note: "Microsoft.NET.Test.Sdk" alone does NOT make a project a test project —
// it is a transport package, not a test framework.
let private testFrameworkPackages =
    [ "xunit.v3",               "xunit"
      "xunit",                  "xunit"
      "nunit3testadapter",      "nunit"
      "nunit",                  "nunit"
      "expecto",                "expecto" ]

/// Return the canonical framework label for a PackageReference id, or None if it
/// is not a test-framework package.
let private tryMapTestFramework (packageId: string) =
    testFrameworkPackages
    |> List.tryPick (fun (prefix, label) ->
        if packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then Some label
        else None)

/// Collect distinct test-framework labels referenced by the project.
let private detectTestFrameworks (doc: XDocument) =
    doc.Descendants(xname "PackageReference")
    |> Seq.choose (fun p ->
        attr "Include" p
        |> Option.bind tryMapTestFramework)
    |> Seq.distinct
    |> Seq.toArray

let private looksLikeTestProject (doc: XDocument) =
    boolProperty "IsTestProject" doc = Some true
    || doc.Descendants(xname "PackageReference")
       |> Seq.exists (fun p ->
           attr "Include" p
           |> Option.exists (fun id ->
               id.Contains("xunit", StringComparison.OrdinalIgnoreCase)
               || id.Contains("nunit", StringComparison.OrdinalIgnoreCase)
               || id.Contains("expecto", StringComparison.OrdinalIgnoreCase)))

/// Count test-method attribute occurrences in a source file. Best-effort regex
/// scan — no dotnet test invocation. Matches both bare names and namespace-qualified
/// variants, plus the trailing "Attribute" suffix (so `[<Xunit.FactAttribute>]`
/// counts the same as `[<Fact>]`). NUnit's `[<Test>]` / `[<TestCase>]` are also
/// counted; Expecto's tests are not attribute-driven, so they will read as 0
/// for an Expecto-only project — caller should treat that as "n/a" rather than
/// "no tests" when testFrameworks contains "expecto".
let private countTestAttributesInFile (filePath: string) =
    try
        let source = File.ReadAllText(filePath)
        // `(?:[\w.]+\.)?` allows an optional qualifier (e.g. `Xunit.`)
        // `Attribute` suffix is optional. `[^\]]*` swallows any constructor args.
        let pattern =
            """\[<(?:[\w.]+\.)?(?:Fact|Theory|Test|TestCase|TestMethod)(?:Attribute)?(?:[^\]]*)?>\]"""

        System.Text.RegularExpressions.Regex.Matches(source, pattern).Count
    with _ ->
        0

/// Find the most-recently-written .dll under bin/ whose basename matches the
/// project name (case-insensitive). Returns absolute path or None.
/// Best-effort glob — projects with custom OutputPath may not be found; acceptable for v0.8.0.
let private findLatestBuildArtifact (projectDir: string) (projectName: string) =
    let separator: string = string Path.DirectorySeparatorChar
    let binSegment = $"%s{separator}bin%s{separator}"

    try
        Directory.EnumerateFiles(projectDir, "*.dll", SearchOption.AllDirectories)
        |> Seq.filter (fun p ->
            p.Contains(binSegment, StringComparison.OrdinalIgnoreCase)
            && String.Equals(
                Path.GetFileNameWithoutExtension(p),
                projectName,
                StringComparison.OrdinalIgnoreCase))
        |> Seq.sortByDescending (fun p -> File.GetLastWriteTimeUtc(p))
        |> Seq.tryHead
    with _ ->
        None

/// Build the test-discovery + last-build JSON sub-object for one project.
let private testProjectInfo
    (projectPath: string)
    (doc: XDocument)
    (compiledFiles: (string * string * string option) list)
    : JsonNode =
    let projectDir = Path.GetDirectoryName(projectPath)

    // Prefer the .fsproj's `<AssemblyName>` over the file basename — they differ
    // in plenty of real projects. Fallback chain: AssemblyName → file basename.
    let assemblyBasename =
        childValue "AssemblyName" doc
        |> Option.defaultValue (Path.GetFileNameWithoutExtension(projectPath))

    let frameworks = detectTestFrameworks doc
    let isTest     = frameworks.Length > 0

    // Count test attributes only when this is a recognised test project.
    // Best-effort: read each compile source; sum [<Fact>] + [<Theory>] occurrences.
    let testCountNode : JsonNode =
        if not isTest then
            null
        else
            let total =
                compiledFiles
                |> List.sumBy (fun (path, _, _) -> countTestAttributesInFile path)
            JsonValue.Create(total)

    let frameworkArray =
        JsonArray(frameworks |> Array.map jstr) :> JsonNode

    let latestArtifact = findLatestBuildArtifact projectDir assemblyBasename

    let lastBuildSucceeded : JsonNode =
        match latestArtifact with
        | Some _ -> JsonValue.Create(true)
        | None   -> null

    let lastBuildAt : JsonNode =
        match latestArtifact with
        | Some p -> jstr (File.GetLastWriteTimeUtc(p).ToString("o"))
        | None   -> null

    let binaryOutputPath : JsonNode =
        match latestArtifact with
        | Some p -> jstr p
        | None   -> null

    jobj
        [ "isTestProject",    JsonValue.Create(isTest) :> JsonNode
          "testFrameworks",   frameworkArray
          "testCount",        testCountNode
          "lastBuildSucceeded", lastBuildSucceeded
          "lastBuildAt",      lastBuildAt
          "binaryOutputPath", binaryOutputPath ]

let private discoverTestProjects (workspaceRoot: string) (currentProjectPath: string) =
    if not (Directory.Exists workspaceRoot) then
        [||]
    else
        let separator: string = string Path.DirectorySeparatorChar
        let binSegment = $"%s{separator}bin%s{separator}"
        let objSegment = $"%s{separator}obj%s{separator}"

        Directory.EnumerateFiles(workspaceRoot, "*.fsproj", SearchOption.AllDirectories)
        |> Seq.filter (fun path ->
            not (path.Contains(binSegment, StringComparison.OrdinalIgnoreCase))
            && not (path.Contains(objSegment, StringComparison.OrdinalIgnoreCase)))
        |> Seq.choose (fun projectPath ->
            match tryReadProject projectPath with
            | Error _ -> None
            | Ok doc ->
                let projectDir = Path.GetDirectoryName(projectPath)

                let referencesCurrent =
                    doc.Descendants(xname "ProjectReference")
                    |> Seq.exists (fun reference ->
                        attr "Include" reference
                        |> Option.exists (fun includePath ->
                            projectReferencesCurrentProject includePath currentProjectPath projectDir))

                if looksLikeTestProject doc && referencesCurrent then
                    Some(
                        jobj
                            [ "projectPath", jstr projectPath
                              "targetFramework",
                              childValue "TargetFramework" doc |> Option.map jstr |> Option.defaultValue null
                              "referencesProject", jbool true ]
                        :> JsonNode
                    )
                else
                    None)
        |> Seq.toArray

let private findAnalyzerConfigFiles (projectDir: string) =
    [ "Directory.Build.props"
      "Directory.Build.targets"
      "Directory.Packages.props"
      ".editorconfig" ]
    |> List.choose (fun fileName ->
        let path = Path.Combine(projectDir, fileName)
        if File.Exists path then Some path else None)

let private pickSingleFsproj (projects: string array) (sourcePath: string) =
    match projects with
    | [| one |] -> Ok one
    | [||] -> Error $"No .fsproj found in: %s{sourcePath}"
    | many ->
        let names = many |> Array.map Path.GetFileName |> String.concat ", "
        Error $"Multiple .fsproj files found; pass one explicitly: %s{names}"

let private resolveHealthProjectPath (input: string option) =
    match input with
    | None ->
        Error
            "projectPath is required. Either pass it (a .fsproj, .sln, .slnx, or directory) or call set_project first to establish a default."
    | Some path when System.String.IsNullOrWhiteSpace path ->
        Error
            "projectPath must not be empty. Pass a .fsproj/.sln/.slnx path or call set_project first."
    | Some inputPath ->
        let fullPath = Path.GetFullPath(inputPath)
        let ext = Path.GetExtension(fullPath)

        if File.Exists fullPath && ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase) then
            Ok fullPath
        elif File.Exists fullPath && ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase) then
            FsLangMcp.ProjectFiles.SolutionParsing.fsprojsFromSlnx fullPath |> pickSingleFsproj <| fullPath
        elif File.Exists fullPath && ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) then
            FsLangMcp.ProjectFiles.SolutionParsing.fsprojsFromSln fullPath |> pickSingleFsproj <| fullPath
        elif Directory.Exists fullPath then
            Directory.GetFiles(fullPath, "*.fsproj", SearchOption.TopDirectoryOnly)
            |> Array.map Path.GetFullPath
            |> pickSingleFsproj <| fullPath
        else
            Error "project_health expects a .fsproj, .sln, .slnx path, or a directory containing exactly one .fsproj."

let createReport
    (args: ProjectHealthArgs)
    (lspSnapshot: LspHealthSnapshot)
    (projectOptionsProbe: ProjectOptionsProbe)
    : Async<JsonNode> =
    async {
        let compileCheck = args.compileCheck |> Option.defaultValue "Skip"

        match resolveHealthProjectPath args.projectPath with
        | Error reason ->
            let blockedAxis r =
                jobj [ "status", jstr "blocked"; "reason", jstr r ] :> JsonNode

            return
                jobj
                    [ "status", jstr "ok"
                      "reportKind", jstr "project"
                      "toolingReadiness",
                      jobj
                          [ "fcs", blockedAxis reason
                            "lsp", blockedAxis reason
                            "overall", jstr "blocked" ]
                      "compileStatus", jobj [ "status", jstr "not_checked" ]
                      "project",
                      jobj
                          [ "projectPath",
                            args.projectPath
                            |> Option.map (Path.GetFullPath >> jstr)
                            |> Option.defaultValue null
                            "exists", jbool false ] ]
                :> JsonNode
        | Ok projectPath ->
            match tryReadProject projectPath with
            | Error reason ->
                let blockedAxis r =
                    jobj [ "status", jstr "blocked"; "reason", jstr r ] :> JsonNode

                let readReason = $"Project file cannot be read: %s{reason}"

                return
                    jobj
                        [ "status", jstr "ok"
                          "reportKind", jstr "project"
                          "toolingReadiness",
                          jobj
                              [ "fcs", blockedAxis readReason
                                "lsp", blockedAxis readReason
                                "overall", jstr "blocked" ]
                          "compileStatus", jobj [ "status", jstr "not_checked" ]
                          "project", jobj [ "projectPath", jstr projectPath; "exists", jbool true ] ]
                    :> JsonNode
            | Ok doc ->
                let projectDir = Path.GetDirectoryName(projectPath)

                let workspaceRoot =
                    args.workspacePath
                    |> Option.map (fun p ->
                        let full = Path.GetFullPath p
                        if File.Exists full then Path.GetDirectoryName full else full)
                    |> Option.defaultValue projectDir

                let files = compileFiles projectPath doc
                let fileSummary = sourceSummary files
                let testInfo = testProjectInfo projectPath doc files
                let hasMissingFiles = fileSummary["missingFiles"].AsArray().Count > 0
                let hasUnreadableFiles = fileSummary["unreadableFiles"].AsArray().Count > 0

                let! projectOptionsHealth =
                    async {
                        match! projectOptionsProbe projectPath with
                        | Ok source -> return jobj [ "status", jstr "available"; "source", jstr source ] :> JsonNode
                        | Error reason ->
                            return jobj [ "status", jstr "unavailable"; "reason", jstr reason ] :> JsonNode
                    }

                let fcsWarnings = ResizeArray<JsonNode>()

                if hasMissingFiles || hasUnreadableFiles then
                    fcsWarnings.Add(jstr "Some project source files are missing or unreadable.")

                match projectOptionsHealth["status"].GetValue<string>() with
                | "available" -> ()
                | _ -> fcsWarnings.Add(jstr "Project options are unavailable; semantic tools may be incomplete.")

                let fcsReadiness =
                    if hasMissingFiles || hasUnreadableFiles then
                        jobj
                            [ "status", jstr "blocked"
                              "blockers", fileSummary["missingFiles"].DeepClone()
                              "recovery", JsonArray(jstr "Restore or materialize required source files.") :> JsonNode ]
                    elif fcsWarnings.Count > 0 then
                        jobj
                            [ "status", jstr "degraded"
                              "warnings", JsonArray(fcsWarnings.ToArray()) :> JsonNode ]
                    else
                        jobj [ "status", jstr "ready"; "warnings", JsonArray() :> JsonNode ]

                let lspReadiness =
                    if lspSnapshot.WorkspaceReady then
                        jobj [ "status", jstr "ready" ]
                    else
                        jobj
                            [ "status", jstr "not_ready"
                              "reason",
                              jstr
                                  "workspace not initialized; auto-warmed on first textDocument_*/workspace_* call" ]

                let fcsStatus = fcsReadiness["status"].GetValue<string>()
                let lspStatus = lspReadiness["status"].GetValue<string>()

                let overallStatus =
                    match fcsStatus, lspStatus with
                    | "ready",   "ready"     -> "ready"
                    | "ready",   "not_ready" -> "fcs_only"
                    | "blocked", _           -> "blocked"
                    | _                      -> "degraded"

                let readiness =
                    jobj
                        [ "fcs", fcsReadiness :> JsonNode
                          "lsp", lspReadiness :> JsonNode
                          "overall", jstr overallStatus ]

                let analyzers = analyzerPackages doc
                let analyzerConfigFiles = findAnalyzerConfigFiles projectDir

                let analyzerHealth =
                    if analyzers.Length = 0 && analyzerConfigFiles.IsEmpty then
                        jobj
                            [ "status", jstr "no_analyzers_configured"
                              "analyzers", JsonArray() :> JsonNode ]
                    else
                        jobj
                            [ "status", jstr "analyzers_configured"
                              "analyzers", JsonArray(analyzers) :> JsonNode
                              "configurationFiles",
                              JsonArray(analyzerConfigFiles |> List.map jstr |> List.toArray) :> JsonNode ]

                let testProjects = discoverTestProjects workspaceRoot projectPath

                let testHealth =
                    if testProjects.Length = 0 then
                        jobj [ "status", jstr "no_test_projects_found"; "projects", JsonArray() :> JsonNode ]
                    else
                        jobj
                            [ "status", jstr "test_projects_found"
                              "projects", JsonArray(testProjects) :> JsonNode ]

                return
                    jobj
                        [ "status", jstr "ok"
                          "reportKind", jstr "project"
                          "toolingReadiness", readiness :> JsonNode
                          "compileStatus",
                          jobj
                              [ "status",
                                jstr (
                                    if String.Equals(compileCheck, "UseCached", StringComparison.OrdinalIgnoreCase) then
                                        "cached_diagnostics_not_checked_in_v0"
                                    else
                                        "not_checked"
                                ) ]
                          "project",
                          jobj
                              [ "projectPath", jstr projectPath
                                "projectDirectory", jstr projectDir
                                "projectName", jstr (Path.GetFileNameWithoutExtension(projectPath))
                                "sdk", attr "Sdk" doc.Root |> Option.map jstr |> Option.defaultValue null
                                "outputType", childValue "OutputType" doc |> Option.map jstr |> Option.defaultValue null
                                "targetFramework",
                                childValue "TargetFramework" doc |> Option.map jstr |> Option.defaultValue null
                                "targetFrameworks",
                                childValue "TargetFrameworks" doc |> Option.map jstr |> Option.defaultValue null
                                "isTestProject",    testInfo["isTestProject"].DeepClone()
                                "testFrameworks",   testInfo["testFrameworks"].DeepClone()
                                "testCount",        (let n = testInfo["testCount"] in if isNull n then null else n.DeepClone())
                                "lastBuildSucceeded", (let n = testInfo["lastBuildSucceeded"] in if isNull n then null else n.DeepClone())
                                "lastBuildAt",      (let n = testInfo["lastBuildAt"] in if isNull n then null else n.DeepClone())
                                "binaryOutputPath", (let n = testInfo["binaryOutputPath"] in if isNull n then null else n.DeepClone()) ]
                          "workspace",
                          jobj
                              [ "workspaceRoot", jstr workspaceRoot
                                "lspWorkspaceRoot",
                                lspSnapshot.WorkspaceRoot |> Option.map jstr |> Option.defaultValue null
                                "lspProjectPath", lspSnapshot.ProjectPath |> Option.map jstr |> Option.defaultValue null
                                "lspWorkspaceReady", jbool lspSnapshot.WorkspaceReady
                                "diagnosticsFileCount", jint lspSnapshot.DiagnosticsFileCount ]
                          "projectOptions", projectOptionsHealth
                          "analyzers", analyzerHealth :> JsonNode
                          "tests", testHealth :> JsonNode
                          "files", fileSummary :> JsonNode ]
                    :> JsonNode
    }
