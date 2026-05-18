module FsLangMcp.ProjectFiles

open System
open System.IO
open System.Xml.Linq
open System.Text.Json.Nodes
open FsLangMcp.Types

[<Struct>]
type internal ScanKind =
    | Outline
    | SymbolSearch
    | References
    | Diagnostics
    | TestDiscovery
    | ApiSurface
    | Review
    | DeadCode
    | ProjectInspection

[<Struct>]
type internal ExclusionReason =
    | ObjOrBinDirectory
    | GitDirectory
    | ToolCacheDirectory
    | TestResultArtifact
    | GeneratedFile
    | DesignerFile
    | AssemblyInfoFile
    | TemporaryFile
    | OutsideWorkspace
    | ExternalLinkedFile
    | UnsupportedExtension
    | OverMaxFilesLimit

type internal ScanFilterOptions =
    { IncludeGenerated: bool
      IncludeTests: bool
      IncludeExternalLinkedFiles: bool
      IncludeObjBin: bool
      MaxFiles: int option }

type internal ProjectFile =
    { Path: string
      IncludePath: string
      Link: string option
      IsSignature: bool
      PairedImplementationPath: string option
      PairedSignaturePath: string option }

type internal FilteredProjectFiles =
    { Included: ProjectFile list
      Excluded: (ProjectFile * ExclusionReason) list
      Truncated: bool }

let internal xname localName = XName.Get(localName)

let internal attr (name: string) (element: XElement) =
    match element.Attribute(xname name) with
    | null -> None
    | value -> Some value.Value

let internal childValue (name: string) (doc: XDocument) =
    doc.Descendants(xname name)
    |> Seq.tryPick (fun element ->
        if String.IsNullOrWhiteSpace element.Value then
            None
        else
            Some(element.Value.Trim()))

let internal boolProperty name doc =
    childValue name doc
    |> Option.bind (fun value ->
        match Boolean.TryParse(value) with
        | true, parsed -> Some parsed
        | false, _ -> None)

let internal tryReadProject (projectPath: string) =
    try
        Ok(XDocument.Load(projectPath))
    with ex ->
        Error ex.Message

let internal resolveProjectPath (input: string option) =
    match input with
    | None ->
        Error
            "projectPath is required. Either pass it explicitly or call set_project first to establish a default."
    | Some path when System.String.IsNullOrWhiteSpace path ->
        Error
            "projectPath must not be empty. Either pass a .fsproj path or call set_project first to establish a default."
    | Some path ->
        let fullPath = Path.GetFullPath(path)

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
            Error $"Project path does not exist or is not an .fsproj: %s{fullPath}"

let internal isFsFile (path: string) =
    let ext = Path.GetExtension(path)

    String.Equals(ext, ".fs", StringComparison.OrdinalIgnoreCase)
    || String.Equals(ext, ".fsi", StringComparison.OrdinalIgnoreCase)

let private normalizeSeparators (path: string) =
    path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)

let private hasSegment (segment: string) (path: string) =
    let separator: string = string Path.DirectorySeparatorChar
    let normalized = normalizeSeparators path
    normalized.Contains($"%s{separator}%s{segment}%s{separator}", StringComparison.OrdinalIgnoreCase)
    || normalized.EndsWith($"%s{separator}%s{segment}", StringComparison.OrdinalIgnoreCase)

let internal isGeneratedFile (path: string) =
    let fileName = Path.GetFileName(path)

    fileName.EndsWith(".g.fs", StringComparison.OrdinalIgnoreCase)
    || fileName.EndsWith(".g.i.fs", StringComparison.OrdinalIgnoreCase)
    || fileName.EndsWith(".generated.fs", StringComparison.OrdinalIgnoreCase)

let internal isDesignerFile (path: string) =
    Path.GetFileName(path).EndsWith(".Designer.fs", StringComparison.OrdinalIgnoreCase)

let internal isAssemblyInfoFile (path: string) =
    Path.GetFileName(path).EndsWith(".AssemblyInfo.fs", StringComparison.OrdinalIgnoreCase)

let private isTemporaryFile (path: string) =
    let fileName = Path.GetFileName(path)
    fileName.StartsWith("~", StringComparison.Ordinal) || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)

let private isTestResultArtifact (path: string) =
    hasSegment "TestResults" path
    || hasSegment "test-results" path
    || hasSegment "coverage" path

let private isTestFile (path: string) =
    let fileName = Path.GetFileNameWithoutExtension(path)
    let normalized = normalizeSeparators path

    fileName.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
    || fileName.EndsWith("Test", StringComparison.OrdinalIgnoreCase)
    || normalized.Contains($"%c{Path.DirectorySeparatorChar}tests%c{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)

let private reasonToString reason =
    match reason with
    | ObjOrBinDirectory -> "obj_or_bin_directory"
    | GitDirectory -> "git_directory"
    | ToolCacheDirectory -> "tool_cache_directory"
    | TestResultArtifact -> "test_result_artifact"
    | GeneratedFile -> "generated_file"
    | DesignerFile -> "designer_file"
    | AssemblyInfoFile -> "assembly_info_file"
    | TemporaryFile -> "temporary_file"
    | OutsideWorkspace -> "outside_workspace"
    | ExternalLinkedFile -> "external_linked_file"
    | UnsupportedExtension -> "unsupported_extension"
    | OverMaxFilesLimit -> "over_max_files_limit"

let internal defaultFilterOptions scanKind =
    { IncludeGenerated = false
      IncludeTests =
        match scanKind with
        | TestDiscovery -> true
        | _ -> false
      IncludeExternalLinkedFiles = false
      IncludeObjBin = false
      MaxFiles = None }

let private resolveCompilePath (projectDir: string) (includePath: string) =
    if Path.IsPathRooted(includePath) then
        Path.GetFullPath(includePath)
    else
        Path.GetFullPath(Path.Combine(projectDir, includePath))

let internal compileFiles (projectPath: string) (doc: XDocument) =
    let projectDir = Path.GetDirectoryName(projectPath)

    let rawFiles =
        doc.Descendants(xname "Compile")
        |> Seq.choose (fun element ->
            attr "Include" element
            |> Option.map (fun includePath ->
                let path = resolveCompilePath projectDir includePath

                { Path = path
                  IncludePath = includePath
                  Link = attr "Link" element
                  IsSignature = Path.GetExtension(path).Equals(".fsi", StringComparison.OrdinalIgnoreCase)
                  PairedImplementationPath = None
                  PairedSignaturePath = None }))
        |> Seq.filter (fun file -> isFsFile file.Path)
        |> Seq.toList

    let implementationByBase =
        rawFiles
        |> List.choose (fun file ->
            if file.IsSignature then
                None
            else
                Some(Path.ChangeExtension(file.Path, null), file.Path))
        |> Map.ofList

    let signatureByBase =
        rawFiles
        |> List.choose (fun file ->
            if file.IsSignature then
                Some(Path.ChangeExtension(file.Path, null), file.Path)
            else
                None)
        |> Map.ofList

    rawFiles
    |> List.map (fun file ->
        let basePath = Path.ChangeExtension(file.Path, null)

        { file with
            PairedImplementationPath =
                if file.IsSignature then
                    Map.tryFind basePath implementationByBase
                else
                    None
            PairedSignaturePath =
                if file.IsSignature then
                    None
                else
                    Map.tryFind basePath signatureByBase })

let private classifyFile (workspaceRoot: string) (options: ScanFilterOptions) (file: ProjectFile) =
    let path = file.Path
    let fullWorkspaceRoot = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar)
    let fullPath = Path.GetFullPath(path)

    if not (isFsFile path) then Some UnsupportedExtension
    elif hasSegment "obj" path || hasSegment "bin" path then
        if options.IncludeObjBin then None else Some ObjOrBinDirectory
    elif hasSegment ".git" path then Some GitDirectory
    elif hasSegment ".claude" path || hasSegment ".codex" path then Some ToolCacheDirectory
    elif isTestResultArtifact path then Some TestResultArtifact
    elif isTemporaryFile path then Some TemporaryFile
    elif isAssemblyInfoFile path then
        if options.IncludeGenerated then None else Some AssemblyInfoFile
    elif isDesignerFile path then
        if options.IncludeGenerated then None else Some DesignerFile
    elif isGeneratedFile path then
        if options.IncludeGenerated then None else Some GeneratedFile
    elif isTestFile path && not options.IncludeTests then Some TestResultArtifact
    elif file.Link.IsSome && not options.IncludeExternalLinkedFiles then Some ExternalLinkedFile
    elif not (fullPath.StartsWith(fullWorkspaceRoot, StringComparison.OrdinalIgnoreCase)) then Some OutsideWorkspace
    else None

let internal filterProjectFiles workspaceRoot options files =
    let included = ResizeArray<ProjectFile>()
    let excluded = ResizeArray<ProjectFile * ExclusionReason>()
    let mutable truncated = false

    for file in files do
        match classifyFile workspaceRoot options file with
        | Some reason -> excluded.Add(file, reason)
        | None ->
            match options.MaxFiles with
            | Some maxFiles when included.Count >= maxFiles ->
                truncated <- true
                excluded.Add(file, OverMaxFilesLimit)
            | _ -> included.Add(file)

    { Included = included |> Seq.toList
      Excluded = excluded |> Seq.toList
      Truncated = truncated }

let internal filterSummaryToJson (filtered: FilteredProjectFiles) =
    let reasonCounts =
        filtered.Excluded
        |> List.countBy snd
        |> List.map (fun (reason, count) -> reasonToString reason, jint count)

    jobj
        [ "includedFiles", jint filtered.Included.Length
          "excludedFiles", jint filtered.Excluded.Length
          "exclusionsByReason", jobj reasonCounts
          "truncated", jbool filtered.Truncated ]

// ─── SolutionParsing ──────────────────────────────────────────────────────────
// Extracts .fsproj paths from .sln / .slnx files. Pure functions, no IO beyond
// reading the solution file.

module internal SolutionParsing =
    let private xname localName = XName.Get(localName)

    let private attr (name: string) (element: XElement) =
        match element.Attribute(xname name) with
        | null -> None
        | value -> Some value.Value

    /// Parses an .sln file and returns absolute paths of .fsproj entries that exist on disk.
    let fsprojsFromSln (slnPath: string) : string array =
        let slnDir = Path.GetDirectoryName(slnPath)

        File.ReadAllLines(slnPath)
        |> Array.choose (fun line ->
            let trimmed = line.TrimStart()

            if trimmed.StartsWith("Project(", StringComparison.OrdinalIgnoreCase) then
                let parts = trimmed.Split('"')
                // Project("{type}") = "Name", "relative\path.fsproj", "{guid}"
                // indices:   1              3         5
                if parts.Length > 5 && parts[5].EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) then
                    let normalized = parts[5].Replace('\\', Path.DirectorySeparatorChar)
                    let full = Path.GetFullPath(Path.Combine(slnDir, normalized))
                    if File.Exists full then Some full else None
                else
                    None
            else
                None)

    /// Parses an .slnx file and returns absolute paths of .fsproj entries that exist on disk.
    let fsprojsFromSlnx (slnxPath: string) : string array =
        let slnxDir = Path.GetDirectoryName(slnxPath)

        try
            let doc = XDocument.Load(slnxPath)

            doc.Descendants(xname "Project")
            |> Seq.choose (fun el ->
                attr "Path" el
                |> Option.filter (fun p -> p.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
                |> Option.map (fun p ->
                    let normalized =
                        p.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)

                    Path.GetFullPath(Path.Combine(slnxDir, normalized))))
            |> Seq.filter File.Exists
            |> Seq.toArray
        with _ ->
            [||]

    /// Lists .fsproj files referenced by the given workspace target. For a direct
    /// .fsproj path, returns [path]. For .sln / .slnx, returns all .fsproj entries
    /// that exist on disk. For any other path, returns an empty array.
    let listProjects (workspacePath: string) : string array =
        if not (File.Exists workspacePath) then
            [||]
        else
            let ext = Path.GetExtension(workspacePath)

            if String.Equals(ext, ".fsproj", StringComparison.OrdinalIgnoreCase) then
                [| Path.GetFullPath(workspacePath) |]
            elif String.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase) then
                fsprojsFromSlnx workspacePath
            elif String.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase) then
                fsprojsFromSln workspacePath
            else
                [||]
