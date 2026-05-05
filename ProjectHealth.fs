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

let private looksLikeTestProject (doc: XDocument) =
    boolProperty "IsTestProject" doc = Some true
    || doc.Descendants(xname "PackageReference")
       |> Seq.exists (fun p ->
           attr "Include" p
           |> Option.exists (fun id ->
               id.Contains("xunit", StringComparison.OrdinalIgnoreCase)
               || id.Contains("nunit", StringComparison.OrdinalIgnoreCase)
               || id.Contains("expecto", StringComparison.OrdinalIgnoreCase)))

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

let private resolveHealthProjectPath (inputPath: string) =
    let fullPath = Path.GetFullPath(inputPath)

    if
        File.Exists fullPath
        && Path.GetExtension(fullPath).Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
    then
        Ok fullPath
    elif Directory.Exists fullPath then
        let projects =
            Directory.GetFiles(fullPath, "*.fsproj", SearchOption.TopDirectoryOnly)
            |> Array.map Path.GetFullPath

        match projects with
        | [| one |] -> Ok one
        | [||] -> Error $"No .fsproj found in directory: %s{fullPath}"
        | many ->
            let names = many |> Array.map Path.GetFileName |> String.concat ", "
            Error $"Multiple .fsproj files found in directory; pass one explicitly: %s{names}"
    else
        Error "project_health v1 expects an explicit .fsproj path or a directory containing exactly one .fsproj."

let createReport
    (args: ProjectHealthArgs)
    (lspSnapshot: LspHealthSnapshot)
    (projectOptionsProbe: ProjectOptionsProbe)
    : Async<JsonNode> =
    async {
        let compileCheck = args.compileCheck |> Option.defaultValue "Skip"

        match resolveHealthProjectPath args.projectPath with
        | Error reason ->
            return
                jobj
                    [ "status", jstr "ok"
                      "reportKind", jstr "project"
                      "toolingReadiness",
                      jobj
                          [ "status", jstr "blocked"
                            "blockers", JsonArray(jstr reason) :> JsonNode
                            "recovery", JsonArray(jstr "Pass an explicit .fsproj path to project_health.") :> JsonNode ]
                      "compileStatus", jobj [ "status", jstr "not_checked" ]
                      "project",
                      jobj
                          [ "projectPath", jstr (Path.GetFullPath(args.projectPath))
                            "exists", jbool false ] ]
                :> JsonNode
        | Ok projectPath ->
            match tryReadProject projectPath with
            | Error reason ->
                return
                    jobj
                        [ "status", jstr "ok"
                          "reportKind", jstr "project"
                          "toolingReadiness",
                          jobj
                              [ "status", jstr "blocked"
                                "blockers", JsonArray(jstr $"Project file cannot be read: %s{reason}") :> JsonNode
                                "recovery", JsonArray(jstr "Fix or restore the project file.") :> JsonNode ]
                          "compileStatus", jobj [ "status", jstr "not_checked" ]
                          "project", jobj [ "projectPath", jstr projectPath; "exists", jbool true ] ]
                    :> JsonNode
            | Ok doc ->
                let projectDir = Path.GetDirectoryName(projectPath)

                let workspaceRoot =
                    args.workspacePath
                    |> Option.map Path.GetFullPath
                    |> Option.defaultValue projectDir

                let files = compileFiles projectPath doc
                let fileSummary = sourceSummary files
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
                    | "ready", "ready" -> "ready"
                    | ("ready" | "degraded"), _ when fcsStatus <> "blocked" -> "fcs_only"
                    | _ -> "degraded"

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
                                childValue "TargetFrameworks" doc |> Option.map jstr |> Option.defaultValue null ]
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
