module FsLangMcp.ProjectInspection

open System
open System.IO
open System.Text.Json.Nodes
open FsLangMcp.Types
open FsLangMcp.ProjectFiles

let private packageReferenceToJson includePackageDetails (reference: EvaluatedPackageReference) =
    if includePackageDetails then
        jobj
            [ "packageId", jstr reference.PackageId
              "version", reference.Version |> Option.map jstr |> Option.defaultValue null
              "includeAssets", reference.IncludeAssets |> Option.map jstr |> Option.defaultValue null
              "privateAssets", reference.PrivateAssets |> Option.map jstr |> Option.defaultValue null
              "fullPath", reference.FullPath |> Option.map jstr |> Option.defaultValue null
              "evaluationSource", jstr "msbuild-evaluated" ]
        :> JsonNode
    else
        jstr reference.PackageId

let private projectReferenceToJson (reference: EvaluatedProjectReference) =
    jobj
        [ "include", jstr reference.IncludePath
          "path", jstr reference.ProjectPath
          "exists", jbool (File.Exists reference.ProjectPath)
          "targetFramework", reference.TargetFramework |> Option.map jstr |> Option.defaultValue null
          "evaluationSource", jstr "msbuild-evaluated" ]
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
          "exists", jbool (File.Exists file.Path)
          "evaluationSource", jstr "msbuild-evaluated" ]
    :> JsonNode

let private tryProperty name (properties: Map<string, string>) =
    properties
    |> Map.toSeq
    |> Seq.tryPick (fun (key, value) ->
        if String.Equals(key, name, StringComparison.OrdinalIgnoreCase) then Some value else None)

let private propertyToJson name value =
    jobj [ "name", jstr name; "value", jstr value; "source", jstr "msbuild-evaluated" ] :> JsonNode

let internal inspectProject
    (args: FSharpProjectInspectArgs)
    (evaluatedProjectProvider: EvaluatedProjectSnapshotProvider)
    : Async<JsonNode> =
    async {
        match resolveProjectPath args.projectPath with
        | Error reason ->
            return
                jobj
                    [ "status", jstr "error"
                      "message", jstr reason
                      "projectPath",
                      args.projectPath
                      |> Option.map (Path.GetFullPath >> jstr)
                      |> Option.defaultValue null ]
                :> JsonNode
        | Ok projectPath ->
            match! evaluatedProjectProvider projectPath with
            | Error reason ->
                return
                    jobj
                        [ "status", jstr "error"
                          "message", jstr $"Project cannot be evaluated: %s{reason}"
                          "projectPath", jstr projectPath
                          "evaluation",
                          jobj
                              [ "status", jstr "unavailable"
                                "source", jstr "ionide-proj-info"
                                "reason", jstr reason ] ]
                    :> JsonNode
            | Ok snapshot ->
                let workspaceRoot =
                    args.workspacePath
                    |> Option.map Path.GetFullPath
                    |> Option.defaultValue snapshot.ProjectDirectory

                let includeGenerated = args.includeGeneratedFiles |> Option.defaultValue false
                let includePackageDetails = args.includePackageDetails |> Option.defaultValue true
                let includeResolvedOptions = args.includeResolvedOptions |> Option.defaultValue false

                let filterOptions =
                    { defaultFilterOptions ProjectInspection with
                        IncludeGenerated = includeGenerated
                        IncludeTests = true
                        // Compile order is a project-model view, so evaluated linked
                        // items remain part of it even when they live outside the
                        // project directory. Their absolute path is reported explicitly.
                        IncludeExternalLinkedFiles = true }

                let files = snapshot.Files
                let filtered = filterProjectFiles workspaceRoot filterOptions files

                let packageReferences =
                    snapshot.PackageReferences
                    |> List.map (packageReferenceToJson includePackageDetails)
                    |> List.toArray

                let projectReferences =
                    snapshot.ProjectReferences |> List.map projectReferenceToJson |> List.toArray

                let properties =
                    [ "OutputType"
                      "TargetFramework"
                      "TargetFrameworks"
                      "LangVersion"
                      "TreatWarningsAsErrors"
                      "Nullable"
                      "GenerateDocumentationFile" ]
                    |> List.choose (fun name ->
                        tryProperty name snapshot.Properties |> Option.map (propertyToJson name))
                    |> List.toArray

                return
                    jobj
                        [ "status", jstr "ok"
                          "evaluation",
                          jobj
                              [ "status", jstr "evaluated"
                                "source", jstr snapshot.EvaluationSource
                                "projectPath", jstr snapshot.ProjectPath
                                "targetFramework",
                                snapshot.TargetFramework |> Option.map jstr |> Option.defaultValue null
                                "restoreSucceeded", jbool snapshot.RestoreSucceeded
                                "importCount", jint snapshot.ImportedProjects.Length
                                "imports",
                                JsonArray(snapshot.ImportedProjects |> List.map jstr |> List.toArray)
                                :> JsonNode ]
                          "project",
                          jobj
                              [ "projectPath", jstr snapshot.ProjectPath
                                "projectDirectory", jstr snapshot.ProjectDirectory
                                "projectName", jstr snapshot.ProjectName
                                "sdk", snapshot.Sdk |> Option.map jstr |> Option.defaultValue null
                                "targetFramework",
                                snapshot.TargetFramework |> Option.map jstr |> Option.defaultValue null
                                "targetFrameworks",
                                (match snapshot.TargetFrameworks with
                                 | [] -> null
                                 | frameworks -> jstr (String.concat ";" frameworks))
                                "outputType", snapshot.OutputType |> Option.map jstr |> Option.defaultValue null
                                "evaluationSource", jstr snapshot.EvaluationSource ]
                          "properties", JsonArray(properties) :> JsonNode
                          "compileOrder", JsonArray(filtered.Included |> List.mapi fileToJson |> List.toArray) :> JsonNode
                          "references",
                          jobj
                              [ "projectReferences", JsonArray(projectReferences) :> JsonNode
                                "packageReferences", JsonArray(packageReferences) :> JsonNode
                                "evaluationSource", jstr snapshot.EvaluationSource ]
                          "sourceSummary",
                          jobj
                              [ "sourceFileCount", jint files.Length
                                "includedSourceFileCount", jint filtered.Included.Length
                                "signatureFileCount", jint (files |> List.filter _.IsSignature |> List.length)
                                "hasSignatureFiles", jbool (files |> List.exists _.IsSignature)
                                "hasLinkedFiles", jbool (files |> List.exists (fun file -> file.Link.IsSome))
                                "hasGeneratedFiles", jbool (files |> List.exists (fun file -> isGeneratedFile file.Path))
                                "evaluationSource", jstr snapshot.EvaluationSource ]
                          "filterSummary", filterSummaryToJson filtered :> JsonNode
                          "resolvedOptions",
                          (if includeResolvedOptions then
                               jobj
                                   [ "status", jstr "included"
                                     "source", jstr snapshot.EvaluationSource
                                     "otherOptions",
                                     JsonArray(snapshot.OtherOptions |> Array.map jstr) :> JsonNode ]
                           else
                               null) ]
                    :> JsonNode
    }
