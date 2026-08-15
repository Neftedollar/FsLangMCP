module FsLangMcp.ProjectHealth

open System
open System.IO
open System.Xml.Linq
open System.Text.Json.Nodes
open FsLangMcp.Types
open FsLangMcp.ProjectFiles

type LspHealthSnapshot =
    { ProjectPath: string option
      WorkspaceRoot: string option
      LoadedProjects: string array
      SessionLive: bool
      WorkspaceReady: bool
      DiagnosticsFileCount: int }

type ProjectOptionsProbe = string -> Async<Result<ProjectOptionsInfo, string>>

let private xname localName = XName.Get(localName)

let private attr (name: string) (element: XElement) =
    match element.Attribute(xname name) with
    | null -> None
    | value -> Some value.Value

/// Read ordinary MSBuild item metadata. NuGet accepts metadata both as an XML
/// attribute and as a child element; when both are present, the attribute is
/// the value MSBuild exposes and therefore wins here too.
let private itemMetadata (name: string) (element: XElement) =
    element.Attributes()
    |> Seq.tryPick (fun attribute ->
        if attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase) then
            let value = attribute.Value.Trim()
            if String.IsNullOrWhiteSpace value then None else Some value
        else
            None)
    |> Option.orElseWith (fun () ->
        element.Elements()
        |> Seq.tryPick (fun child ->
            if child.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase) then
                let value = child.Value.Trim()
                if String.IsNullOrWhiteSpace value then None else Some value
            else
                None))

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

/// Structured analyzer PackageReference info. Shared by project_health's analyzer-health
/// block and fcs_analyzer_diagnostics so both agree on what "an analyzer package" is:
/// the package id contains "Analyzer", OR its IncludeAssets contains "analyzers".
type AnalyzerPackageInfo =
    { PackageId: string
      Version: string option
      IncludeAssets: string
      PrivateAssets: string option }

/// Analyzer configuration detected from one .fsproj: the analyzer PackageReferences plus
/// any analyzer config files alongside it. `Configured` is the SAME signal project_health
/// uses to report `analyzers_configured`.
type AnalyzerConfig =
    { Configured: bool
      Packages: AnalyzerPackageInfo list
      ConfigFiles: string list }

let private tryAnalyzerPackageInfo (element: XElement) : AnalyzerPackageInfo option =
    let includeValue =
        attr "Include" element |> Option.orElseWith (fun () -> attr "Update" element)

    let includeAssets = itemMetadata "IncludeAssets" element |> Option.defaultValue ""

    includeValue
    |> Option.filter (fun packageId ->
        packageId.Contains("Analyzer", StringComparison.OrdinalIgnoreCase)
        || includeAssets.Contains("analyzers", StringComparison.OrdinalIgnoreCase))
    |> Option.map (fun packageId ->
        { PackageId = packageId
          Version = itemMetadata "Version" element
          IncludeAssets = includeAssets
          PrivateAssets = itemMetadata "PrivateAssets" element })

let private analyzerPackageInfos (doc: XDocument) : AnalyzerPackageInfo list =
    doc.Descendants(xname "PackageReference")
    |> Seq.choose tryAnalyzerPackageInfo
    |> Seq.toList

/// Render one analyzer package as the JSON object project_health and
/// fcs_analyzer_diagnostics both emit: { packageId, version, includeAssets, privateAssets }.
let analyzerPackageInfoToJson (p: AnalyzerPackageInfo) : JsonNode =
    jobj
        [ "packageId", jstr p.PackageId
          "version", p.Version |> Option.map jstr |> Option.defaultValue null
          "includeAssets", jstr p.IncludeAssets
          "privateAssets", p.PrivateAssets |> Option.map jstr |> Option.defaultValue null ]
    :> JsonNode

let private analyzerPackages (doc: XDocument) =
    analyzerPackageInfos doc |> List.map analyzerPackageInfoToJson |> List.toArray

let private sourceSummary (files: ProjectFile list) =
    let missing, unreadable =
        files
        |> List.fold
            (fun (missingAcc, unreadableAcc) file ->
                if not (File.Exists file.Path) then
                    file.Path :: missingAcc, unreadableAcc
                else
                    try
                        use stream = File.Open(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                        missingAcc, unreadableAcc
                    with ex ->
                        missingAcc, $"%s{file.Path}: %s{ex.Message}" :: unreadableAcc)
            ([], [])

    let signatureCount =
        files |> List.filter _.IsSignature |> List.length

    jobj
        [ "sourceFileCount", jint files.Length
          "signatureFileCount", jint signatureCount
          "missingFiles", JsonArray(missing |> List.rev |> List.map jstr |> List.toArray) :> JsonNode
          "unreadableFiles", JsonArray(unreadable |> List.rev |> List.map jstr |> List.toArray) :> JsonNode
          "hasLinkedFiles", jbool (files |> List.exists (fun file -> file.Link.IsSome))
          "hasGeneratedFiles", jbool (files |> List.exists (fun file -> isGeneratedFile file.Path))
          "evaluationSource", jstr "msbuild-evaluated" ]

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

/// Public test-project predicate reused by fcs_tests_for_symbol (#60). A project counts
/// as a test project when <IsTestProject>true</IsTestProject> is set OR it references a
/// known test framework (xunit / nunit / expecto) — the SAME signal project_health's test
/// discovery uses (looksLikeTestProject). Missing / unreadable .fsproj → false.
let isTestProjectFile (fsprojPath: string) : bool =
    match tryReadProject fsprojPath with
    | Error _ -> false
    | Ok doc -> looksLikeTestProject doc

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
            """\[<(?:[\w.]+\.)?(?:Fact|Theory|Test|TestCase|TestMethod)(?:Attribute)?\b(?:[^\]]*)?>\]"""

        System.Text.RegularExpressions.Regex.Matches(source, pattern).Count
    with _ ->
        0

type private BuildArtifactInfo =
    { Path: string
      Configuration: string option }

/// Find the most-recently-written .dll under bin/ whose basename matches the
/// project name (case-insensitive), together with the conventional first path
/// segment under bin/ (normally Debug or Release) as its configuration.
/// Searches only inside projectDir/bin/ to avoid OOM walks on monorepos.
let private findLatestBuildArtifact (projectDir: string) (projectName: string) =
    let binDir = Path.Combine(projectDir, "bin")

    if not (Directory.Exists binDir) then
        None
    else
        try
            Directory.EnumerateFiles(binDir, "*.dll", SearchOption.AllDirectories)
            |> Seq.filter (fun p ->
                String.Equals(
                    Path.GetFileNameWithoutExtension(p),
                    projectName,
                    StringComparison.OrdinalIgnoreCase))
            |> Seq.sortByDescending File.GetLastWriteTimeUtc
            |> Seq.tryHead
            |> Option.map (fun path ->
                let relativePath = Path.GetRelativePath(binDir, path)

                let segments =
                    relativePath.Split(
                        [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |],
                        StringSplitOptions.RemoveEmptyEntries
                    )

                { Path = path
                  // A direct bin/Foo.dll does not encode a configuration. Keep
                  // that unknown instead of guessing from another project property.
                  Configuration = if segments.Length > 1 then Some segments[0] else None })
        with _ ->
            None

/// Build the test-discovery + last-build JSON sub-object for one project.
let private evaluatedTestFrameworks (snapshot: EvaluatedProjectSnapshot) =
    snapshot.PackageReferences
    |> List.choose (fun package -> tryMapTestFramework package.PackageId)
    |> List.distinct
    |> List.toArray

let private testProjectInfo (snapshot: EvaluatedProjectSnapshot) : JsonNode =
    let projectDir = snapshot.ProjectDirectory
    let assemblyBasename = snapshot.AssemblyName
    let frameworks = evaluatedTestFrameworks snapshot
    let isTest = snapshot.IsTestProject || frameworks.Length > 0

    // Count test attributes only when this is a recognised test project.
    // Best-effort: read each compile source; sum [<Fact>] + [<Theory>] occurrences.
    let testCountNode : JsonNode =
        if not isTest then
            null
        else
            let total =
                snapshot.Files |> List.sumBy (fun file -> countTestAttributesInFile file.Path)
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
        | Some artifact -> jstr (File.GetLastWriteTimeUtc(artifact.Path).ToString("o"))
        | None   -> null

    let binaryOutputPath : JsonNode =
        match latestArtifact with
        | Some artifact -> jstr artifact.Path
        | None   -> null

    let configuration : JsonNode =
        latestArtifact
        |> Option.bind _.Configuration
        |> Option.map jstr
        |> Option.defaultValue null

    jobj
        [ "isTestProject",    JsonValue.Create(isTest) :> JsonNode
          "testFrameworks",   frameworkArray
          "testCount",        testCountNode
          "lastBuildSucceeded", lastBuildSucceeded
          "lastBuildAt",      lastBuildAt
          "binaryOutputPath", binaryOutputPath
          "configuration", configuration ]

let private discoverTestProjects
    (workspaceRoot: string)
    (currentProjectPath: string)
    (evaluatedProjectProvider: EvaluatedProjectSnapshotProvider)
    =
    async {
        if not (Directory.Exists workspaceRoot) then
            return [||]
        else
            let separator: string = string Path.DirectorySeparatorChar
            let binSegment = $"%s{separator}bin%s{separator}"
            let objSegment = $"%s{separator}obj%s{separator}"

            let projectPaths =
                Directory.EnumerateFiles(workspaceRoot, "*.fsproj", SearchOption.AllDirectories)
                |> Seq.filter (fun path ->
                    not (path.Contains(binSegment, StringComparison.OrdinalIgnoreCase))
                    && not (path.Contains(objSegment, StringComparison.OrdinalIgnoreCase)))
                |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
                |> Seq.toArray

            // ProjInfo/MSBuild evaluation is process-global and can race while distinct
            // project graphs restore/read shared obj state. Keep discovery deterministic
            // and serialize loads; the outer project_health handler already has its own
            // request gate, but that does not protect sibling loads started in parallel.
            let evaluated = ResizeArray<Result<EvaluatedProjectSnapshot, string>>()

            for projectPath in projectPaths do
                let! result = evaluatedProjectProvider projectPath
                evaluated.Add result

            return
                Array.zip projectPaths (evaluated.ToArray())
                |> Array.choose (fun (projectPath, result) ->
                    match result with
                    | Error _ -> None
                    | Ok snapshot ->
                        let referencesCurrent =
                            snapshot.ProjectReferences
                            |> List.exists (fun reference ->
                                String.Equals(
                                    Path.GetFullPath reference.ProjectPath,
                                    Path.GetFullPath currentProjectPath,
                                    StringComparison.OrdinalIgnoreCase
                                ))

                        let isTest =
                            snapshot.IsTestProject || (evaluatedTestFrameworks snapshot).Length > 0

                        if isTest && referencesCurrent then
                            Some(
                                jobj
                                    [ "projectPath", jstr projectPath
                                      "targetFramework",
                                      snapshot.TargetFramework |> Option.map jstr |> Option.defaultValue null
                                      "referencesProject", jbool true
                                      "evaluationSource", jstr snapshot.EvaluationSource ]
                                :> JsonNode
                            )
                        else
                            None)
    }

/// Walk up from `startDir` to the filesystem root, returning the first existing path among
/// the relative candidates. Mirrors MSBuild's nearest-Directory.Build.* lookup and the
/// conventional `.config/dotnet-tools.json` discovery.
let private findNearestUpwards (startDir: string) (relativeCandidates: string list) : string option =
    let rec loop (dir: string) =
        if String.IsNullOrEmpty dir then
            None
        else
            match
                relativeCandidates
                |> List.tryPick (fun rel ->
                    let p = Path.Combine(dir, rel)
                    if File.Exists p then Some(Path.GetFullPath p) else None)
            with
            | Some hit -> Some hit
            | None ->
                let parent = Path.GetDirectoryName dir

                if String.IsNullOrEmpty parent || String.Equals(parent, dir, StringComparison.Ordinal) then
                    None
                else
                    loop parent

    loop (Path.GetFullPath startDir)

let private findAnalyzerConfigFiles (projectDir: string) =
    [ "Directory.Build.props"
      "Directory.Build.targets"
      "Directory.Packages.props"
      ".editorconfig" ]
    // Each MSBuild/config filename has its own nearest-file search. A nearer
    // Directory.Build.props must not accidentally hide an independently located
    // Directory.Build.targets (or vice versa).
    |> List.choose (fun fileName -> findNearestUpwards projectDir [ fileName ])

/// Analyzer PackageReferences can be centralized in an MSBuild import
/// (Directory.Build.props/.targets, or Directory.Packages.props via GlobalPackageReference)
/// rather than the .fsproj — analyzerPackageInfos only sees the .fsproj, so scan those XML
/// imports too (Codex review). .editorconfig is not XML → skipped (and is not an analyzer signal).
let private analyzerPackagesFromConfigFile (path: string) : AnalyzerPackageInfo list =
    let isMsbuildXml =
        path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)

    if not isMsbuildXml then
        []
    else
        try
            let doc = XDocument.Load(path)

            [ "PackageReference"; "GlobalPackageReference" ]
            |> List.collect (fun elemName ->
                doc.Descendants(xname elemName)
                |> Seq.choose tryAnalyzerPackageInfo
                |> Seq.toList)
        with _ ->
            []

let private analyzerConfigOfDoc (projectDir: string) (doc: XDocument) : AnalyzerConfig =
    let configFiles = findAnalyzerConfigFiles projectDir

    // `Configured` must be a REAL analyzer signal — an analyzer PackageReference (id contains
    // "Analyzer" or IncludeAssets contains "analyzers"), whether declared in the .fsproj OR
    // centralized in an MSBuild import (Directory.Build.props/.targets, Directory.Packages.props).
    // The mere EXISTENCE of a config file on disk is NOT evidence: nearly every project has an
    // .editorconfig, which previously flipped `Configured` true unconditionally and made
    // fcs_analyzer_diagnostics shell out to the analyzer CLI on plain, analyzer-free projects.
    let packages =
        analyzerPackageInfos doc @ (configFiles |> List.collect analyzerPackagesFromConfigFile)
        |> List.distinctBy (fun p -> p.PackageId)

    { Configured = not packages.IsEmpty
      Packages = packages
      ConfigFiles = configFiles }

let private analyzerConfigOfSnapshot (snapshot: EvaluatedProjectSnapshot) : AnalyzerConfig =
    let packages =
        snapshot.PackageReferences
        |> List.choose (fun reference ->
            let includeAssets = reference.IncludeAssets |> Option.defaultValue ""

            if
                reference.PackageId.Contains("Analyzer", StringComparison.OrdinalIgnoreCase)
                || includeAssets.Contains("analyzers", StringComparison.OrdinalIgnoreCase)
            then
                Some
                    { PackageId = reference.PackageId
                      Version = reference.Version
                      IncludeAssets = includeAssets
                      PrivateAssets = reference.PrivateAssets }
            else
                None)
        |> List.distinctBy (fun package -> package.PackageId.ToUpperInvariant())

    let importedConfigFiles =
        snapshot.ImportedProjects
        |> List.filter (fun path ->
            let name = Path.GetFileName path

            name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase))

    let configFiles =
        importedConfigFiles
        @ (findNearestUpwards snapshot.ProjectDirectory [ ".editorconfig" ] |> Option.toList)
        |> List.distinct

    { Configured = not packages.IsEmpty
      Packages = packages
      ConfigFiles = configFiles }

/// Detect the analyzer configuration of one .fsproj the SAME way project_health does:
/// `Configured` is driven ONLY by real analyzer PackageReferences — config files
/// (Directory.Build.*, Directory.Packages.props, .editorconfig) are collected as
/// informational evidence but never flip `Configured` on their own (nearly every project
/// has an .editorconfig, which is not an analyzer signal). Reused by fcs_analyzer_diagnostics
/// so the two tools never disagree on whether analyzers are configured. Missing/unreadable
/// .fsproj → Error.
let detectAnalyzerConfig (projectPath: string) : Result<AnalyzerConfig, string> =
    match tryReadProject projectPath with
    | Error reason -> Error reason
    | Ok doc -> Ok(analyzerConfigOfDoc (Path.GetDirectoryName projectPath) doc)

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

/// A solution (.slnx/.sln) or directory with MORE THAN ONE .fsproj can't be reduced to a
/// single project to report on. Rather than block (#100 — `overall:"blocked"` read like an
/// error), project_health emits a lightweight solution summary from this list. Returns None
/// for single-project / non-solution inputs, which the normal single-project path handles.
let private listSolutionProjects (input: string option) : (string * string array) option =
    match input with
    | Some inputPath when not (System.String.IsNullOrWhiteSpace inputPath) ->
        let fullPath = Path.GetFullPath inputPath
        let ext = Path.GetExtension fullPath

        let projects =
            if File.Exists fullPath && ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase) then
                FsLangMcp.ProjectFiles.SolutionParsing.fsprojsFromSlnx fullPath
            elif File.Exists fullPath && ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) then
                FsLangMcp.ProjectFiles.SolutionParsing.fsprojsFromSln fullPath
            elif Directory.Exists fullPath then
                Directory.GetFiles(fullPath, "*.fsproj", SearchOption.TopDirectoryOnly)
                |> Array.map Path.GetFullPath
            else
                [||]

        if projects.Length > 1 then Some(fullPath, projects) else None
    | _ -> None

let internal createReport
    (args: ProjectHealthArgs)
    (lspSnapshot: LspHealthSnapshot)
    (evaluatedProjectProvider: EvaluatedProjectSnapshotProvider)
    : Async<JsonNode> =
    async {
        let compileCheck = args.compileCheck |> Option.defaultValue "Skip"

        match listSolutionProjects args.projectPath with
        | Some(source, projects) ->
            // Solution mode: don't block — summarize the member projects and direct the
            // caller to pass one .fsproj for full FCS/LSP readiness. (#100)
            let projectNodes =
                projects
                |> Array.map (fun p ->
                    jobj
                        [ "name", jstr (Path.GetFileNameWithoutExtension p)
                          "fsproj", jstr p
                          "exists", jbool (File.Exists p) ]
                    :> JsonNode)

            // Solution mode cannot run a compile check — there's no single project to compile.
            // Be HONEST about a requested (non-"Skip") compileCheck rather than silently
            // reporting "not_checked" as if the request were a no-op: an agent that keys off
            // compileStatus.status should be able to tell the check never ran, and be pointed
            // at the per-project path that can actually run it.
            let compileStatus =
                if String.Equals(compileCheck, "Skip", StringComparison.OrdinalIgnoreCase) then
                    jobj [ "status", jstr "not_checked" ] :> JsonNode
                else
                    jobj
                        [ "status", jstr "not_supported_in_solution_mode"
                          "reason",
                          jstr
                              $"compileCheck=\"%s{compileCheck}\" was requested, but project_health cannot run a compile check across a whole solution — compilation is verified per .fsproj."
                          "hint",
                          jstr
                              "Re-run project_health with an explicit .fsproj path (see solution.projects below) to get a compile verdict." ]
                    :> JsonNode

            return
                jobj
                    [ "status", jstr "ok"
                      "reportKind", jstr "solution"
                      "toolingReadiness", jobj [ "overall", jstr "solution" ]
                      "compileStatus", compileStatus
                      "solution",
                      jobj
                          [ "source", jstr source
                            "projectCount", jint projects.Length
                            "projects", JsonArray projectNodes :> JsonNode ]
                      "hint",
                      jstr
                          "project_health reports one project at a time — pass a specific .fsproj for full FCS/LSP readiness. (set_project + find/check already operate solution-wide.)" ]
                :> JsonNode
        | None ->

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
            let! evaluatedResult = evaluatedProjectProvider projectPath

            match evaluatedResult with
            | Error reason ->
                let blockedAxis r =
                    jobj [ "status", jstr "blocked"; "reason", jstr r ] :> JsonNode

                let evaluationReason = $"Project cannot be evaluated: %s{reason}"

                return
                    jobj
                        [ "status", jstr "ok"
                          "reportKind", jstr "project"
                          "toolingReadiness",
                          jobj
                              [ "fcs", blockedAxis evaluationReason
                                "lsp", blockedAxis evaluationReason
                                "overall", jstr "blocked" ]
                          "compileStatus", jobj [ "status", jstr "not_checked" ]
                          "project", jobj [ "projectPath", jstr projectPath; "exists", jbool true ]
                          "evaluation",
                          jobj
                              [ "status", jstr "unavailable"
                                "source", jstr "ionide-proj-info"
                                "reason", jstr reason ] ]
                    :> JsonNode
            | Ok evaluatedProject ->
                let projectDir = evaluatedProject.ProjectDirectory

                let normalizeWorkspaceRoot (path: string) =
                    let fullPath = Path.GetFullPath path
                    if File.Exists fullPath then Path.GetDirectoryName fullPath else fullPath

                let containsProject (workspaceRoot: string) =
                    let relative = Path.GetRelativePath(workspaceRoot, projectPath)
                    let parentPrefix = $"..%c{Path.DirectorySeparatorChar}"

                    not (Path.IsPathRooted relative)
                    && not (String.Equals(relative, "..", StringComparison.Ordinal))
                    && not (relative.StartsWith(parentPrefix, StringComparison.Ordinal))

                let workspaceRoot =
                    match args.workspacePath with
                    | Some path -> normalizeWorkspaceRoot path
                    | None ->
                        // After set_project on a solution, the active project may live
                        // under src/ while reverse-reference tests live under tests/.
                        // Prefer the live workspace root when it actually contains the
                        // inspected project; explicit out-of-workspace inspections still
                        // fall back to their own project directory.
                        lspSnapshot.WorkspaceRoot
                        |> Option.map normalizeWorkspaceRoot
                        |> Option.filter (fun root -> Directory.Exists root && containsProject root)
                        |> Option.defaultValue projectDir

                let files = evaluatedProject.Files
                let fileSummary = sourceSummary files
                let testInfo = testProjectInfo evaluatedProject
                let hasMissingFiles = fileSummary["missingFiles"].AsArray().Count > 0
                let hasUnreadableFiles = fileSummary["unreadableFiles"].AsArray().Count > 0

                let referenceFraction =
                    ReferenceResolution.fraction
                        evaluatedProject.ReferencesExisting
                        evaluatedProject.ReferencesTotal

                let restoreUnresolved =
                    not evaluatedProject.RestoreSucceeded
                    || ReferenceResolution.looksUnrestored
                        evaluatedProject.ReferencesExisting
                        evaluatedProject.ReferencesTotal

                let projectOptionsFields =
                    [ "status", jstr "available"
                      "source", jstr evaluatedProject.EvaluationSource
                      "evaluationStatus", jstr "evaluated"
                      "restoreStatus", jstr (if restoreUnresolved then "unrestored" else "restored")
                      "referencesResolved", JsonValue.Create(Math.Round(referenceFraction, 3)) :> JsonNode
                      "referencesExisting", jint evaluatedProject.ReferencesExisting
                      "referencesTotal", jint evaluatedProject.ReferencesTotal ]

                let projectOptionsFields =
                    if restoreUnresolved then
                        projectOptionsFields
                        @ [ "warning",
                            jstr
                                "External references unresolved — run dotnet restore (then build). FCS semantic tools will fail with 'FSharp.Core.dll not found' until restored." ]
                    else
                        projectOptionsFields

                let projectOptionsHealth = jobj projectOptionsFields :> JsonNode

                let fcsWarnings = ResizeArray<JsonNode>()

                if hasMissingFiles || hasUnreadableFiles then
                    fcsWarnings.Add(jstr "Some project source files are missing or unreadable.")

                if restoreUnresolved then
                    fcsWarnings.Add(
                        jstr
                            "External references unresolved — run dotnet restore (FCS semantic tools fail with 'FSharp.Core.dll not found' until the project is restored/built)."
                    )

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

                let pathsEqual left right =
                    try
                        String.Equals(
                            Path.GetFullPath left,
                            Path.GetFullPath right,
                            if OperatingSystem.IsWindows() then
                                StringComparison.OrdinalIgnoreCase
                            else
                                StringComparison.Ordinal
                        )
                    with _ ->
                        false

                let lspContextMatched =
                    (lspSnapshot.ProjectPath |> Option.exists (pathsEqual projectPath))
                    || (lspSnapshot.LoadedProjects |> Array.exists (pathsEqual projectPath))

                let lspReadiness =
                    if lspSnapshot.SessionLive && lspSnapshot.WorkspaceReady && lspContextMatched then
                        jobj [ "status", jstr "ready"; "contextMatched", jbool true ]
                    else
                        let reason =
                            if not lspSnapshot.SessionLive then
                                "no live FSAC session is active for this project"
                            elif not lspSnapshot.WorkspaceReady then
                                "workspace not initialized; auto-warmed on first textDocument_*/workspace_* call"
                            else
                                "the live FSAC workspace does not contain the inspected project; call set_project for this project or its solution"

                        jobj
                            [ "status", jstr "not_ready"
                              "contextMatched", jbool lspContextMatched
                              "reason", jstr reason ]

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

                // Analyzer references are taken from the same evaluated item set as
                // inspection, so imported/conditional packages cannot disagree.
                let analyzerCfg = analyzerConfigOfSnapshot evaluatedProject

                let analyzers =
                    analyzerCfg.Packages |> List.map analyzerPackageInfoToJson |> List.toArray

                let analyzerConfigFiles = analyzerCfg.ConfigFiles

                let analyzerHealth =
                    if not analyzerCfg.Configured then
                        jobj
                            [ "status", jstr "no_analyzers_configured"
                              "analyzers", JsonArray() :> JsonNode
                              "configurationFiles",
                              JsonArray(analyzerConfigFiles |> List.map jstr |> List.toArray) :> JsonNode ]
                    else
                        jobj
                            [ "status", jstr "analyzers_configured"
                              "analyzers", JsonArray(analyzers) :> JsonNode
                              "configurationFiles",
                              JsonArray(analyzerConfigFiles |> List.map jstr |> List.toArray) :> JsonNode ]

                let! testProjects =
                    discoverTestProjects workspaceRoot projectPath evaluatedProjectProvider

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
                          "evaluation",
                          jobj
                              [ "status", jstr "evaluated"
                                "source", jstr evaluatedProject.EvaluationSource
                                "projectPath", jstr evaluatedProject.ProjectPath
                                "targetFramework",
                                evaluatedProject.TargetFramework |> Option.map jstr |> Option.defaultValue null
                                "restoreSucceeded", jbool evaluatedProject.RestoreSucceeded
                                "importCount", jint evaluatedProject.ImportedProjects.Length
                                "imports",
                                JsonArray(evaluatedProject.ImportedProjects |> List.map jstr |> List.toArray)
                                :> JsonNode ]
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
                              [ "projectPath", jstr evaluatedProject.ProjectPath
                                "projectDirectory", jstr projectDir
                                "projectName", jstr evaluatedProject.ProjectName
                                "sdk", evaluatedProject.Sdk |> Option.map jstr |> Option.defaultValue null
                                "outputType",
                                evaluatedProject.OutputType |> Option.map jstr |> Option.defaultValue null
                                "targetFramework",
                                evaluatedProject.TargetFramework |> Option.map jstr |> Option.defaultValue null
                                "targetFrameworks",
                                (match evaluatedProject.TargetFrameworks with
                                 | [] -> null
                                 | frameworks -> jstr (String.concat ";" frameworks))
                                "evaluationSource", jstr evaluatedProject.EvaluationSource
                                "isTestProject",    testInfo["isTestProject"].DeepClone()
                                "testFrameworks",   testInfo["testFrameworks"].DeepClone()
                                "testCount",        (let n = testInfo["testCount"] in if isNull n then null else n.DeepClone())
                                "lastBuildSucceeded", (let n = testInfo["lastBuildSucceeded"] in if isNull n then null else n.DeepClone())
                                "lastBuildAt",      (let n = testInfo["lastBuildAt"] in if isNull n then null else n.DeepClone())
                                "binaryOutputPath", (let n = testInfo["binaryOutputPath"] in if isNull n then null else n.DeepClone())
                                "configuration", (let n = testInfo["configuration"] in if isNull n then null else n.DeepClone()) ]
                          "workspace",
                          jobj
                              [ "workspaceRoot", jstr workspaceRoot
                                "lspWorkspaceRoot",
                                lspSnapshot.WorkspaceRoot |> Option.map jstr |> Option.defaultValue null
                                "lspProjectPath", lspSnapshot.ProjectPath |> Option.map jstr |> Option.defaultValue null
                                "lspLoadedProjects",
                                JsonArray(lspSnapshot.LoadedProjects |> Array.map jstr) :> JsonNode
                                "lspSessionLive", jbool lspSnapshot.SessionLive
                                "lspContextMatched", jbool lspContextMatched
                                "lspWorkspaceReady", jbool lspSnapshot.WorkspaceReady
                                "diagnosticsFileCount", jint lspSnapshot.DiagnosticsFileCount ]
                          "projectOptions", projectOptionsHealth
                          "analyzers", analyzerHealth :> JsonNode
                          "tests", testHealth :> JsonNode
                          "files", fileSummary :> JsonNode ]
                    :> JsonNode
    }

// ─── #75: fcs_analyzer_setup_preview ──────────────────────────────────────────────
// Read-only planner: diff a project's CURRENT analyzer wiring against the required set
// (analyzer package refs + GeneratePathProperty, FSharp.Analyzers.Build, the
// FSharpAnalyzersOtherFlags property, and a local fsharp-analyzers tool manifest) and emit
// the exact XML/JSON snippets to add. Writes nothing. Reuses the same analyzerPackages
// detection project_health's `analyzers` axis uses for the "current" view. This is .fsproj
// /.props/.json text — not F# semantics — so it reads XML/JSON directly, no FCS.

/// Default analyzer packages wired when the caller doesn't name any.
let private defaultAnalyzerPackages = [ "G-Research.FSharp.Analyzers"; "Ionide.Analyzers" ]

/// NuGet's GeneratePathProperty emits an MSBuild property named `Pkg<id>` with every `.`
/// replaced by `_` (other characters, including `-`, preserved verbatim). This is the exact
/// mapping the repo's own Directory.Build.targets relies on, e.g.
/// G-Research.FSharp.Analyzers → PkgG-Research_FSharp_Analyzers and Ionide.Analyzers →
/// PkgIonide_Analyzers.
let private pkgPathProperty (packageId: string) = "Pkg" + packageId.Replace(".", "_")

/// Known-good pinned versions for the canonical packages so emitted snippets are
/// paste-ready; unknown packages get a `*` wildcard the caller should pin.
let private suggestedAnalyzerVersion (packageId: string) =
    match packageId.ToLowerInvariant() with
    | "g-research.fsharp.analyzers" -> "0.22.*"
    | "ionide.analyzers" -> "0.15.*"
    | _ -> "*"

let private analyzersBuildPackage = "FSharp.Analyzers.Build"
let private analyzersBuildVersion = "0.5.*"
let private fsharpAnalyzersToolVersion = "0.36.0"
let private analyzerDllSubPath = "analyzers/dotnet/fs"

/// Read an XML doc, returning None when it is missing or unreadable.
let private tryReadProjectOpt (path: string) =
    match tryReadProject path with
    | Ok doc -> Some doc
    | Error _ -> None

/// (packageId, hasGeneratePathProperty) for every PackageReference in a doc.
let private packageRefsOf (doc: XDocument) : (string * bool) list =
    doc.Descendants(xname "PackageReference")
    |> Seq.choose (fun el ->
        attr "Include" el
        |> Option.orElseWith (fun () -> attr "Update" el)
        |> Option.map (fun packageId ->
            let genPath =
                attr "GeneratePathProperty" el
                |> Option.map (fun v -> v.Equals("true", StringComparison.OrdinalIgnoreCase))
                |> Option.defaultValue false

            packageId, genPath))
    |> Seq.toList

let private analyzerPackageRefSnippet (packageId: string) =
    let version = suggestedAnalyzerVersion packageId

    String.concat
        "\n"
        [ $"<PackageReference Include=\"{packageId}\" Version=\"{version}\" GeneratePathProperty=\"true\">"
          "  <IncludeAssets>analyzers</IncludeAssets>"
          "  <PrivateAssets>all</PrivateAssets>"
          "</PackageReference>" ]

let private analyzersBuildRefSnippet =
    String.concat
        "\n"
        [ $"<PackageReference Include=\"{analyzersBuildPackage}\" Version=\"{analyzersBuildVersion}\">"
          "  <IncludeAssets>build</IncludeAssets>"
          "  <PrivateAssets>all</PrivateAssets>"
          "</PackageReference>" ]

/// The `--analyzers-path "$(Pkg…)/analyzers/dotnet/fs"` value for the requested packages.
let private analyzersFlagsValue (packages: string list) =
    packages
    |> List.map (fun packageId -> $"--analyzers-path \"$({pkgPathProperty packageId})/{analyzerDllSubPath}\"")
    |> String.concat " "

let private toolManifestSnippet =
    String.concat
        "\n"
        [ "{"
          "  \"version\": 1,"
          "  \"isRoot\": true,"
          "  \"tools\": {"
          "    \"fsharp-analyzers\": {"
          $"      \"version\": \"{fsharpAnalyzersToolVersion}\","
          "      \"commands\": [ \"fsharp-analyzers\" ]"
          "    }"
          "  }"
          "}" ]

let private toolEntrySnippet =
    String.concat
        "\n"
        [ "\"fsharp-analyzers\": {"
          $"  \"version\": \"{fsharpAnalyzersToolVersion}\","
          "  \"commands\": [ \"fsharp-analyzers\" ]"
          "}" ]

/// True when a dotnet-tools.json manifest already declares the fsharp-analyzers tool.
let private toolManifestHasFsharpAnalyzers (manifestPath: string) : bool =
    try
        match JsonNode.Parse(File.ReadAllText manifestPath) with
        | null -> false
        | node ->
            match node["tools"] with
            | :? JsonObject as tools -> not (isNull tools["fsharp-analyzers"])
            | _ -> false
    with _ ->
        false

/// Read-only analyzer-setup plan for one F# project. Reports what is already wired, what is
/// missing, and the exact snippet to add for each gap. Writes nothing.
let analyzerSetupPreview (args: FcsAnalyzerSetupPreviewArgs) : JsonNode =
    let invalidArgs message =
        jobj [ "status", jstr "invalid_args"; "message", jstr message ] :> JsonNode

    match resolveHealthProjectPath args.projectPath with
    | Error reason -> invalidArgs reason
    | Ok projectPath ->
        match tryReadProject projectPath with
        | Error reason -> invalidArgs $"Project file cannot be read: %s{reason}"
        | Ok doc ->
            let projectDir = Path.GetDirectoryName projectPath
            let projectFileName = Path.GetFileName projectPath

            let requestedPackages =
                args.analyzerPackages
                |> Option.map (List.choose (fun p -> if String.IsNullOrWhiteSpace p then None else Some(p.Trim())))
                |> Option.filter (List.isEmpty >> not)
                |> Option.defaultValue defaultAnalyzerPackages

            // Nearest config files, walking up from the project directory (MSBuild order).
            let dirBuildProps = findNearestUpwards projectDir [ "Directory.Build.props" ]
            let dirBuildTargets = findNearestUpwards projectDir [ "Directory.Build.targets" ]

            let toolManifest =
                findNearestUpwards projectDir [ Path.Combine(".config", "dotnet-tools.json"); "dotnet-tools.json" ]

            // PackageReference scan across the .fsproj + nearest Directory.Build.props (some
            // repos centralise analyzer refs there).
            let allPackageRefs =
                [ Some doc; dirBuildProps |> Option.bind tryReadProjectOpt ]
                |> List.choose id
                |> List.collect packageRefsOf

            let findRef (packageId: string) =
                allPackageRefs
                |> List.tryFind (fun (id, _) -> String.Equals(id, packageId, StringComparison.OrdinalIgnoreCase))

            // FSharpAnalyzersOtherFlags can live in the .fsproj or either Directory.Build.* file.
            let flagsValue =
                [ Some doc
                  dirBuildProps |> Option.bind tryReadProjectOpt
                  dirBuildTargets |> Option.bind tryReadProjectOpt ]
                |> List.choose id
                |> List.tryPick (childValue "FSharpAnalyzersOtherFlags")

            let changes = ResizeArray<JsonNode>()
            let notes = ResizeArray<string>()

            let addChange (file: string) (kind: string) (preview: string) (reason: string) =
                changes.Add(
                    jobj
                        [ "file", jstr file
                          "kind", jstr kind
                          "preview", jstr preview
                          "reason", jstr reason ]
                    :> JsonNode
                )

            // 1. Analyzer package refs + GeneratePathProperty per requested package.
            for packageId in requestedPackages do
                match findRef packageId with
                | None ->
                    addChange
                        projectFileName
                        "add_package_ref"
                        (analyzerPackageRefSnippet packageId)
                        $"%s{packageId} is not referenced — add it as an analyzer package (IncludeAssets=analyzers, PrivateAssets=all). GeneratePathProperty=true exposes $(%s{pkgPathProperty packageId}) for the analyzer flags."
                | Some(_, genPath) ->
                    if not genPath then
                        addChange
                            projectFileName
                            "enable_generate_path_property"
                            (analyzerPackageRefSnippet packageId)
                            $"%s{packageId} is referenced but lacks GeneratePathProperty=\"true\"; without it $(%s{pkgPathProperty packageId}) is empty and FSharpAnalyzersOtherFlags cannot locate the analyzer DLLs."

            // 2. FSharp.Analyzers.Build — the MSBuild glue that runs analyzers during build.
            match findRef analyzersBuildPackage with
            | Some _ -> ()
            | None ->
                addChange
                    projectFileName
                    "add_package_ref"
                    analyzersBuildRefSnippet
                    $"%s{analyzersBuildPackage} provides the MSBuild target that runs the analyzers during `dotnet build`; without it the analyzers never execute."

            // 3. FSharpAnalyzersOtherFlags property — points the host at the analyzer DLLs.
            let recommendedFlags = analyzersFlagsValue requestedPackages

            let flagsTargetFile =
                dirBuildTargets |> Option.map Path.GetFileName |> Option.defaultValue "Directory.Build.targets"

            match flagsValue with
            | None ->
                let preview =
                    match dirBuildTargets with
                    | Some _ -> $"<FSharpAnalyzersOtherFlags>{recommendedFlags}</FSharpAnalyzersOtherFlags>"
                    | None ->
                        String.concat
                            "\n"
                            [ "<Project>"
                              "  <PropertyGroup>"
                              $"    <FSharpAnalyzersOtherFlags>{recommendedFlags}</FSharpAnalyzersOtherFlags>"
                              "  </PropertyGroup>"
                              "</Project>" ]

                addChange
                    flagsTargetFile
                    "add_property"
                    preview
                    "FSharpAnalyzersOtherFlags tells the F# analyzer host where each package's analyzer DLLs live (via the GeneratePathProperty $(Pkg…) paths). It is missing — add it so the analyzers are discovered."
            | Some existing ->
                let missing =
                    requestedPackages
                    |> List.filter (fun id -> not (existing.Contains(pkgPathProperty id, StringComparison.OrdinalIgnoreCase)))

                if not missing.IsEmpty then
                    addChange
                        flagsTargetFile
                        "update_property"
                        $"<FSharpAnalyzersOtherFlags>{recommendedFlags}</FSharpAnalyzersOtherFlags>"
                        $"""FSharpAnalyzersOtherFlags is present but does not reference: %s{missing |> List.map pkgPathProperty |> String.concat ", "}. Update it so every requested analyzer package is on the --analyzers-path list."""

            // 4. Local fsharp-analyzers tool manifest — how analyzers run in CI.
            match toolManifest with
            | None ->
                addChange
                    (Path.Combine(".config", "dotnet-tools.json"))
                    "add_tool_manifest"
                    toolManifestSnippet
                    "No local tool manifest declares `fsharp-analyzers`; the CLI (`dotnet fsharp-analyzers --project …`) is how analyzers run in CI. Create one with `dotnet new tool-manifest`, then add the tool."
            | Some manifestPath ->
                if not (toolManifestHasFsharpAnalyzers manifestPath) then
                    let rel =
                        try
                            Path.GetRelativePath(projectDir, manifestPath)
                        with _ ->
                            Path.GetFileName manifestPath

                    addChange
                        rel
                        "add_tool"
                        toolEntrySnippet
                        $"A tool manifest exists (%s{Path.GetFileName manifestPath}) but has no `fsharp-analyzers` entry; add it under \"tools\" so `dotnet tool restore` makes the analyzer CLI available."

            // "Current" view: reuse the exact detection project_health's analyzers axis uses.
            let currentAnalyzers = analyzerPackages doc
            let alreadyConfigured = currentAnalyzers.Length > 0

            if alreadyConfigured then
                notes.Add "Project already references analyzer package(s); reporting only the gaps below."
            else
                notes.Add "No analyzer packages detected; the plan below wires analyzers from scratch."

            if changes.Count = 0 then
                notes.Add "All required analyzer wiring is already present for the requested packages — nothing to add."

            notes.Add $"""Requested analyzer packages: %s{String.concat ", " requestedPackages}."""

            notes.Add
                "Pkg path-property names map the package id with every '.' replaced by '_' (e.g. Ionide.Analyzers → PkgIonide_Analyzers); the snippets above already use the correct names."

            notes.Add
                "Preview only — this tool writes nothing. After applying the changes, run fcs_analyzer_diagnostics to read what the analyzers report."

            jobj
                [ "status", jstr "succeeded"
                  "project", jstr projectPath
                  "alreadyConfigured", jbool alreadyConfigured
                  "requestedAnalyzerPackages",
                  JsonArray(requestedPackages |> List.map jstr |> List.toArray) :> JsonNode
                  "currentAnalyzers", JsonArray(currentAnalyzers) :> JsonNode
                  "configFiles",
                  jobj
                      [ "directoryBuildProps", dirBuildProps |> Option.map jstr |> Option.defaultValue null
                        "directoryBuildTargets", dirBuildTargets |> Option.map jstr |> Option.defaultValue null
                        "toolManifest", toolManifest |> Option.map jstr |> Option.defaultValue null ]
                  "plannedChanges", JsonArray(changes.ToArray()) :> JsonNode
                  "notes", JsonArray(notes.ToArray() |> Array.map jstr) :> JsonNode ]
            :> JsonNode
