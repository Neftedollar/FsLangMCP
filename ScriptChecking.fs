module internal FsLangMcp.ScriptChecking

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text

[<NoComparison; NoEquality>]
type ScriptResult =
    { Options: FSharpProjectOptions
      Results: FSharpCheckProjectResults
      Enrichment: FsLangMcp.ConstructorDiagnostics.Enrichment
      Diagnostics: FSharpDiagnostic array }

[<RequireQualifiedAccess; NoComparison; NoEquality>]
type ScriptFailure =
    | Timeout
    | Busy
    | SdkNotFound of FsLangMcp.SdkPreflight.SdkNotFoundException
    | SdkUnavailable of string
    | Failed of string

exception private ScriptSdkResolutionException of string

/// Resolving #r "nuget:" can retain non-cancellable compiler/restore work. A timed
/// out caller must not release admission while that work is still running. Each
/// bridge owns one script slot, released only by the actual worker's finally.
type Runner(checker: FSharpChecker, ?beforeCheckOverride: unit -> Task) =
    let gate = new SemaphoreSlim(1, 1)

    member internal _.IsBusy = gate.CurrentCount = 0

    member _.Check(path: string, timeout: TimeSpan) : Task<Microsoft.FSharp.Core.Result<ScriptResult, ScriptFailure>> =
        task {
            let elapsed = System.Diagnostics.Stopwatch.StartNew()
            let mutable expired = 0
            let ensureActive () =
                if Volatile.Read(&expired) <> 0 || elapsed.Elapsed >= timeout then
                    raise (TimeoutException())

            if timeout <= TimeSpan.Zero then
                return Error ScriptFailure.Timeout
            elif not (gate.Wait(0)) then
                return Error ScriptFailure.Busy
            else
                let work =
                    Task.Run<Microsoft.FSharp.Core.Result<ScriptResult, ScriptFailure>>(fun () ->
                        task {
                            try
                                try
                                    ensureActive ()
                                    match beforeCheckOverride with
                                    | Some beforeCheck -> do! beforeCheck ()
                                    | None -> ()
                                    ensureActive ()
                                    // FCS can silently fall back to its host SDK when
                                    // the script's exact global.json pin is absent.
                                    // Keep the preflight inside retained admission so
                                    // an expired caller cannot leak another worker.
                                    FsLangMcp.SdkPreflight.ensure [Path.GetDirectoryName(path)]
                                    ensureActive ()
                                    let runDotnet arguments =
                                        FsLangMcp.ProcessRunner.runAsyncInDirectory
                                            (FsLangMcp.SdkPreflight.resolveDotnetBinary ()) arguments
                                            (Path.GetDirectoryName(path)) ["DOTNET_CLI_UI_LANGUAGE", "en-US"]
                                            (timeout - elapsed.Elapsed) CancellationToken.None
                                    let! sdkInfo = runDotnet ["--info"]
                                    ensureActive ()
                                    let sdkDirectory =
                                        sdkInfo.StandardOutput.Split('\n')
                                        |> Array.tryPick (fun line ->
                                            let line = line.Trim()
                                            if line.StartsWith("Base Path:", StringComparison.Ordinal) then
                                                Some(line.Substring("Base Path:".Length).Trim())
                                            else None)
                                        |> Option.filter Directory.Exists
                                    let! sdkDirectory =
                                        task {
                                            match sdkDirectory with
                                            | Some directory when sdkInfo.ExitCode = 0 -> return directory
                                            | _ ->
                                                let! version = runDotnet ["--version"]
                                                return raise (ScriptSdkResolutionException(
                                                    "Could not resolve the .NET SDK for this script. "
                                                    + version.StandardOutput + version.StandardError))
                                        }
                                    ensureActive ()
                                    let fsiSettings = Path.Combine(sdkDirectory, "FSharp", "FSharp.Compiler.Interactive.Settings.dll")
                                    if not (File.Exists fsiSettings) then
                                        raise (ScriptSdkResolutionException($"The selected script SDK lacks its FSI settings library: {fsiSettings}"))
                                    let source = File.ReadAllText(path) |> SourceText.ofString
                                    let! options, resolutionDiagnostics =
                                        checker.GetProjectOptionsFromScript(
                                            path,
                                            source,
                                            useSdkRefs = true,
                                            useFsiAuxLib = true,
                                            assumeDotNetFramework = false,
                                            sdkDirOverride = sdkDirectory,
                                            otherFlags = [| "-r:" + fsiSettings |],
                                            loadedTimeStamp = DateTime.UtcNow
                                        )
                                        |> Async.StartAsTask

                                    // Preflight proves only exact disable pins. FCS
                                    // also reports other unresolved SDK policies as
                                    // FS3384 while falling back to host references.
                                    // That fallback cannot certify this script.
                                    match resolutionDiagnostics |> List.tryFind (fun d -> d.ErrorNumber = 3384) with
                                    | Some diagnostic -> raise (ScriptSdkResolutionException diagnostic.Message)
                                    | None -> ()

                                    // Scripts and every #load source are checked, never
                                    // evaluated. Invalidate to observe same-length edits
                                    // and changes in loaded files on repeated checks.
                                    ensureActive ()
                                    checker.InvalidateConfiguration(options)
                                    let! results = checker.ParseAndCheckProject(options) |> Async.StartAsTask
                                    ensureActive ()
                                    let! enrichment =
                                        FsLangMcp.ConstructorDiagnostics.fromProjectResults
                                            checker options results
                                            (fun () -> Volatile.Read(&expired) = 0 && elapsed.Elapsed < timeout)
                                        |> Async.StartAsTask
                                    ensureActive ()
                                    let diagnostics =
                                        Array.append (List.toArray resolutionDiagnostics) results.Diagnostics
                                        |> Array.distinctBy (fun d ->
                                            d.FileName, d.ErrorNumber, d.Severity, d.StartLine,
                                            d.StartColumn, d.EndLine, d.EndColumn, d.Message)

                                    return Ok { Options = options; Results = results; Enrichment = enrichment; Diagnostics = diagnostics }
                                with
                                | :? TimeoutException -> return Error ScriptFailure.Timeout
                                | FsLangMcp.SdkPreflight.SdkPinUnsatisfiable failure ->
                                    return Error(ScriptFailure.SdkNotFound failure)
                                | ScriptSdkResolutionException message ->
                                    return Error(ScriptFailure.SdkUnavailable message)
                                | ex -> return Error(ScriptFailure.Failed ex.Message)
                            finally
                                gate.Release() |> ignore
                        })

                try
                    let remaining = timeout - elapsed.Elapsed
                    if remaining <= TimeSpan.Zero then
                        Interlocked.Exchange(&expired, 1) |> ignore
                        return Error ScriptFailure.Timeout
                    else
                        return! work.WaitAsync(remaining)
                with :? TimeoutException ->
                    Interlocked.Exchange(&expired, 1) |> ignore
                    return Error ScriptFailure.Timeout
        }
