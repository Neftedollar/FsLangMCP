module FsLangMcp.Program

open System
open System.IO
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open FsLangMcp.Types
open FsLangMcp.LspBridge
open FsLangMcp.FcsBridge
open FsLangMcp.ProjectHealth
open FsLangMcp.ProjectInspection
open FsLangMcp.Tools
open FsLangMcp.RuntimeStatus
open FsLangMcp.ProcessRunner
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server
open System.Text.Json
open System.Text.Json.Nodes
open System.Reflection
open System.Text.RegularExpressions

// ─── CLI helpers ───────────────────────────────────────────────────────────────

let private readPositiveIntEnv (name: string) (defaultValue: int) =
    match Environment.GetEnvironmentVariable(name) with
    | null -> defaultValue
    | value ->
        match Int32.TryParse(value) with
        | true, parsed when parsed > 0 -> parsed
        | _ -> defaultValue

let private timeoutFromEnv (name: string) (defaultMilliseconds: int) =
    TimeSpan.FromMilliseconds(float (readPositiveIntEnv name defaultMilliseconds))

let private runProcess (fileName: string) (args: string list) =
    let result =
        ProcessRunner.run
            fileName
            args
            (timeoutFromEnv "FSLANGMCP_BOOTSTRAP_TIMEOUT_MS" 300_000)

    result.ExitCode, result.StandardOutput, result.StandardError

let private parseProjInfoOutput (path: string) (exitCode: int) (stdout: string) (stderr: string) : JsonNode =
    if exitCode <> 0 then
        jobj [ "error", jstr $"proj-info failed (exit %d{exitCode})"; "stderr", jstr stderr ] :> JsonNode
    elif String.IsNullOrWhiteSpace(stdout) then
        jobj [ "error", jstr "proj-info produced no output" ] :> JsonNode
    else
        let token = JsonNode.Parse(stdout)

        let json =
            match token with
            | :? JsonArray as arr when arr.Count > 0 ->
                match arr.[0] with
                | :? JsonObject as obj -> obj
                | other ->
                    let kind = other.GetValueKind()
                    jobj [ "warning", jstr (sprintf "unexpected element type: %s" (kind.ToString())) ]
            | :? JsonObject as obj -> obj
            | _ -> JsonObject()

        let otherOptions =
            json["OtherOptions"]
            |> Option.ofObj
            |> Option.map (fun t -> t.Deserialize<string array>() |> Option.ofObj |> Option.defaultValue [||])
            |> Option.defaultValue [||]

        jobj
            [ "projectPath", jstr path
              "otherOptions", JsonArray(otherOptions |> Array.map jstr) :> JsonNode
              "optionsCount", jint otherOptions.Length ]
        :> JsonNode

let private runProjInfoAsync (path: string) : Task<JsonNode> =
    task {
        InstallationHealth.ensureCurrent ()

        // #192: the out-of-process proj-info CLI calls the same Init.init and dies the
        // same way (exit 134, unhandled), so it needs the same pre-flight the in-process
        // loader got. Raising lets Tools.toolResult render the shared typed envelope.
        // Walk from the path itself when it is a directory: starting at its parent
        // would skip a global.json sitting inside it.
        let projectDirectory =
            let full = Path.GetFullPath path

            if Directory.Exists full then
                full
            else
                Path.GetDirectoryName full

        do! Task.Run(fun () -> SdkPreflight.ensure [ projectDirectory ])

        try
            let! result =
                ProcessRunner.runAsync
                    "proj-info"
                    [ "--project"; path; "--fcs"; "--serialize" ]
                    (timeoutFromEnv "FSLANGMCP_PROJ_INFO_TIMEOUT_MS" 120_000)
                    CancellationToken.None

            return parseProjInfoOutput path result.ExitCode result.StandardOutput result.StandardError
        with
        | :? System.ComponentModel.Win32Exception
        | :? IOException ->
            return
                raise (
                    FileNotFoundException
                        "proj-info not found on PATH. Install with: dotnet tool install -g ionide.projinfo.tool"
                )
    }

let internal runLimitedWithTimeoutCore
    (gate: SemaphoreSlim)
    (cancellationToken: CancellationToken)
    (timeoutMs: int option)
    (onAdmitted: unit -> unit)
    (work: int option -> Task<JsonNode>)
    : Task<JsonNode> =
    match timeoutMs with
    | Some milliseconds when milliseconds < 0 ->
        Task.FromResult(
            jobj
                [ "status", jstr "invalid_args"
                  "errorKind", jstr "invalid_timeout"
                  "message", jstr $"timeoutMs must be non-negative; got %d{milliseconds}."
                  "timeoutMs", jint milliseconds ]
            :> JsonNode
        )
    | _ ->
        task {
            let stopwatch = Stopwatch.StartNew()
            let admissionTimeoutMs =
                match timeoutMs with
                | Some milliseconds -> milliseconds
                | None -> Timeout.Infinite

            let mutable entered = false

            try
                let! admitted = gate.WaitAsync(admissionTimeoutMs, cancellationToken)

                if not admitted then
                    return
                        jobj
                            [ "status", jstr "timeout"
                              "errorKind", jstr "fcs_admission_timeout"
                              "message",
                              jstr $"FCS admission timed out after %d{admissionTimeoutMs} ms before protected work started."
                              "timeoutMs", jint admissionTimeoutMs
                              "retryable", jbool true ]
                        :> JsonNode
                else
                    entered <- true
                    onAdmitted ()

                    // Cancellation can race with a released slot. Once protected work starts it
                    // owns the slot until its returned Task really completes; before that point a
                    // cancelled waiter must never become a late starter.
                    cancellationToken.ThrowIfCancellationRequested()

                    let remainingTimeoutMs =
                        match timeoutMs with
                        | Some milliseconds ->
                            let remaining = max 0L (int64 milliseconds - stopwatch.ElapsedMilliseconds)
                            Some(int remaining)
                        | None -> None

                    return! work remainingTimeoutMs
            finally
                if entered then
                    gate.Release() |> ignore
        }

let internal runLimitedWithTimeout
    (gate: SemaphoreSlim)
    (cancellationToken: CancellationToken)
    (timeoutMs: int option)
    (work: int option -> Task<JsonNode>)
    : Task<JsonNode> =
    runLimitedWithTimeoutCore gate cancellationToken timeoutMs ignore work

/// Owns one admitted FCS slot while the response-producing task and any explicitly
/// retained non-cancellable workers are still active. Retention must happen before
/// the response task settles; ProjectOutline does that in the timeout/cancellation
/// catch path that observed the underlying task still running.
type internal ProtectedWorkLifetime(gate: SemaphoreSlim) =
    let mutable pendingOwners = 1

    let releaseOne () =
        if Interlocked.Decrement(&pendingOwners) = 0 then
            gate.Release() |> ignore

    member _.Retain(operation: Task) =
        Interlocked.Increment(&pendingOwners) |> ignore

        operation.ContinueWith(
            (fun (completed: Task) ->
                if completed.IsFaulted then
                    completed.Exception |> ignore

                releaseOne ()),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        )
        |> ignore

    member _.CompleteResponseOwner() = releaseOne ()

let internal runLimitedWithTimeoutRetainedCore
    (gate: SemaphoreSlim)
    (cancellationToken: CancellationToken)
    (timeoutMs: int option)
    (onAdmitted: unit -> unit)
    (work: int option -> (Task -> unit) -> Task<JsonNode>)
    : Task<JsonNode> =
    match timeoutMs with
    | Some milliseconds when milliseconds < 0 ->
        Task.FromResult(
            jobj
                [ "status", jstr "invalid_args"
                  "errorKind", jstr "invalid_timeout"
                  "message", jstr $"timeoutMs must be non-negative; got %d{milliseconds}."
                  "timeoutMs", jint milliseconds ]
            :> JsonNode
        )
    | _ ->
        task {
            let stopwatch = Stopwatch.StartNew()
            let admissionTimeoutMs = timeoutMs |> Option.defaultValue Timeout.Infinite
            let mutable lifetime: ProtectedWorkLifetime option = None

            try
                let! admitted = gate.WaitAsync(admissionTimeoutMs, cancellationToken)

                if not admitted then
                    return
                        jobj
                            [ "status", jstr "timeout"
                              "errorKind", jstr "fcs_admission_timeout"
                              "message",
                              jstr
                                  $"FCS admission timed out after %d{admissionTimeoutMs} ms before protected work started."
                              "timeoutMs", jint admissionTimeoutMs
                              "retryable", jbool true ]
                        :> JsonNode
                else
                    let admittedLifetime = ProtectedWorkLifetime(gate)
                    lifetime <- Some admittedLifetime
                    onAdmitted ()
                    cancellationToken.ThrowIfCancellationRequested()

                    let remainingTimeoutMs =
                        timeoutMs
                        |> Option.map (fun milliseconds ->
                            max 0L (int64 milliseconds - stopwatch.ElapsedMilliseconds) |> int)

                    return! work remainingTimeoutMs admittedLifetime.Retain
            finally
                lifetime |> Option.iter _.CompleteResponseOwner()
        }

let internal runLimitedWithTimeoutRetained
    (gate: SemaphoreSlim)
    (cancellationToken: CancellationToken)
    (timeoutMs: int option)
    (work: int option -> (Task -> unit) -> Task<JsonNode>)
    : Task<JsonNode> =
    runLimitedWithTimeoutRetainedCore gate cancellationToken timeoutMs ignore work

/// Find-specific admission wrapper. Unlike the legacy relative-timeout helper, this
/// receives the same monotonic deadline object that the admitted find operation uses,
/// so queue wait and every later phase consume one clock. The semantic cutoff reserves
/// FindRequestDeadline.ResponseAllowanceMs for constructing a typed response.
let internal runLimitedWithFindDeadlineRetainedCore
    (gate: SemaphoreSlim)
    (cancellationToken: CancellationToken)
    (deadline: FindRequestDeadline)
    (onAdmitted: unit -> unit)
    (work: FindRequestDeadline -> (Task -> unit) -> Task<JsonNode>)
    : Task<JsonNode> =
    task {
        let mutable lifetime: ProtectedWorkLifetime option = None

        let admissionTimeout () =
            jobj
                [ "status", jstr "timeout"
                  "errorKind", jstr "fcs_admission_timeout"
                  "message",
                  jstr
                      $"FCS admission exhausted find's end-to-end timeoutMs=%d{deadline.TimeoutMs} before protected work started."
                  "timeoutMs", jint deadline.TimeoutMs
                  "retryable", jbool true ]
            :> JsonNode

        try
            let remaining = deadline.RemainingSemanticBudget()

            if remaining <= TimeSpan.Zero then
                deadline.MarkSemanticExpired()
                return admissionTimeout ()
            else
                use admissionCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)

                let admission = gate.WaitAsync(admissionCancellation.Token)
                let timeout = Task.Delay(remaining, admissionCancellation.Token)

                let contenders =
                    match deadline.SemanticExpirySignal with
                    | Some signal -> [| admission; timeout; signal |]
                    | None -> [| admission; timeout |]

                let! winner = Task.WhenAny(contenders)
                admissionCancellation.Cancel()

                let admitted =
                    Object.ReferenceEquals(winner, admission)
                    && admission.IsCompletedSuccessfully
                    && not deadline.SemanticExpired

                if not admitted then
                    try
                        do! admission
                    with :? OperationCanceledException ->
                        ()

                    // The semaphore can complete concurrently with expiry after WhenAny
                    // chose the other contender. Return that raced permit explicitly.
                    if admission.IsCompletedSuccessfully then
                        gate.Release() |> ignore

                    cancellationToken.ThrowIfCancellationRequested()
                    deadline.MarkSemanticExpired()
                    return admissionTimeout ()
                else
                    let admittedLifetime = ProtectedWorkLifetime(gate)
                    lifetime <- Some admittedLifetime
                    onAdmitted ()
                    cancellationToken.ThrowIfCancellationRequested()

                    if deadline.SemanticExpired then
                        deadline.MarkSemanticExpired()
                        return admissionTimeout ()
                    else
                        return! work deadline admittedLifetime.Retain
        finally
            lifetime |> Option.iter _.CompleteResponseOwner()
    }

let internal runLimitedWithFindDeadlineRetained
    (gate: SemaphoreSlim)
    (cancellationToken: CancellationToken)
    (deadline: FindRequestDeadline)
    (work: FindRequestDeadline -> (Task -> unit) -> Task<JsonNode>)
    : Task<JsonNode> =
    runLimitedWithFindDeadlineRetainedCore gate cancellationToken deadline ignore work

let internal runLimited
    (gate: SemaphoreSlim)
    (cancellationToken: CancellationToken)
    (work: unit -> Task<JsonNode>)
    : Task<JsonNode> =
    runLimitedWithTimeout gate cancellationToken None (fun _ -> work ())

let internal normalizeProjectOutlineAdmissionResult (timeoutMs: int) (result: JsonNode) : JsonNode =
    let isAdmissionTimeout =
        match result["errorKind"] with
        | null -> false
        | kind -> kind.GetValue<string>() = "fcs_admission_timeout"

    if not isAdmissionTimeout then
        result
    else
        let issue =
            jobj
                [ "phase", jstr "admission"
                  "status", jstr "timed_out"
                  "errorKind", jstr "fcs_admission_timeout"
                  "message", result["message"].DeepClone() ]
            :> JsonNode

        jobj
            [ "status", jstr "unknown"
              "errorKind", jstr "fcs_admission_timeout"
              "message", result["message"].DeepClone()
              "timeoutMs", jint timeoutMs
              "retryable", jbool true
              "resultSetComplete", jbool false
              "coverage",
              jobj
                  [ "complete", jbool false
                    "filesRequested", jint 0
                    "filesScanned", jint 0
                    "filesTimedOut", jint 0
                    "filesFailed", jint 0
                    "filesNotStarted", jint 0
                    "phases",
                    JsonArray([| jobj [ "phase", jstr "admission"; "status", jstr "timed_out" ] :> JsonNode |])
                    :> JsonNode
                    "issues", JsonArray([| issue |]) :> JsonNode
                    "issuesReturned", jint 1
                    "issuesTruncated", jbool false ]
              :> JsonNode
              "truncated", jbool false
              "nextCursor", null
              "files", JsonArray() :> JsonNode ]
        :> JsonNode

type internal RuntimeToolPin =
    { PackageId: string
      Version: string
      Command: string }

[<RequireQualifiedAccess>]
module internal RuntimeToolManifest =
    [<Literal>]
    let ResourceName = "FsLangMcp.dotnet-tools.json"

    let private requiredRuntimeTools =
        [ "fantomas", "fantomas"
          "fsautocomplete", "fsautocomplete"
          "ionide.projinfo.tool", "proj-info" ]

    let loadPinnedRuntimeTools () : RuntimeToolPin list =
        use stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)

        if isNull stream then
            invalidOp $"Embedded runtime tool manifest '{ResourceName}' was not found."

        use document = JsonDocument.Parse(stream)
        let root = document.RootElement

        if root.GetProperty("version").GetInt32() <> 1 || not (root.GetProperty("isRoot").GetBoolean()) then
            invalidOp "The embedded dotnet-tools.json must be a root version-1 tool manifest."

        let tools = root.GetProperty("tools")

        requiredRuntimeTools
        |> List.map (fun (packageId, command) ->
            let mutable entry = Unchecked.defaultof<JsonElement>

            if not (tools.TryGetProperty(packageId, &entry)) then
                invalidOp $"The embedded tool manifest does not pin required runtime tool '{packageId}'."

            let version = entry.GetProperty("version").GetString()

            if
                String.IsNullOrWhiteSpace version
                || not (Regex.IsMatch(version, "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$"))
            then
                invalidOp $"Runtime tool '{packageId}' must use one exact SemVer; got '{version}'."

            let commands =
                entry.GetProperty("commands").EnumerateArray()
                |> Seq.map _.GetString()
                |> Seq.toArray

            if commands <> [| command |] then
                invalidOp $"Runtime tool '{packageId}' must expose exactly the '{command}' command."

            if entry.GetProperty("rollForward").GetBoolean() then
                invalidOp $"Runtime tool '{packageId}' must set rollForward=false."

            { PackageId = packageId
              Version = version
              Command = command })

    let dotnetToolArgs (verb: string) (pin: RuntimeToolPin) =
        [ "tool"
          verb
          "-g"
          pin.PackageId
          "--version"
          pin.Version
          "--allow-downgrade" ]

    let globalToolsDirectory () =
        let cliHome =
            Environment.GetEnvironmentVariable("DOTNET_CLI_HOME")
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultWith (fun () -> Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))

        Path.Combine(cliHome, ".dotnet", "tools")

    /// Fantomas.Client 0.9.x invokes `dotnet fantomas --daemon`. The current
    /// Fantomas package exposes only a `fantomas` tool command, so a sibling
    /// `dotnet-fantomas` shim is required for the dotnet driver to resolve it.
    let ensureFantomasDotnetAlias () =
        let directory = globalToolsDirectory ()
        let extension = if OperatingSystem.IsWindows() then ".exe" else ""
        let source = Path.Combine(directory, "fantomas" + extension)
        let destination = Path.Combine(directory, "dotnet-fantomas" + extension)

        if not (File.Exists source) then
            invalidOp $"The exact Fantomas install did not create its expected shim: {source}"

        let temporary = destination + $".tmp-{Guid.NewGuid():N}"

        try
            if OperatingSystem.IsWindows() then
                File.Copy(source, temporary, true)
            else
                File.CreateSymbolicLink(temporary, Path.GetFileName(source)) |> ignore

            File.Move(temporary, destination, true)
        finally
            if File.Exists temporary then
                File.Delete temporary

let private ensureDotnetGlobalTool (pin: RuntimeToolPin) =
    let updateCode, _, updateErr =
        runProcess "dotnet" (RuntimeToolManifest.dotnetToolArgs "update" pin)

    if updateCode = 0 then
        Console.Error.WriteLine($"[bootstrap] updated {pin.PackageId} to exact {pin.Version}")
        true
    else
        let installCode, _, installErr =
            runProcess "dotnet" (RuntimeToolManifest.dotnetToolArgs "install" pin)

        if installCode = 0 then
            Console.Error.WriteLine($"[bootstrap] installed {pin.PackageId} exact {pin.Version}")
            true
        else
            Console.Error.WriteLine($"[bootstrap] failed for {pin.PackageId} {pin.Version}")

            if not (String.IsNullOrWhiteSpace(updateErr)) then
                Console.Error.WriteLine(updateErr)

            if not (String.IsNullOrWhiteSpace(installErr)) then
                Console.Error.WriteLine(installErr)

            false

let private bootstrapTools () =
    try
        let installed =
            RuntimeToolManifest.loadPinnedRuntimeTools ()
            |> List.map ensureDotnetGlobalTool
            |> List.forall id

        if installed then
            RuntimeToolManifest.ensureFantomasDotnetAlias ()

        installed
    with ex ->
        Console.Error.WriteLine($"[bootstrap] {ex.Message}")
        false

let private applyCliOverrides (argv: string array) =
    let rec loop index projectPath =
        if index >= argv.Length then
            Start { ProjectPath = projectPath }
        else
            match argv[index] with
            | "--project"
            | "-p" ->
                if index + 1 >= argv.Length then
                    Fail "--project requires a value."
                else
                    loop (index + 2) (Some argv[index + 1])
            | "--fsac-command" ->
                if index + 1 >= argv.Length then
                    Fail "--fsac-command requires a value."
                else
                    Environment.SetEnvironmentVariable("FSAC_COMMAND", argv[index + 1])
                    loop (index + 2) projectPath
            | "--fsac-args" ->
                if index + 1 >= argv.Length then
                    Fail "--fsac-args requires a value."
                else
                    Environment.SetEnvironmentVariable("FSAC_ARGS", argv[index + 1])
                    loop (index + 2) projectPath
            | "--help"
            | "-h" ->
                ShowHelp
                    "Usage: fslangmcp [--project <path-to-fsproj|sln|slnx|directory>] [--fsac-command <cmd>] [--fsac-args \"...\"] [--bootstrap-tools] [--version]"
            | "--version" -> ShowVersion
            | "--bootstrap-tools" -> BootstrapTools
            | unknown -> Fail $"Unknown argument: %s{unknown}"

    let projectFromEnvironment =
        Environment.GetEnvironmentVariable("FSA_PROJECT_PATH")
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    loop 0 projectFromEnvironment

// ─── Entry point ───────────────────────────────────────────────────────────────

/// #192 hardening. Deliberately narrow, and the split matters:
///
///   * TaskScheduler.UnobservedTaskException is genuinely log-and-survive. Marking
///     the exception observed keeps a faulted background task from becoming fatal
///     even if a host ever enables ThrowUnobservedTaskExceptions.
///   * AppDomain.CurrentDomain.UnhandledException CANNOT rescue the process on
///     .NET — by then the runtime has already decided to terminate. It only buys a
///     named cause on stderr instead of an agent seeing a bare "connection lost".
///
/// What neither covers: a dependency calling Environment.Exit/FailFast, a stack
/// overflow, or a child process dying (the actual #192 mechanism — that one is
/// handled by SdkPreflight, not here).
let private installHostFaultDiagnostics () =
    TaskScheduler.UnobservedTaskException.Add(fun args ->
        args.SetObserved()
        Console.Error.WriteLine($"[unobserved-task] %s{args.Exception.GetBaseException().Message}"))

    AppDomain.CurrentDomain.UnhandledException.Add(fun args ->
        let description =
            match args.ExceptionObject with
            | :? exn as ex -> $"%s{ex.GetType().Name}: %s{ex.Message}\n%s{ex.StackTrace}"
            | other -> $"%O{other}"

        Console.Error.WriteLine($"[unhandled] terminating=%b{args.IsTerminating} %s{description}"))

let private mainCore argv =
    match applyCliOverrides argv with
    | BootstrapTools -> if bootstrapTools () then 0 else 1
    | ShowVersion ->
        Console.WriteLine(FsLangMcp.Version.current)
        0
    | ShowHelp message ->
        Console.WriteLine(message)
        0
    | Fail message ->
        Console.Error.WriteLine(message)
        1
    | Start startOptions ->
        installHostFaultDiagnostics ()
        let fcsBridge = new FcsBridge()
        let projInfoTimeout = timeoutFromEnv "FSLANGMCP_PROJ_INFO_TIMEOUT_MS" 120_000

        let projectOptionsTelemetry () : ProjectOptionsTelemetry =
            { LoadAttempts = fcsBridge.ProjectOptionsLoadCount
              StaleReloads = fcsBridge.ProjectOptionsStaleReloadCount
              CacheValidations = fcsBridge.ProjectOptionsCacheValidationCount
              InFlight = fcsBridge.ProjectOptionsInFlightCount }

        let waitForProjInfo operationName projectPath (work: Task<Result<'T, string>>) =
            task {
                try
                    return! work.WaitAsync(projInfoTimeout)
                with :? TimeoutException ->
                    return
                        Error
                            $"%s{operationName} timed out after %d{int64 projInfoTimeout.TotalMilliseconds} ms for '%s{projectPath}'."
            }

        let getEvaluatedProjectSnapshot projectPath =
            fcsBridge.GetEvaluatedProjectSnapshot(projectPath)
            |> waitForProjInfo "MSBuild project evaluation" projectPath

        let probeProjectOptions projectPath =
            fcsBridge.ProbeProjectOptions(projectPath)
            |> waitForProjInfo "FCS project-options probe" projectPath

        let evaluatedSourceFilesProvider
            (projectPath: string)
            : Task<Result<string array, string>> =
            task {
                let! result = getEvaluatedProjectSnapshot projectPath

                return
                    result
                    |> Result.map (fun snapshot ->
                        snapshot.Files |> List.map (fun file -> file.Path) |> List.toArray)
            }

        use bridge =
            new FsAutoCompleteBridge(evaluatedSourceFilesProvider = evaluatedSourceFilesProvider)

        use fcsGate = new SemaphoreSlim(readPositiveIntEnv "FSLANGMCP_MAX_CONCURRENT_FCS" 2)
        // FSAC owns one mutable workspace. Keep the public gate fixed at one; the bridge
        // also serializes internally because some FCS orchestrators call LSP directly.
        use lspGate = new SemaphoreSlim(1, 1)

        let setProjectAndRefresh (args: SetProjectArgs) : Task<JsonNode> =
            task {
                InstallationHealth.ensureCurrent ()
                let! result = bridge.SetProject args

                let succeeded =
                    match result["status"] with
                    | :? JsonValue as value ->
                        try
                            value.GetValue<string>() = "ok"
                        with _ ->
                            false
                    | _ -> false

                if succeeded then
                    // A rejected restartLsp=false transition must not invalidate the
                    // still-active context. Only accepted transitions clear analysis.
                    fcsBridge.ClearAnalysisCaches()

                    // Enrich readiness.projectOptions by probing the first evaluated
                    // project. CLI preload and the MCP tool share this exact coordinator.
                    match result["result"] with
                    | :? JsonObject as resultObj ->
                        let probeTarget =
                            match resultObj["loadedProjects"] with
                            | :? JsonArray as arr when arr.Count > 0 ->
                                match arr[0] with
                                | null -> None
                                | node ->
                                    let value = node.GetValue<string>()
                                    if String.IsNullOrWhiteSpace value then None else Some value
                            | _ -> None

                        match probeTarget with
                        | Some path ->
                            let! probe = probeProjectOptions path

                            match resultObj["readiness"] with
                            | :? JsonObject as readinessObj ->
                                match probe with
                                | Ok info ->
                                    readinessObj["projectOptions"] <- jbool true

                                    if
                                        FsLangMcp.Types.ReferenceResolution.looksUnrestored
                                            info.ReferencesExisting
                                            info.ReferencesTotal
                                    then
                                        readinessObj["restoreStatus"] <- jstr "unrestored"

                                        readinessObj["restoreHint"] <-
                                            jstr
                                                "external references unresolved — run dotnet restore && dotnet build before using FCS tools"
                                | Error error ->
                                    readinessObj["projectOptions"] <- jbool false
                                    readinessObj["projectOptionsError"] <- jstr error
                            | _ -> ()
                        | None -> ()

                        match currentThreadWarning (projectOptionsTelemetry ()) with
                        | Some warning -> resultObj["runtimeWarning"] <- warning
                        | None -> ()
                    | _ -> ()

                return result
            }

        match startOptions.ProjectPath with
        | Some projectPath ->
            let preload =
                setProjectAndRefresh
                    { projectPath = projectPath
                      workspacePath = None
                      restartLsp = Some true }
                |> fun operation -> operation.GetAwaiter().GetResult()

            let ready =
                match preload["status"], preload["result"] with
                | (:? JsonValue as status), (:? JsonObject as resultObj) when status.GetValue<string>() = "ok" ->
                    match resultObj["readiness"] with
                    | :? JsonObject as readiness ->
                        let readBool (key: string) =
                            match readiness[key] with
                            | :? JsonValue as value ->
                                let mutable parsed = false
                                value.TryGetValue(&parsed) && parsed
                            | _ -> false

                        readBool "lsp" && readBool "projectOptions"
                    | _ -> false
                | _ -> false

            if not ready then
                invalidOp $"--project preload failed: {preload.ToJsonString()}"
        | None -> ()

        let versionResponse () : Task<JsonNode> =
            Task.FromResult(
                jobj
                    [ "status", jstr "ok"
                      "fslangmcpVersion", jstr FsLangMcp.Version.current
                      "productName", jstr FsLangMcp.Version.productName ]
                :> JsonNode
            )

        let server =
            mcpServer {
                name "fsharp-fsautocomplete"
                version FsLangMcp.Version.current

                tool (
                    TypedTool.define<CompletionArgs>
                        "textDocument_completion"
                        "Raw LSP proxy to fsautocomplete textDocument/completion. Exact-position IDE primitive; requires set_project first. line/character are 0-based. Pass 'text' for unsaved content. Avoid for free-form agent flows — completion is exact-position editor IO; for symbol semantics use fcs_symbol_at_word or find(kind=position) instead."
                        (fun args (ct: CancellationToken) ->
                            toolResult (fun () -> runLimited lspGate ct (fun () -> bridge.Completion args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<CheckArgs>
                        "check"
                        "Fresh F# verdict for the active context. Bare check() returns clean|errors|unknown from an in-process type-check. Optional: scope, path, snippet, speed, severity. trusted (default) is fresh; fast uses cached FSAC. Counts reconcile: totalDiagnostics=errors+warnings+info. Project scope marks downstream consumers unchecked; workspace requires a solution or directory. Prefer for yes/no validation."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            let timeoutMs = args.timeoutMs |> Option.defaultValue 60_000 |> Some

                            toolResult (fun () ->
                                runLimitedWithTimeout fcsGate ct timeoutMs (fun remainingTimeoutMs ->
                                    let args = { args with timeoutMs = remainingTimeoutMs }
                                    Dispatcher.CheckDispatch.run fcsBridge bridge (Dispatcher.Check args))))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<SetProjectArgs>
                        "set_project"
                        "Initialize or switch the FSAC/LSP project context. Required before raw LSP proxies. Accepts .fsproj, .sln, .slnx, or directory. Waits up to 30s for workspace load and clears stale FCS analysis while retaining validated MSBuild project options. Response includes loadedProjects, readiness, restart intent, and whether a running FSAC process was actually replaced."
                        (fun args (ct: CancellationToken) ->
                            toolResult (fun () ->
                                runLimited lspGate ct (fun () -> setProjectAndRefresh args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<ProjectHealthArgs>
                        "project_health"
                        "Fast read-only preflight for one F# project. Reports whether FsLangMCP can trust semantic tooling, project options availability, source file readability, analyzer setup, test project discovery, and current LSP readiness. projectPath is optional after set_project (falls back to the active project); pass it explicitly to inspect a different .fsproj/.sln/.slnx. Does not start/switch FSAC, run compile, or run tests."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            let snapshot =
                                { ProjectPath = bridge.CurrentProjectPath
                                  WorkspaceRoot = bridge.CurrentWorkspaceRoot
                                  LoadedProjects = bridge.LoadedProjects
                                  SessionLive = bridge.IsSessionLive
                                  WorkspaceReady = bridge.IsWorkspaceReady
                                  DiagnosticsFileCount = bridge.DiagnosticsFileCount }

                            let evaluatedProject path =
                                getEvaluatedProjectSnapshot path |> Async.AwaitTask

                            toolResult (fun () ->
                                runLimited fcsGate ct (fun () ->
                                    createReport args snapshot evaluatedProject |> Async.StartAsTask)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FSharpProjectInspectArgs>
                        "fsharp_project_inspect"
                        "Read-only .fsproj inspection for agents. Prefer over textual reads of `.fsproj` — handles MSBuild evaluation correctly. Returns project identity, compile order, package/project references, signature/implementation pairing, and shared scan filtering summary. `projectPath` is optional after `set_project`. Does not build, restore, test, or edit files."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            let evaluatedProject path =
                                getEvaluatedProjectSnapshot path |> Async.AwaitTask

                            toolResult (fun () ->
                                runLimited fcsGate ct (fun () ->
                                    inspectProject args evaluatedProject |> Async.StartAsTask)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsReferencedSymbolsArgs>
                        "fcs_referenced_symbols"
                        "Substring search across the project's referenced assemblies (NuGet + framework) by DisplayName or FullName (case-insensitive). Prefer `fcs_nuget_types` when you already know the exact assembly name; use `find` for project-local symbols. Reports assembly, kind, accessibility, isObsolete. `includeNonPublic=true` for internals. Paginated; default 200, max 1000. First call triggers ParseAndCheckProject. Details: docs/tools-detailed.md#fcs_referenced_symbols."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (fun () -> runLimited fcsGate ct (fun () -> fcsBridge.ReferencedSymbols args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsSuggestOpenArgs>
                        "fcs_suggest_open"
                        "Given an unresolved symbol name (FS0039), returns ranked `open` directive candidates — project-local first, then referenced assemblies. Use when an agent sees 'X is not defined' to get the right namespace instantly. Set includeReferences=false for project-only. Caveat: openPath is empty for global-namespace symbols; doesn't deduplicate the same name across multiple assemblies."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (fun () -> runLimited fcsGate ct (fun () -> fcsBridge.SuggestOpen args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsNugetTypesArgs>
                        "fcs_nuget_types"
                        "Enumerate all types in one referenced assembly. `packageId` accepts the NuGet package id OR an assembly SimpleName it ships (exact, case-insensitive, never a prefix); the two often differ, and multi-assembly packages resolve fully. Prefer `fcs_referenced_symbols` for substring search across assemblies. Entry: displayName, fullName, kind, accessibility, isObsolete. Paginated; default 500, max 2000. A miss adds `hint` + `candidatePackages`. Details: docs/tools-detailed.md#fcs_nuget_types."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (fun () -> runLimited fcsGate ct (fun () -> fcsBridge.NugetTypes args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsNugetMembersArgs>
                        "fcs_nuget_members"
                        "Members of one type from a referenced assembly. packageId accepts a NuGet package id or assembly SimpleName; use fcs_nuget_types to discover names. Rows include signature and constraints, exact CLR accessibility when metadata is available, isAbstract, genericParameters, obsolete/XML docs. Protected API is included by default; includeNonPublic widens. Paginated (500 default, 2000 max); misses include routing hints. Details: docs/tools-detailed.md#fcs_nuget_members."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (fun () -> runLimited fcsGate ct (fun () -> fcsBridge.NugetMembers args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsFileOutlineArgs>
                        "fcs_file_outline"
                        "Compact outline for one F# file. Start with summaryOnly=true (default): container headers and attributes, member counts, CustomOperation index, and parse/check diagnostics. Those diagnostics are a per-file signal, not a project verdict. summaryOnly=false adds signatures/accessibility/attributes; oversized output downgrades to summary. Filters locals/noise by default. Use fcs_project_outline for a project or find(kind=\"symbol\") for search."
                        (fun args (ct: CancellationToken) ->
                            toolResult (fun () -> runLimited fcsGate ct (fun () -> fcsBridge.FileOutline args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsMakeInternalVisibleArgs>
                        "fcs_make_internal_visible"
                        "Drop the `private` keyword from a declaration at `(line, character)`. Returns a non-destructive workspace edit `{ status, edits, appliedPreview, originalLineText }` — does NOT write the file. Use before tests need to call internals. Returns `{ status: 'no_action', reason }` on no symbol or no recognized modifier. Supported forms and Variant B status: docs/tools-detailed.md#fcs_make_internal_visible."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (fun () -> runLimited fcsGate ct (fun () -> fcsBridge.MakeInternalVisible args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FindArgs>
                        "find"
        "F# semantic search across solution projects: definitions/references, field sites, and member calls. Bare find(query) returns bounded snippets; contextLines adds up to 8 lines per side. Production JSON is capped at 60,000 UTF-16 code units, and cursors advance by delivered sites. Narrow with kind (symbol|members|field|definition|position), scope, path, or projectPath. Member calls: kind=members + member=Name. Field impact: kind=field + includeSiteTypes. scopeNote reports breadth."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            let timeoutMs = args.timeoutMs |> Option.defaultValue 120_000

                            toolResult (fun () ->
                                if timeoutMs < 0 then
                                    runLimitedWithTimeout fcsGate ct (Some timeoutMs) (fun _ ->
                                        Dispatcher.FindDispatch.run fcsBridge bridge (Dispatcher.Find args))
                                else
                                    task {
                                        let deadline = FindRequestDeadline(timeoutMs)

                                        let! result =
                                            runLimitedWithFindDeadlineRetained
                                                fcsGate
                                                ct
                                                deadline
                                                (fun sharedDeadline retainUntil ->
                                                    Dispatcher.FindDispatch.runWithinDeadline
                                                        fcsBridge
                                                        bridge
                                                        sharedDeadline
                                                        ct
                                                        retainUntil
                                                        (Dispatcher.Find args))

                                        let isAdmissionTimeout =
                                            match result["errorKind"] with
                                            | null -> false
                                            | errorKind ->
                                                errorKind.GetValue<string>() = "fcs_admission_timeout"

                                        if isAdmissionTimeout then
                                            return
                                                FindDeadlineResponse.beforeDiscovery
                                                    args
                                                    deadline
                                                    "admission"
                                                    "timed_out"
                                                    "fcs_admission_timeout"
                                                    (result["message"].GetValue<string>())
                                                |> FindResponseBudget.guardFinalResponse
                                        else
                                            return result
                                    }))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsSymbolAtWordArgs>
                        "fcs_symbol_at_word"
                        "Tolerant FCS symbol lookup for agent workflows. Accepts a line plus word/occurrence, finds the candidate span, and returns symbol identity, kind, type string, definition range, and optional documentation. Prefer over exact-position hover/type queries."
                        (fun args (ct: CancellationToken) ->
                            toolResult (fun () -> runLimited fcsGate ct (fun () -> fcsBridge.SymbolAtWord args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsProjectOutlineArgs>
                        "fcs_project_outline"
                        "Whole-project structural overview over filtered compile files. Default timeoutMs=60000 covers queue admission, project evaluation, and file scans. Read status plus coverage before treating absence as exhaustive; incomplete filtered discovery emits no continuation cursor. Skips generated/build artifacts. Prefer find for symbol sites and fcs_file_outline for one file."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            let timeoutMs = args.timeoutMs |> Option.defaultValue 60_000 |> Some

                            toolResult (fun () ->
                                task {
                                    let! result =
                                        runLimitedWithTimeoutRetained
                                            fcsGate
                                            ct
                                            timeoutMs
                                            (fun remainingTimeoutMs retainUntil ->
                                                let args =
                                                    { args with
                                                        timeoutMs = remainingTimeoutMs }

                                                fcsBridge.ProjectOutlineWithinDeadline(args, ct, retainUntil))

                                    return
                                        normalizeProjectOutlineAdmissionResult
                                            (timeoutMs |> Option.defaultValue 60_000)
                                            result
                                }))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsSignatureHelpArgs>
                        "fcs_signature_help"
                        "Low-level exact-position FCS signature help. Returns overloads/parameters around a call site. line/character are 0-based. Pass projectPath/projectOptions and 'text' when available."
                        (fun args (ct: CancellationToken) ->
                            toolResult (fun () -> runLimited fcsGate ct (fun () -> fcsBridge.SignatureHelp args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<PositionArgs>
                        "fsharp_signature_data"
                        "Structured FSAC signature help via fsharp/signatureData. Requires set_project and an exact call-site position. Use this when FCS fallback is insufficient or when validating FSAC's current workspace view."
                        (fun args (ct: CancellationToken) ->
                            toolResult (fun () -> runLimited lspGate ct (fun () -> bridge.SignatureData args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FormattingArgs>
                        "textDocument_formatting"
                        "Raw LSP formatting proxy via fsautocomplete/Fantomas. Requires set_project first. Returns formatted text and edits; it does not write to disk. Pass 'text' for unsaved content."
                        (fun args (ct: CancellationToken) ->
                            toolResult (fun () -> runLimited lspGate ct (fun () -> bridge.Formatting args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<CodeActionArgs>
                        "textDocument_codeAction"
                        "Raw LSP codeAction proxy at an exact position with empty diagnostic context. Requires set_project first. Useful for debugging FSAC; prefer future diagnostics-to-fix workflows for agent repairs. Pass 'text' for unsaved content."
                        (fun args (ct: CancellationToken) ->
                            toolResult (fun () -> runLimited lspGate ct (fun () -> bridge.CodeAction args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<RenameArgs>
                        "textDocument_rename"
                        "Raw LSP semantic rename at an exact position. Requires `set_project` first. Prefer over textual rename — handles shadowing and aliased opens safely. Returns raw WorkspaceEdit; needs a precise target. Pass `text` for unsaved content."
                        (fun args (ct: CancellationToken) ->
                            toolResult (fun () -> runLimited lspGate ct (fun () -> bridge.Rename args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsGetProjectOptionsArgs>
                        "fcs_get_project_options"
                        "Diagnostic helper: get FSharp compiler OtherOptions for a .fsproj via proj-info. projectPath is optional after set_project (falls back to the active project); pass it explicitly to inspect a different one."
                        (fun args (ct: CancellationToken) ->
                            let resolved =
                                args.projectPath
                                |> Option.orElse bridge.CurrentProjectPath
                                |> Option.filter (System.String.IsNullOrWhiteSpace >> not)

                            match resolved with
                            | None ->
                                toolResult (fun () ->
                                    Task.FromException<JsonNode>(
                                        ArgumentException
                                            "projectPath is required. Either pass it explicitly or call set_project first to establish a default."
                                    ))
                            | Some path ->
                                toolResult (fun () -> runLimited fcsGate ct (fun () -> runProjInfoAsync path)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsCheckCompileOrderArgs>
                        "fcs_check_compile_order"
                        "Detect F#'s file-ordering gotcha: a symbol used before the file that DEFINES it in <Compile> order reads as FS0039 'not defined' though it exists. Returns { symbol, definedIn, usedIn{file,compileIndex,range,lineText}, fix } so an agent reorders the .fsproj. Use when `check` reports FS0039 'X is not defined' to tell a compile-ORDER problem from a missing `open` (fcs_suggest_open handles that). projectPath optional after set_project; `symbol` narrows to one name."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (fun () -> runLimited fcsGate ct (fun () -> fcsBridge.CheckCompileOrder args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FslangmcpVersionArgs>
                        "fslangmcp_version"
                        "Returns the installed FsLangMCP product version and name. Zero-arg (pass {}). Same value is also surfaced in the set_project response (fslangmcpVersion field) and the fsharp_runtime_status response. Use this tool when filing UX feedback so reports can be matched to a specific release of the MCP server. Pure: no project context required, no side effects, no caches read."
                        (fun _ (_ct: CancellationToken) -> toolResult versionResponse)
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<DiagnosticFixesArgs>
                        "fcs_diagnostic_fixes"
                        "Fetch a file's diagnostics, then request code-action fixes for each and group them per diagnostic: range, severity, code, message, fixes [{title, kind, editSummary}], plus diagnosticCount/fixCount. Agent-friendly wrapper over raw textDocument_codeAction: supplies the diagnostic context the raw proxy leaves empty and groups the fixes. Requires set_project first. Pass line(+character) to narrow to one position, else all; pass text for unsaved content."
                        (fun args (ct: CancellationToken) ->
                            toolResult (fun () -> runLimited lspGate ct (fun () -> bridge.DiagnosticFixes args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<RuntimeStatusArgs>
                        "fsharp_runtime_status"
                        "Read-only observational snapshot of FsLangMCP: managed-heap and GC counters, OS-visible and thread-pool thread counts, project-options load/reload counters, FCS checker/cache state, and FSAC child working set. At an anomalous OS thread count it emits a heuristic warning and parent-process restart recommendation. Never triggers GC, walks the heap, suspends threads, or attaches diagnostics."
                        (fun args (_ct: CancellationToken) ->
                            toolResult (fun () ->
                                Task.FromResult(
                                    buildSnapshotWithTelemetry
                                        args
                                        fcsBridge.CheckerConfig
                                        bridge.FsacProcess
                                        (projectOptionsTelemetry ())
                                )))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsExplainDiagnosticArgs>
                        "fcs_explain_diagnostic"
                        "Explain an F# compiler diagnostic in plain language with repair context: title, explanation, likelyCauses, repairHints, relatedTools. Pass `code` (\"FS0039\"), `errorNumber` (39), or path+line+character to auto-fetch it via FCS. Use this when `check` reports an FS error you need to turn into a fix — feed it check's errorNumberText. Curated map of ~25 common diagnostics; pass the raw `message` to enrich hints (FS0039 → fcs_suggest_open). Unknown codes return status=unknown_code."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (fun () -> runLimited fcsGate ct (fun () -> fcsBridge.ExplainDiagnostic args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsTestsForSymbolArgs>
                        "fcs_tests_for_symbol"
                        "Test-project references to a symbol, with enclosing test when safe. Prefer over find for test coverage; find returns all uses. Excludes definitions. Paginates with maxResults/cursor and uses one timeoutMs budget (120000 default). testCount/siteCount are reference sites; uniqueTestCount is distinct enclosing tests. Inspect coverage.complete and perProject before trusting zero. A production .fsproj reuses the active solution when available; otherwise an incomplete zero gives a widening hint."
                        (fun args (ct: CancellationToken) ->
                            let activeProjectPath = bridge.CurrentProjectPath

                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse activeProjectPath }

                            let timeoutMs = args.timeoutMs |> Option.defaultValue 120_000 |> Some

                            toolResult (fun () ->
                                runLimitedWithTimeout fcsGate ct timeoutMs (fun remainingTimeoutMs ->
                                    let args = { args with timeoutMs = remainingTimeoutMs }
                                    fcsBridge.TestsForSymbol(args, ?activeProjectPath = activeProjectPath))))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<RenamePreviewArgs>
                        "fcs_rename_preview"
                        "Preview a semantic rename's full impact WITHOUT applying it — non-destructive, writes nothing. Runs the same FSAC machinery as `textDocument_rename` but returns edits grouped by file, each with originalLineText and previewLineText, plus totalEdits, fileCount, and a crossProject flag. Use it to inspect blast radius before `textDocument_rename` applies the change. Requires `set_project`. Returns `no_symbol` when the position has no renamable symbol. Pass `text` for unsaved buffers."
                        (fun args (ct: CancellationToken) ->
                            toolResult (fun () -> runLimited lspGate ct (fun () -> bridge.RenamePreview args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsPublicApiArgs>
                        "fcs_public_api"
                        "Emit an F# project's public API surface: every public type and its public members with signatures, sorted stably by fullName then member name so two version snapshots diff cleanly. Prefer over `fcs_project_outline` for API-stability/breaking-change diffs — public-only (includeInternal=true adds internals), signature-complete, deterministic order. projectPath optional after set_project. Narrow with namespaceFilter (substring on FullName); paginated via maxResults + cursor."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (fun () -> runLimited fcsGate ct (fun () -> fcsBridge.PublicApi args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsRefactorImpactArgs>
                        "fcs_refactor_impact"
                        "Preview a change's blast radius + a verify checklist WITHOUT editing. Orchestrates find (cross-project use sites), fcs_tests_for_symbol, fcs_check_compile_order (kind=move) and fcs_public_api (kind=signature|delete, when public) into { target, impact, tests, compileOrder?, apiSurface?, verify[] }. Pass `symbol` or path+line+character; kind=rename|signature|move|delete|auto. Use before a rename/move/delete; prefer `fcs_rename_preview` for the exact edits, this for project-wide impact."
                        (fun args (ct: CancellationToken) ->
                            let activeProjectPath = bridge.CurrentProjectPath

                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse activeProjectPath }

                            toolResult (fun () ->
                                runLimited fcsGate ct (fun () ->
                                    fcsBridge.RefactorImpact(
                                        args,
                                        (fun rp -> bridge.RenamePreview rp),
                                        ?activeProjectPath = activeProjectPath
                                    ))))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsSignatureStatusArgs>
                        "fcs_signature_status"
                        "Report the .fsi-vs-impl public-surface gap for one .fs WITHOUT editing: type-checks the impl with its sibling .fsi stripped, then diffs — members public in the impl but missing from the .fsi (silently hidden) → missingFromSig; .fsi entries with no impl match → staleInSig, each with a val/type signaturePreview. No .fsi? lists the would-be signature. Use for .fsi drift (members hidden/stale); prefer `fcs_public_api` for the whole public surface. projectPath falls back to set_project."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (fun () -> runLimited fcsGate ct (fun () -> fcsBridge.SignatureStatus args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsReviewScanArgs>
                        "fcs_review_scan"
                        "Scan F# source for review CANDIDATES from the untyped AST — spots to eyeball, not a linter. Categories: match_wildcard, try_with, raise_or_failwith, mutable_binding, blocking_call, cast_or_box, reflection, large_function. Target: `path` (one file) or `projectPath` (falls back to set_project); narrow with `categories`, cap with `maxResults`. Parse-only, writes nothing; candidates carry range, lineText, note, plus counts.byCategory. Missing compiled files → `unresolvedFiles`, status `partial`."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (fun () -> runLimited fcsGate ct (fun () -> fcsBridge.ReviewScan args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsDeadCodeArgs>
                        "fcs_dead_code"
                        "List likely-unused F# symbols as cleanup candidates — a conservative cleanup pass (candidates, not deletions); use `find` to verify each candidate's real usage before removing. Sweeps the project (GetAllUsesOfAllSymbols) and flags private/internal value & function bindings whose only use is their own definition. Public is excluded (includePublic=true adds it); skips compiler-generated, [<EntryPoint>], overrides/interface impls, ctors. Always emits caveats. projectPath falls back to set_project."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (fun () -> runLimited fcsGate ct (fun () -> fcsBridge.DeadCode args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsCreateFilePlanArgs>
                        "fcs_create_file_plan"
                        "Plan WHERE a new .fs file belongs WITHOUT creating it — read-only, writes nothing. Loads the resolved <Compile> order, recommends an insertion index (right after `afterFile`, else namespace-neighbour/end), infers the namespace/module convention from neighbours, and emits the exact <Compile Include=...> edit plus a dependency note (a file may only reference EARLIER files). Use before adding an .fs file to pick the right <Compile> position; pair with `fcs_check_compile_order` after."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (fun () -> runLimited fcsGate ct (fun () -> fcsBridge.CreateFilePlan args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsAnalyzerDiagnosticsArgs>
                        "fcs_analyzer_diagnostics"
                        "Report F# ANALYZER diagnostics (not compiler diagnostics), grouped: analyzersConfigured, analyzerPackages, diagnostics [{analyzer, code, severity, message, file, range}], counts {byAnalyzer, bySeverity}. Detects analyzer config like project_health, runs the fsharp-analyzers CLI when available, parses its SARIF; none configured → no_analyzers. Use to read analyzer DIAGNOSTICS; project_health reports whether analyzers are CONFIGURED. severity filters; projectPath falls back to set_project."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (fun () -> runLimited fcsGate ct (fun () -> fcsBridge.AnalyzerDiagnostics args)))
                    |> unwrapResult
                )

                tool (
                    TypedTool.define<FcsAnalyzerSetupPreviewArgs>
                        "fcs_analyzer_setup_preview"
                        "Plan what to add to enable F# analyzers WITHOUT applying it — read-only, writes nothing. Reads the .fsproj + Directory.Build.props/.targets + dotnet-tools.json, diffs current wiring against the required set: analyzer package refs + GeneratePathProperty, FSharp.Analyzers.Build, the FSharpAnalyzersOtherFlags property, a local fsharp-analyzers manifest. Emits each gap as an exact XML/JSON snippet + reason. Use this to set analyzers up; pair with fcs_analyzer_diagnostics to read diagnostics after."
                        (fun args (ct: CancellationToken) ->
                            let args =
                                { args with projectPath = args.projectPath |> Option.orElse bridge.CurrentProjectPath }

                            toolResult (fun () -> runLimited fcsGate ct (fun () -> fcsBridge.AnalyzerSetupPreview args)))
                    |> unwrapResult
                )

            }

        try
            // Generic Host watches appsettings.json by default. In restricted hosts the
            // file-watcher token can already be cancelled, making ChangeToken.OnChange
            // re-register synchronously forever before MCP can read `initialize`.
            let reloadConfigKey = "DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE"
            let previousReloadConfig = Environment.GetEnvironmentVariable(reloadConfigKey)

            let serverTask =
                try
                    Environment.SetEnvironmentVariable(reloadConfigKey, "false")
                    FsLangMcp.McpHost.runToolOnly FsLangMcp.Version.current server
                finally
                    // Server.run creates the Generic Host synchronously before returning
                    // its pending task. Restore immediately so FSAC children inherit the
                    // caller's environment rather than this server-startup workaround.
                    Environment.SetEnvironmentVariable(reloadConfigKey, previousReloadConfig)

            serverTask.GetAwaiter().GetResult()
            0
        with ex ->
            Console.Error.WriteLine($"Fatal error: %s{ex.Message}")
            1

[<EntryPoint>]
let main argv =
    if argv.Length > 0 && argv[0] = InternalProcessSessionWrapperArgument then
        if argv.Length < 2 then
            Console.Error.WriteLine("The internal process-session wrapper requires a command.")
            64
        else
            runUnixSessionWrapper argv[1] argv[2..]
    elif argv.Length > 0 && argv[0] = ProjectEvaluation.InternalArgument then
        if argv.Length <> 2 then
            Console.Error.WriteLine("The internal project-evaluation helper requires exactly one project path.")
            64
        else
            ProjectEvaluation.runHelper argv[1]
    else
        try
            mainCore argv
        with ex ->
            Console.Error.WriteLine($"Fatal error: %s{ex.Message}")
            1
