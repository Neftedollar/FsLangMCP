module FsLangMcp.ProjectInspection

open System
open System.IO
open System.Text.Json.Nodes
open FsLangMcp.Types
open FsLangMcp.ProjectFiles

let private packageReferenceToJson includePackageDetails element =
    let packageId =
        attr "Include" element
        |> Option.orElseWith (fun () -> attr "Update" element)
        |> Option.defaultValue ""

    if includePackageDetails then
        jobj
            [ "packageId", jstr packageId
              "version", attr "Version" element |> Option.map jstr |> Option.defaultValue null
              "includeAssets", attr "IncludeAssets" element |> Option.map jstr |> Option.defaultValue null
              "privateAssets", attr "PrivateAssets" element |> Option.map jstr |> Option.defaultValue null ]
        :> JsonNode
    else
        jstr packageId

let private projectReferenceToJson projectDir element =
    let includePath = attr "Include" element |> Option.defaultValue ""

    let resolvedPath =
        if Path.IsPathRooted(includePath) then
            Path.GetFullPath(includePath)
        else
            Path.GetFullPath(Path.Combine(projectDir, includePath))

    jobj
        [ "include", jstr includePath
          "path", jstr resolvedPath
          "exists", jbool (File.Exists resolvedPath) ]
    :> JsonNode

let private fileToJson (index: int) (file: ProjectFile) =
    jobj
        [ "order", jint index
          "path", jstr file.Path
          "include", jstr file.IncludePath
          "kind", jstr (if file.IsSignature then "signature" else "implementation")
          "link", file.Link |> Option.map jstr |> Option.defaultValue null
          "pairedImplementationPath", file.PairedImplementationPath |> Option.map jstr |> Option.defaultValue null
          "pairedSignaturePath", file.PairedSignaturePath |> Option.map jstr |> Option.defaultValue null
          "exists", jbool (File.Exists file.Path) ]
    :> JsonNode

let private propertyToJson name value = jobj [ "name", jstr name; "value", jstr value ] :> JsonNode

let inspectProject (args: FSharpProjectInspectArgs) : JsonNode =
    match resolveProjectPath args.projectPath with
    | Error reason ->
        jobj
            [ "status", jstr "error"
              "message", jstr reason
              "projectPath", jstr (Path.GetFullPath(args.projectPath)) ]
        :> JsonNode
    | Ok projectPath ->
        match tryReadProject projectPath with
        | Error reason ->
            jobj
                [ "status", jstr "error"
                  "message", jstr $"Project file cannot be read: %s{reason}"
                  "projectPath", jstr projectPath ]
            :> JsonNode
        | Ok doc ->
            let projectDir = Path.GetDirectoryName(projectPath)

            let workspaceRoot =
                args.workspacePath
                |> Option.map Path.GetFullPath
                |> Option.defaultValue projectDir

            let includeGenerated = args.includeGeneratedFiles |> Option.defaultValue false
            let includePackageDetails = args.includePackageDetails |> Option.defaultValue true
            let includeResolvedOptions = args.includeResolvedOptions |> Option.defaultValue false

            let filterOptions =
                { defaultFilterOptions ProjectInspection with
                    IncludeGenerated = includeGenerated
                    IncludeTests = true }

            let files = compileFiles projectPath doc
            let filtered = filterProjectFiles workspaceRoot filterOptions files

            let packageReferences =
                doc.Descendants(xname "PackageReference")
                |> Seq.map (packageReferenceToJson includePackageDetails)
                |> Seq.toArray

            let projectReferences =
                doc.Descendants(xname "ProjectReference")
                |> Seq.map (projectReferenceToJson projectDir)
                |> Seq.toArray

            let properties =
                [ "OutputType"
                  "TargetFramework"
                  "TargetFrameworks"
                  "LangVersion"
                  "TreatWarningsAsErrors"
                  "Nullable"
                  "GenerateDocumentationFile" ]
                |> List.choose (fun name -> childValue name doc |> Option.map (propertyToJson name))
                |> List.toArray

            jobj
                [ "status", jstr "ok"
                  "project",
                  jobj
                      [ "projectPath", jstr projectPath
                        "projectDirectory", jstr projectDir
                        "projectName", jstr (Path.GetFileNameWithoutExtension(projectPath))
                        "sdk", attr "Sdk" doc.Root |> Option.map jstr |> Option.defaultValue null
                        "targetFramework", childValue "TargetFramework" doc |> Option.map jstr |> Option.defaultValue null
                        "targetFrameworks", childValue "TargetFrameworks" doc |> Option.map jstr |> Option.defaultValue null
                        "outputType", childValue "OutputType" doc |> Option.map jstr |> Option.defaultValue null ]
                  "properties", JsonArray(properties) :> JsonNode
                  "compileOrder", JsonArray(filtered.Included |> List.mapi fileToJson |> List.toArray) :> JsonNode
                  "references",
                  jobj
                      [ "projectReferences", JsonArray(projectReferences) :> JsonNode
                        "packageReferences", JsonArray(packageReferences) :> JsonNode ]
                  "sourceSummary",
                  jobj
                      [ "sourceFileCount", jint files.Length
                        "includedSourceFileCount", jint filtered.Included.Length
                        "signatureFileCount", jint (files |> List.filter _.IsSignature |> List.length)
                        "hasSignatureFiles", jbool (files |> List.exists _.IsSignature)
                        "hasLinkedFiles", jbool (files |> List.exists (fun file -> file.Link.IsSome))
                        "hasGeneratedFiles", jbool (files |> List.exists (fun file -> isGeneratedFile file.Path)) ]
                  "filterSummary", filterSummaryToJson filtered :> JsonNode
                  "resolvedOptions",
                  (if includeResolvedOptions then
                       jobj [ "status", jstr "not_included_in_v0_4_0"; "reason", jstr "Use fcs_get_project_options for full compiler OtherOptions." ]
                   else
                       null) ]
            :> JsonNode

