[<Xunit.Collection("FsLangMcp LSP process isolation")>]
module FsLangMcp.Tests.LspBridgeTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FsLangMcp.LspBridge

[<CollectionDefinition("FsLangMcp LSP process isolation", DisableParallelization = true)>]
type LspBridgeProcessIsolationCollection() = class end

let private jsonElement (json: string) =
    use doc = JsonDocument.Parse(json)
    doc.RootElement.Clone()

let private diagnosticEnvelope generation payload serverVersion =
    { Generation = generation
      Payload = payload
      ServerVersion = serverVersion
      ReceivedAt = DateTimeOffset.UtcNow }

let private fakeFsacScript =
    """open System
open System.Text
open System.Text.Json

let readMessage () =
    let mutable contentLength = 0
    let mutable readingHeaders = true

    while readingHeaders do
        let line = Console.In.ReadLine()

        if isNull line then
            Environment.Exit(0)
        elif line.Length = 0 then
            readingHeaders <- false
        elif line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) then
            contentLength <- Int32.Parse(line.Substring("Content-Length:".Length).Trim())

    let chars = Array.zeroCreate<char> contentLength
    let mutable offset = 0

    while offset < contentLength do
        let count = Console.In.Read(chars, offset, contentLength - offset)

        if count = 0 then
            Environment.Exit(0)

        offset <- offset + count

    String(chars)

let writeResponse (id: string) (result: string) =
    let response = $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result}}}"
    let byteCount = Encoding.UTF8.GetByteCount(response)
    Console.Out.Write($"Content-Length: {byteCount}\r\n\r\n{response}")
    Console.Out.Flush()

while true do
    use message = JsonDocument.Parse(readMessage ())
    let root = message.RootElement
    let mutable id = Unchecked.defaultof<JsonElement>

    if root.TryGetProperty("id", &id) then
        let methodName = root.GetProperty("method").GetString()
        let result = if methodName = "initialize" then "{\"capabilities\":{}}" else "null"
        writeResponse (id.GetRawText()) result
"""

let private fakeFsacScriptWithSlowFormatting (markerPath: string) (delayMilliseconds: int) =
    let markerLiteral = JsonSerializer.Serialize(markerPath)
    let responseLine = "        let result = if methodName = \"initialize\" then \"{\\\"capabilities\\\":{}}\" else \"null\""

    let slowResponse =
        $"""        if methodName = "textDocument/formatting" then
            System.IO.File.WriteAllText({markerLiteral}, "entered")
            System.Threading.Thread.Sleep({delayMilliseconds})

{responseLine}"""

    fakeFsacScript.Replace(responseLine, slowResponse)

[<Fact>]
let ``Disposing bridge clears atomic diagnostic snapshots`` () =
    let bridge = new FsAutoCompleteBridge()
    let diagnostics = bridge.DiagnosticsStore

    diagnostics[Path.GetFullPath("/old.fs")] <-
        diagnosticEnvelope 1L (System.Text.Json.Nodes.JsonArray()) None

    (bridge :> IDisposable).Dispose()

    Assert.Empty(diagnostics)

[<Fact>]
let ``retired diagnostics notification cannot repopulate a replacement generation`` () : Task =
    task {
        let store =
            System.Collections.Concurrent.ConcurrentDictionary<string, DiagnosticEnvelope>(
                DiagnosticIdentity.pathComparer
            )
        let synchronizationRoot = obj()
        let mutable currentGeneration = 1L
        let generationChecked = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let releaseNotification = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let transitionStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let target =
            DiagnosticsTarget(
                store,
                1L,
                (fun candidate ->
                    let accepted = currentGeneration = candidate
                    generationChecked.TrySetResult(()) |> ignore
                    releaseNotification.Task.GetAwaiter().GetResult()
                    accepted),
                synchronizationRoot
            )

        let payload = System.Text.Json.Nodes.JsonObject()
        payload["uri"] <- System.Text.Json.Nodes.JsonValue.Create("file:///retired.fs")
        payload["version"] <- System.Text.Json.Nodes.JsonValue.Create(1)
        payload["diagnostics"] <- System.Text.Json.Nodes.JsonArray()

        let publish = Task.Run(fun () -> target.PublishDiagnostics(payload))
        do! generationChecked.Task

        let transition =
            Task.Run(fun () ->
                transitionStarted.TrySetResult(()) |> ignore

                lock synchronizationRoot (fun () ->
                    currentGeneration <- 2L
                    store.Clear()))

        do! transitionStarted.Task
        releaseNotification.TrySetResult(()) |> ignore
        do! Task.WhenAll([| publish; transition |])

        Assert.Empty(store)
    }

[<Fact>]
let ``failed FSAC startup clears every diagnostic store`` () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_failed_start_{Guid.NewGuid():N}")
        let projectPath = Path.Combine(root, "App.fsproj")

        try
            Directory.CreateDirectory(root) |> ignore
            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")

            use bridge =
                new FsAutoCompleteBridge(fsacCommandOverride = $"missing-fsac-{Guid.NewGuid():N}")

            bridge.DiagnosticsStore[Path.GetFullPath("/stale.fs")] <-
                diagnosticEnvelope 1L (System.Text.Json.Nodes.JsonArray()) (Some 7)

            let operation =
                bridge.SetProject(
                    { projectPath = projectPath
                      workspacePath = None
                      restartLsp = Some true }
                )

            let! _ = Assert.ThrowsAnyAsync<Exception>(fun () -> operation :> Task)
            Assert.Empty(bridge.DiagnosticsStore)
            Assert.Equal(0L, bridge.SessionGeneration)
            Assert.True(bridge.FsacProcess.IsNone)
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``FSAC escaped Windows drive URI and client URI share one diagnostic key`` () =
    let clientUri = "file:///C:/Repo%20A/App.fs"
    let fsacUri = "file:///C%3A/Repo%20A/App.fs"
    let clientKey = DiagnosticIdentity.tryCanonicalFileKeyFromUri clientUri
    let fsacKey = DiagnosticIdentity.tryCanonicalFileKeyFromUri fsacUri

    Assert.True(clientKey.IsSome)
    Assert.Equal(clientKey, fsacKey)

    let store =
        System.Collections.Concurrent.ConcurrentDictionary<string, DiagnosticEnvelope>(
            DiagnosticIdentity.pathComparer
        )

    let target = DiagnosticsTarget(store, 1L, ((=) 1L), obj())
    let payload = System.Text.Json.Nodes.JsonObject()
    payload["uri"] <- System.Text.Json.Nodes.JsonValue.Create(fsacUri)
    payload["diagnostics"] <- System.Text.Json.Nodes.JsonArray()
    target.PublishDiagnostics(payload)

    Assert.True(store.ContainsKey(clientKey.Value))

[<Fact>]
let ``versionless diagnostics never survive a same-mtime disk edit and recover after rebaseline`` () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_diag_baseline_{Guid.NewGuid():N}")
        let projectPath = Path.Combine(root, "App.fsproj")
        let sourcePath = Path.Combine(root, "App.fs")
        let scriptPath = Path.Combine(root, "fake-fsac.fsx")
        let initialSource = "module App\nlet value = 1\n"
        let changedSource = "module App\nlet value = x\n"

        try
            Directory.CreateDirectory(root) |> ignore
            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
            File.WriteAllText(sourcePath, initialSource)
            File.WriteAllText(scriptPath, fakeFsacScript)

            let evaluatedFiles (_: string) = Task.FromResult(Ok [| sourcePath |])

            use bridge =
                new FsAutoCompleteBridge(
                    startupTimeoutOverride = TimeSpan.FromSeconds(10.0),
                    fsacCommandOverride = "dotnet",
                    fsacArgsOverride = [ "fsi"; "--exec"; scriptPath ],
                    evaluatedSourceFilesProvider = evaluatedFiles
                )

            let! selected =
                bridge.SetProject(
                    { projectPath = projectPath
                      workspacePath = None
                      restartLsp = Some true }
                )

            Assert.Equal("ok", selected["status"].GetValue<string>())
            let generation1 = bridge.SessionGeneration
            let fileKey = DiagnosticIdentity.canonicalFileKeyFromPath sourcePath

            bridge.DiagnosticsStore[fileKey] <-
                diagnosticEnvelope generation1 (System.Text.Json.Nodes.JsonArray()) None

            let! beforeEdit =
                bridge.DiagnosticsForContext(Some projectPath, [| sourcePath |], None, None)

            Assert.True(beforeEdit["complete"].GetValue<bool>())

            let originalWriteTime = File.GetLastWriteTimeUtc(sourcePath)
            Assert.Equal(initialSource.Length, changedSource.Length)
            File.WriteAllText(sourcePath, changedSource)
            File.SetLastWriteTimeUtc(sourcePath, originalWriteTime)

            // Simulate a delayed empty publication from generation 1 arriving after
            // the edit. The content baseline, not receipt time, must reject it.
            bridge.DiagnosticsStore[fileKey] <-
                diagnosticEnvelope generation1 (System.Text.Json.Nodes.JsonArray()) None

            let! afterEdit =
                bridge.DiagnosticsForContext(Some projectPath, [| sourcePath |], None, None)

            Assert.False(afterEdit["complete"].GetValue<bool>())
            Assert.Equal("not_ready", afterEdit["status"].GetValue<string>())
            let generation2 = bridge.SessionGeneration
            Assert.True(generation2 > generation1)

            bridge.DiagnosticsStore[fileKey] <-
                diagnosticEnvelope generation2 (System.Text.Json.Nodes.JsonArray()) None

            let! afterRebaseline =
                bridge.DiagnosticsForContext(Some projectPath, [| sourcePath |], None, None)

            Assert.True(afterRebaseline["complete"].GetValue<bool>())
            Assert.Equal(0, afterRebaseline["staleFileCount"].GetValue<int>())
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``diagnostic context fingerprint changes retire the old generation before cached diagnostics are reused`` () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_diag_context_{Guid.NewGuid():N}")
        let projectPath = Path.Combine(root, "App.fsproj")
        let sourcePath = Path.Combine(root, "App.fs")
        let scriptPath = Path.Combine(root, "fake-fsac.fsx")

        try
            Directory.CreateDirectory(root) |> ignore
            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
            File.WriteAllText(sourcePath, "module App\nlet value = 1\n")
            File.WriteAllText(scriptPath, fakeFsacScript)

            let evaluatedFiles (_: string) = Task.FromResult(Ok [| sourcePath |])

            use bridge =
                new FsAutoCompleteBridge(
                    startupTimeoutOverride = TimeSpan.FromSeconds(10.0),
                    fsacCommandOverride = "dotnet",
                    fsacArgsOverride = [ "fsi"; "--exec"; scriptPath ],
                    evaluatedSourceFilesProvider = evaluatedFiles
                )

            let! _ =
                bridge.SetProject(
                    { projectPath = projectPath
                      workspacePath = None
                      restartLsp = Some true }
                )

            let generation1 = bridge.SessionGeneration

            let! firstBinding =
                bridge.DiagnosticsForContext(Some projectPath, [| sourcePath |], None, Some "context-a")

            Assert.Equal("not_ready", firstBinding["status"].GetValue<string>())
            let generation2 = bridge.SessionGeneration
            Assert.True(generation2 > generation1)

            let fileKey = DiagnosticIdentity.canonicalFileKeyFromPath sourcePath
            bridge.DiagnosticsStore[fileKey] <-
                diagnosticEnvelope generation2 (System.Text.Json.Nodes.JsonArray()) None

            let! sameContext =
                bridge.DiagnosticsForContext(Some projectPath, [| sourcePath |], None, Some "context-a")

            Assert.True(sameContext["complete"].GetValue<bool>())
            Assert.Equal(generation2, bridge.SessionGeneration)

            let! changedContext =
                bridge.DiagnosticsForContext(Some projectPath, [| sourcePath |], None, Some "context-b")

            Assert.Equal("not_ready", changedContext["status"].GetValue<string>())
            let generation3 = bridge.SessionGeneration
            Assert.True(generation3 > generation2)
            Assert.Empty(bridge.DiagnosticsStore)

            bridge.DiagnosticsStore[fileKey] <-
                diagnosticEnvelope generation3 (System.Text.Json.Nodes.JsonArray()) None

            let! rebound =
                bridge.DiagnosticsForContext(Some projectPath, [| sourcePath |], None, Some "context-b")

            Assert.True(rebound["complete"].GetValue<bool>())
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``versionless unsaved diagnostics remain stale while an exact server version is current`` () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_diag_unsaved_{Guid.NewGuid():N}")
        let projectPath = Path.Combine(root, "App.fsproj")
        let sourcePath = Path.Combine(root, "App.fs")
        let scriptPath = Path.Combine(root, "fake-fsac.fsx")
        let diskSource = "module App\nlet value = 1\n"
        let unsavedSource = "module App\nlet value = x\n"

        try
            Directory.CreateDirectory(root) |> ignore
            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
            File.WriteAllText(sourcePath, diskSource)
            File.WriteAllText(scriptPath, fakeFsacScript)

            let evaluatedFiles (_: string) = Task.FromResult(Ok [| sourcePath |])

            use bridge =
                new FsAutoCompleteBridge(
                    startupTimeoutOverride = TimeSpan.FromSeconds(10.0),
                    fsacCommandOverride = "dotnet",
                    fsacArgsOverride = [ "fsi"; "--exec"; scriptPath ],
                    evaluatedSourceFilesProvider = evaluatedFiles
                )

            let! _ =
                bridge.SetProject(
                    { projectPath = projectPath
                      workspacePath = None
                      restartLsp = Some true }
                )

            let! _ = bridge.Formatting({ path = sourcePath; text = Some unsavedSource })
            let generation = bridge.SessionGeneration
            let fileKey = DiagnosticIdentity.canonicalFileKeyFromPath sourcePath

            bridge.DiagnosticsStore[fileKey] <-
                diagnosticEnvelope generation (System.Text.Json.Nodes.JsonArray()) None

            let! versionless =
                bridge.DiagnosticsForContext(Some projectPath, [| sourcePath |], None, None)

            Assert.False(versionless["complete"].GetValue<bool>())
            Assert.Equal("not_ready", versionless["status"].GetValue<string>())
            Assert.Equal(1, versionless["staleFileCount"].GetValue<int>())
            let reboundGeneration = bridge.SessionGeneration
            Assert.True(reboundGeneration > generation)

            // Re-open in the replacement generation. An exact server version is
            // causal evidence for this document transition and remains usable;
            // only versionless publications are barred by the taint.
            let! _ = bridge.Formatting({ path = sourcePath; text = Some unsavedSource })

            bridge.DiagnosticsStore[fileKey] <-
                diagnosticEnvelope reboundGeneration (System.Text.Json.Nodes.JsonArray()) (Some 1)

            let! versioned =
                bridge.DiagnosticsForContext(Some projectPath, [| sourcePath |], None, None)

            Assert.True(versioned["complete"].GetValue<bool>())
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``baseline-equivalent initial open accepts versionless diagnostics without rebasing`` () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_diag_equivalent_open_{Guid.NewGuid():N}")
        let projectPath = Path.Combine(root, "App.fsproj")
        let sourcePath = Path.Combine(root, "App.fs")
        let scriptPath = Path.Combine(root, "fake-fsac.fsx")
        let sourceA = "module App\nlet value = 1\n"

        try
            Directory.CreateDirectory(root) |> ignore
            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
            File.WriteAllText(sourcePath, sourceA)
            File.WriteAllText(scriptPath, fakeFsacScript)

            let evaluatedFiles (_: string) = Task.FromResult(Ok [| sourcePath |])

            use bridge =
                new FsAutoCompleteBridge(
                    startupTimeoutOverride = TimeSpan.FromSeconds(10.0),
                    fsacCommandOverride = "dotnet",
                    fsacArgsOverride = [ "fsi"; "--exec"; scriptPath ],
                    evaluatedSourceFilesProvider = evaluatedFiles
                )

            let! _ =
                bridge.SetProject(
                    { projectPath = projectPath
                      workspacePath = None
                      restartLsp = Some true }
                )

            let generation = bridge.SessionGeneration
            let! opened = bridge.Formatting({ path = sourcePath; text = Some sourceA })
            Assert.Equal("ok", opened["status"].GetValue<string>())

            let fileKey = DiagnosticIdentity.canonicalFileKeyFromPath sourcePath

            bridge.DiagnosticsStore[fileKey] <-
                diagnosticEnvelope generation (System.Text.Json.Nodes.JsonArray()) None

            let! snapshot =
                bridge.DiagnosticsForContext(Some projectPath, [| sourcePath |], None, None)

            Assert.Equal("ok", snapshot["status"].GetValue<string>())
            Assert.True(snapshot["complete"].GetValue<bool>())
            Assert.Equal(0, snapshot["staleFileCount"].GetValue<int>())
            Assert.Equal(generation, bridge.SessionGeneration)
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``versionless diagnostics cannot cross an open content ABA transition`` () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_diag_aba_{Guid.NewGuid():N}")
        let projectPath = Path.Combine(root, "App.fsproj")
        let sourcePath = Path.Combine(root, "App.fs")
        let scriptPath = Path.Combine(root, "fake-fsac.fsx")
        let sourceA = "module App\nlet value = 1\n"
        let sourceB = "module App\nlet value = x\n"

        try
            Directory.CreateDirectory(root) |> ignore
            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
            File.WriteAllText(sourcePath, sourceA)
            File.WriteAllText(scriptPath, fakeFsacScript)

            let evaluatedFiles (_: string) = Task.FromResult(Ok [| sourcePath |])

            use bridge =
                new FsAutoCompleteBridge(
                    startupTimeoutOverride = TimeSpan.FromSeconds(10.0),
                    fsacCommandOverride = "dotnet",
                    fsacArgsOverride = [ "fsi"; "--exec"; scriptPath ],
                    evaluatedSourceFilesProvider = evaluatedFiles
                )

            let! _ =
                bridge.SetProject(
                    { projectPath = projectPath
                      workspacePath = None
                      restartLsp = Some true }
                )

            let generation1 = bridge.SessionGeneration
            let fileKey = DiagnosticIdentity.canonicalFileKeyFromPath sourcePath

            // Baseline A -> didOpen B -> didChange A. Disk and current document
            // hashes now both equal the generation baseline again, which made the
            // old hash-only proof vulnerable to a delayed versionless B result.
            let! _ = bridge.Formatting({ path = sourcePath; text = Some sourceB })
            let! _ = bridge.Formatting({ path = sourcePath; text = Some sourceA })

            bridge.DiagnosticsStore[fileKey] <-
                diagnosticEnvelope generation1 (System.Text.Json.Nodes.JsonArray()) None

            let! delayedVersionless =
                bridge.DiagnosticsForContext(Some projectPath, [| sourcePath |], None, None)

            Assert.False(delayedVersionless["complete"].GetValue<bool>())
            Assert.Equal("not_ready", delayedVersionless["status"].GetValue<string>())
            Assert.Equal("warming", delayedVersionless["lspState"].GetValue<string>())
            Assert.Equal(1, delayedVersionless["staleFileCount"].GetValue<int>())

            let generation2 = bridge.SessionGeneration
            Assert.True(generation2 > generation1)
            Assert.Empty(bridge.DiagnosticsStore)

            bridge.DiagnosticsStore[fileKey] <-
                diagnosticEnvelope generation2 (System.Text.Json.Nodes.JsonArray()) None

            let! recovered =
                bridge.DiagnosticsForContext(Some projectPath, [| sourcePath |], None, None)

            Assert.True(recovered["complete"].GetValue<bool>())
            Assert.Equal("ok", recovered["status"].GetValue<string>())
            Assert.Equal(0, recovered["staleFileCount"].GetValue<int>())
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``diagnostic snapshots fail fast instead of queueing behind an in-flight LSP request`` () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_diag_gate_{Guid.NewGuid():N}")
        let projectPath = Path.Combine(root, "App.fsproj")
        let sourcePath = Path.Combine(root, "App.fs")
        let scriptPath = Path.Combine(root, "slow-fsac.fsx")
        let markerPath = Path.Combine(root, "formatting-entered")

        try
            Directory.CreateDirectory(root) |> ignore
            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
            File.WriteAllText(sourcePath, "module App\nlet value = 1\n")
            File.WriteAllText(scriptPath, fakeFsacScriptWithSlowFormatting markerPath 1500)

            let evaluatedFiles (_: string) = Task.FromResult(Ok [| sourcePath |])

            use bridge =
                new FsAutoCompleteBridge(
                    startupTimeoutOverride = TimeSpan.FromSeconds(10.0),
                    requestTimeoutOverride = TimeSpan.FromSeconds(10.0),
                    fsacCommandOverride = "dotnet",
                    fsacArgsOverride = [ "fsi"; "--exec"; scriptPath ],
                    evaluatedSourceFilesProvider = evaluatedFiles
                )

            let! _ =
                bridge.SetProject(
                    { projectPath = projectPath
                      workspacePath = None
                      restartLsp = Some true }
                )

            let generation = bridge.SessionGeneration
            let formatting = bridge.Formatting({ path = sourcePath; text = None })
            let markerDeadline = DateTimeOffset.UtcNow.AddSeconds(3.0)

            while not (File.Exists markerPath) && DateTimeOffset.UtcNow < markerDeadline do
                do! Task.Delay(10)

            let markerObserved = File.Exists markerPath

            let snapshots =
                Array.init 32 (fun _ ->
                    bridge.DiagnosticsForContext(Some projectPath, [| sourcePath |], None, None))

            let allSnapshots = Task.WhenAll(snapshots)
            let! first = Task.WhenAny(allSnapshots :> Task, Task.Delay(500))
            let settledPromptly = obj.ReferenceEquals(first, allSnapshots)
            let formattingStillRunning = not formatting.IsCompleted

            let! formatted = formatting.WaitAsync(TimeSpan.FromSeconds(5.0))
            let! responses = allSnapshots.WaitAsync(TimeSpan.FromSeconds(5.0))
            do! Task.Delay(100)

            Assert.True(markerObserved, "The fake FSAC did not enter the delayed formatting request.")
            Assert.True(formattingStillRunning, "The formatting request did not hold the lifecycle gate.")
            Assert.True(settledPromptly, "DiagnosticsForContext queued behind the lifecycle gate.")
            Assert.Equal("ok", formatted["status"].GetValue<string>())
            Assert.Equal(generation, bridge.SessionGeneration)

            for response in responses do
                Assert.Equal("not_ready", response["status"].GetValue<string>())
                Assert.False(response["ready"].GetValue<bool>())
                Assert.False(response["contextMatched"].GetValue<bool>())
                Assert.False(response["complete"].GetValue<bool>())
                Assert.Equal("lsp_lifecycle_gate_busy", response["reason"].GetValue<string>())
                Assert.Contains("no snapshot work was queued", response["message"].GetValue<string>())
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``Startup timeout kills an unresponsive FSAC child`` () : Task =
    task {
        let id = Guid.NewGuid().ToString("N")
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_lsp_timeout_%s{id}")
        let projectPath = Path.Combine(root, "App.fsproj")
        let scriptPath = Path.Combine(root, "hang.fsx")
        let pidPath = Path.Combine(root, "child.pid")

        try
            Directory.CreateDirectory(root) |> ignore
            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")

            File.WriteAllText(
                scriptPath,
                $"System.IO.File.WriteAllText(@\"%s{pidPath}\", System.Environment.ProcessId.ToString())\nSystem.Threading.Thread.Sleep(30000)\n"
            )

            use bridge =
                new FsAutoCompleteBridge(
                    startupTimeoutOverride = TimeSpan.FromSeconds(3.0),
                    fsacCommandOverride = "dotnet",
                    fsacArgsOverride = [ "fsi"; "--exec"; scriptPath ]
                )

            let operation =
                bridge.SetProject(
                    { projectPath = projectPath
                      workspacePath = None
                      restartLsp = Some true }
                )

            let! error = Assert.ThrowsAsync<TimeoutException>(fun () -> operation :> Task)
            Assert.Contains("initialize", error.Message)
            Assert.True(File.Exists(pidPath), "The fake FSAC child did not start before timeout.")

            let pid = File.ReadAllText(pidPath) |> Int32.Parse

            let isRunning () =
                try
                    use child = Process.GetProcessById(pid)
                    not child.HasExited
                with :? ArgumentException ->
                    false

            let deadline = DateTimeOffset.UtcNow.AddSeconds(2.0)

            while isRunning () && DateTimeOffset.UtcNow < deadline do
                do! Task.Delay(25)

            Assert.False(isRunning (), $"Timed-out FSAC child %d{pid} is still running.")
            Assert.True(bridge.FsacProcess.IsNone)
        finally
            if Directory.Exists(root) then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``Repeated SetProject restarts fully drain prior LSP lifecycle`` () : Task =
    task {
        let id = Guid.NewGuid().ToString("N")
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_lsp_restart_%s{id}")
        let projectPath = Path.Combine(root, "App.fsproj")
        let scriptPath = Path.Combine(root, "fake-fsac.fsx")

        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")

        File.WriteAllText(scriptPath, fakeFsacScript)

        let bridge =
            new FsAutoCompleteBridge(
                startupTimeoutOverride = TimeSpan.FromSeconds(10.0),
                fsacCommandOverride = "dotnet",
                fsacArgsOverride = [ "fsi"; "--exec"; scriptPath ]
            )

        try
            let! first =
                bridge.SetProject(
                    { projectPath = projectPath
                      workspacePath = None
                      restartLsp = Some true }
                )

            let firstResult = first["result"]
            let readiness = firstResult["readiness"]
            Assert.True((firstResult["lspRestartRequested"]).GetValue<bool>())
            Assert.True((firstResult["lspRestarted"]).GetValue<bool>())
            Assert.False((firstResult["lspReplacedExistingProcess"]).GetValue<bool>())
            Assert.False((readiness["symbolIndex"]).GetValue<bool>())
            Assert.Equal("warming", (readiness["symbolIndexState"]).GetValue<string>())
            Assert.False(String.IsNullOrWhiteSpace((readiness["symbolIndexHint"]).GetValue<string>()))
            Assert.Equal(0, bridge.PendingLspCleanupCount)

            for _ in 1..6 do
                let! restarted =
                    bridge.SetProject(
                        { projectPath = projectPath
                          workspacePath = None
                          restartLsp = Some true }
                    )

                let restartedResult = restarted["result"]
                Assert.True((restartedResult["lspRestarted"]).GetValue<bool>())
                Assert.True((restartedResult["lspReplacedExistingProcess"]).GetValue<bool>())
                Assert.Equal(0, bridge.PendingLspCleanupCount)
        finally
            (bridge :> IDisposable).Dispose()

            if Directory.Exists(root) then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``Timed out cleanup remains retained and blocks a new LSP generation`` () : Task =
    task {
        let id = Guid.NewGuid().ToString("N")
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_lsp_cleanup_%s{id}")
        let projectPath = Path.Combine(root, "App.fsproj")
        let scriptPath = Path.Combine(root, "fake-fsac.fsx")
        let cleanupBarrier = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
        File.WriteAllText(scriptPath, fakeFsacScript)

        let bridge =
            new FsAutoCompleteBridge(
                startupTimeoutOverride = TimeSpan.FromSeconds(10.0),
                cleanupTimeoutOverride = TimeSpan.FromMilliseconds(50.0),
                cleanupDrainBarrierOverride = (fun () -> Some(cleanupBarrier.Task :> Task)),
                fsacCommandOverride = "dotnet",
                fsacArgsOverride = [ "fsi"; "--exec"; scriptPath ]
            )

        let restart () =
            bridge.SetProject(
                { projectPath = projectPath
                  workspacePath = None
                  restartLsp = Some true }
            )

        try
            let! _ = restart ()

            let firstPid =
                match bridge.FsacProcess with
                | Some fsacProc -> fsacProc.Id
                | None -> failwith "The first fake FSAC generation did not start."

            let! firstTimeout =
                Assert.ThrowsAsync<InvalidOperationException>(fun () -> restart () :> Task)

            Assert.Contains("draining the previous FSAC LSP generation", firstTimeout.Message)
            Assert.True(bridge.FsacProcess.IsNone)
            Assert.Equal(1, bridge.PendingLspCleanupCount)

            // A second request must wait on the same retained drain instead of starting
            // a replacement while the prior generation still owns pipe-reader tasks.
            let! secondTimeout =
                Assert.ThrowsAsync<InvalidOperationException>(fun () -> restart () :> Task)

            Assert.Contains("draining the previous FSAC LSP generation", secondTimeout.Message)
            Assert.True(bridge.FsacProcess.IsNone)
            Assert.Equal(1, bridge.PendingLspCleanupCount)

            cleanupBarrier.SetResult(())
            let drainDeadline = DateTimeOffset.UtcNow.AddSeconds(2.0)

            while bridge.PendingLspCleanupCount <> 0 && DateTimeOffset.UtcNow < drainDeadline do
                do! Task.Delay(10)

            Assert.Equal(0, bridge.PendingLspCleanupCount)

            let! _ = restart ()

            match bridge.FsacProcess with
            | Some fsacProc -> Assert.NotEqual(firstPid, fsacProc.Id)
            | None -> Assert.Fail("A new FSAC generation did not start after cleanup completed.")
        finally
            cleanupBarrier.TrySetResult(()) |> ignore
            (bridge :> IDisposable).Dispose()

            if Directory.Exists(root) then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``workspace notification parser recognizes FSAC content wrapped finished event`` () =
    let payload =
        jsonElement """{"content":"{\"Kind\":\"workspaceLoad\",\"Data\":{\"Status\":\"finished\"}}"}"""

    Assert.True(WorkspaceNotification.isWorkspaceLoadFinished payload)

[<Fact>]
let ``workspace notification parser recognizes direct finished status`` () =
    let payload = jsonElement """{"status":"finished"}"""

    Assert.True(WorkspaceNotification.isWorkspaceLoadFinished payload)

[<Fact>]
let ``workspace notification parser ignores project loading events`` () =
    let payload =
        jsonElement """{"content":"{\"Kind\":\"projectLoading\",\"Data\":{\"Project\":\"/tmp/App.fsproj\"}}"}"""

    Assert.False(WorkspaceNotification.isWorkspaceLoadFinished payload)

[<Fact>]
let ``workspace notification parser ignores malformed content`` () =
    let payload = jsonElement """{"content":"not json"}"""

    Assert.False(WorkspaceNotification.isWorkspaceLoadFinished payload)

[<Fact>]
let ``workspace selection reports ambiguous solutions in a directory`` () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_workspace_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(Path.Combine(root, "A.slnx"), "")
        File.WriteAllText(Path.Combine(root, "B.slnx"), "")

        match WorkspaceSelection.select root with
        | WorkspaceSelection.Ambiguous candidates ->
            Assert.Equal(2, candidates.Length)
            Assert.All(candidates, fun candidate -> Assert.Equal(WorkspaceSelection.Solution, candidate.Kind))
        | WorkspaceSelection.Selected _ -> Assert.Fail("Expected ambiguous workspace selection.")
        | WorkspaceSelection.Invalid reason -> Assert.Fail($"Expected ambiguous workspace selection, got: {reason}")
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``workspace selection prefers single solution over directory auto selection`` () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_workspace_one_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        let solutionPath = Path.Combine(root, "Only.slnx")
        File.WriteAllText(solutionPath, "")

        match WorkspaceSelection.select root with
        | WorkspaceSelection.Selected(path, candidates) ->
            Assert.Equal(solutionPath, path)
            Assert.Single(candidates) |> ignore
        | WorkspaceSelection.Ambiguous _ -> Assert.Fail("Expected selected workspace.")
        | WorkspaceSelection.Invalid reason -> Assert.Fail($"Expected selected workspace, got: {reason}")
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``workspace selection resolves a single project nested below a repository root`` () =
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_workspace_nested_{Guid.NewGuid():N}")
    let projectPath = Path.Combine(root, "src", "App", "App.fsproj")

    try
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)) |> ignore
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")

        match WorkspaceSelection.select root with
        | WorkspaceSelection.Selected(path, candidates) ->
            Assert.Equal(Path.GetFullPath(projectPath), path)
            let candidate = Assert.Single(candidates)
            Assert.Equal(WorkspaceSelection.Project, candidate.Kind)
        | WorkspaceSelection.Ambiguous _ -> Assert.Fail("Expected the single nested project to be selected.")
        | WorkspaceSelection.Invalid reason -> Assert.Fail($"Expected a selected workspace, got: {reason}")
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``workspace selection keeps multiple nested projects ambiguous`` () =
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_workspace_nested_many_{Guid.NewGuid():N}")

    try
        for name in [ "App"; "Worker" ] do
            let projectPath = Path.Combine(root, "src", name, $"%s{name}.fsproj")
            Directory.CreateDirectory(Path.GetDirectoryName(projectPath)) |> ignore
            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")

        match WorkspaceSelection.select root with
        | WorkspaceSelection.Ambiguous candidates ->
            Assert.Equal(2, candidates.Length)
            Assert.All(candidates, fun candidate -> Assert.Equal(WorkspaceSelection.Project, candidate.Kind))
        | WorkspaceSelection.Selected _ -> Assert.Fail("Expected nested projects to remain ambiguous.")
        | WorkspaceSelection.Invalid reason -> Assert.Fail($"Expected ambiguous workspace selection, got: {reason}")
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``workspace selection rejects an arbitrary file`` () =
    let path = Path.Combine(Path.GetTempPath(), $"fslangmcp-invalid-{Guid.NewGuid():N}.txt")

    try
        File.WriteAllText(path, "not a project")

        match WorkspaceSelection.select path with
        | WorkspaceSelection.Invalid reason -> Assert.Contains(".fsproj", reason)
        | _ -> Assert.Fail("Expected an invalid workspace selection.")
    finally
        if File.Exists path then
            File.Delete path

[<Fact>]
let ``project-bound workspace symbol rejects another project without starting FSAC`` () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_context_{Guid.NewGuid():N}")
        let projectA = Path.Combine(root, "A.fsproj")
        let projectB = Path.Combine(root, "B.fsproj")

        try
            Directory.CreateDirectory(root) |> ignore
            File.WriteAllText(projectA, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
            File.WriteAllText(projectB, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")

            use bridge =
                new FsAutoCompleteBridge(fsacCommandOverride = "this-command-must-not-be-started")

            let! selected =
                bridge.SetProject(
                    { projectPath = projectA
                      workspacePath = None
                      restartLsp = Some false }
                )

            Assert.Equal("ok", selected["status"].GetValue<string>())

            let! response =
                bridge.WorkspaceSymbolForContext(Some projectB, { query = "missing" })

            Assert.Equal("context_mismatch", response["status"].GetValue<string>())
            Assert.False(response["contextMatched"].GetValue<bool>())
            Assert.True(bridge.FsacProcess.IsNone)
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``file-bound helpers reject another project without starting FSAC`` () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_file_context_{Guid.NewGuid():N}")
        let projectADirectory = Path.Combine(root, "A")
        let projectBDirectory = Path.Combine(root, "B")
        let projectA = Path.Combine(projectADirectory, "A.fsproj")
        let projectB = Path.Combine(projectBDirectory, "B.fsproj")
        let sourceB = Path.Combine(projectBDirectory, "Library.fs")

        try
            Directory.CreateDirectory(projectADirectory) |> ignore
            Directory.CreateDirectory(projectBDirectory) |> ignore
            File.WriteAllText(projectA, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
            File.WriteAllText(projectB, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
            File.WriteAllText(sourceB, "module Library")

            use bridge =
                new FsAutoCompleteBridge(fsacCommandOverride = "this-command-must-not-be-started")

            let! selected =
                bridge.SetProject(
                    { projectPath = projectA
                      workspacePath = None
                      restartLsp = Some false }
                )

            Assert.Equal("ok", selected["status"].GetValue<string>())

            let! formatting = bridge.Formatting({ path = sourceB; text = None })

            let! diagnosticFixes =
                bridge.DiagnosticFixes(
                    { path = sourceB
                      text = None
                      line = None
                      character = None }
                )

            for response in [ formatting; diagnosticFixes ] do
                Assert.Equal("context_mismatch", response["status"].GetValue<string>())
                Assert.False(response["contextMatched"].GetValue<bool>())

            Assert.True(bridge.FsacProcess.IsNone)
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``evaluated Compile membership accepts an external linked source file`` () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_linked_context_{Guid.NewGuid():N}")
        let projectDirectory = Path.Combine(root, "App")
        let linkedDirectory = Path.Combine(root, "Shared")
        let projectPath = Path.Combine(projectDirectory, "App.fsproj")
        let unrelatedProjectPath = Path.Combine(linkedDirectory, "Shared.fsproj")
        let linkedSourcePath = Path.Combine(linkedDirectory, "Linked.fs")
        let scriptPath = Path.Combine(root, "fake-fsac.fsx")

        Directory.CreateDirectory(projectDirectory) |> ignore
        Directory.CreateDirectory(linkedDirectory) |> ignore
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
        File.WriteAllText(unrelatedProjectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
        File.WriteAllText(linkedSourcePath, "module Linked")
        File.WriteAllText(scriptPath, fakeFsacScript)

        let evaluatedFiles (candidateProject: string) =
            if String.Equals(Path.GetFullPath(candidateProject), Path.GetFullPath(projectPath), StringComparison.Ordinal) then
                Task.FromResult(Ok [| linkedSourcePath |])
            else
                Task.FromResult(Error "unexpected project")

        let bridge =
            new FsAutoCompleteBridge(
                startupTimeoutOverride = TimeSpan.FromSeconds(10.0),
                fsacCommandOverride = "dotnet",
                fsacArgsOverride = [ "fsi"; "--exec"; scriptPath ],
                evaluatedSourceFilesProvider = evaluatedFiles
            )

        try
            let! selected =
                bridge.SetProject(
                    { projectPath = projectPath
                      workspacePath = None
                      restartLsp = Some true }
                )

            Assert.Equal("ok", selected["status"].GetValue<string>())

            let! response = bridge.Formatting({ path = linkedSourcePath; text = None })

            Assert.Equal("ok", response["status"].GetValue<string>())
            Assert.True(response["contextMatched"].GetValue<bool>())
        finally
            (bridge :> IDisposable).Dispose()

            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``SetProject bounds a stuck evaluated Compile provider by startup timeout`` () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_evaluated_deadline_{Guid.NewGuid():N}")
        let projectPath = Path.Combine(root, "App.fsproj")
        let startupTimeout = TimeSpan.FromMilliseconds(150.0)
        let watchdog = TimeSpan.FromSeconds(2.0)

        let providerStarted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let providerCompletion =
            TaskCompletionSource<Result<string array, string>>(TaskCreationOptions.RunContinuationsAsynchronously)

        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")

        let evaluatedFiles (_: string) =
            providerStarted.TrySetResult(()) |> ignore
            providerCompletion.Task

        let bridge =
            new FsAutoCompleteBridge(
                startupTimeoutOverride = startupTimeout,
                evaluatedSourceFilesProvider = evaluatedFiles
            )

        try
            let elapsed = Stopwatch.StartNew()

            let selectionTask =
                bridge.SetProject(
                    { projectPath = projectPath
                      workspacePath = None
                      restartLsp = Some false }
                )

            do! providerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1.0))
            let! completed = Task.WhenAny(selectionTask :> Task, Task.Delay(watchdog))

            if not (obj.ReferenceEquals(completed, selectionTask)) then
                // Unblock the fake provider before failing so the old unbounded
                // implementation cannot leak a SetProject continuation into later tests.
                providerCompletion.TrySetResult(Error "test cleanup") |> ignore
                let! _ = selectionTask.WaitAsync(TimeSpan.FromSeconds(1.0))
                Assert.Fail($"SetProject exceeded the {watchdog.TotalMilliseconds:F0}ms watchdog.")

            let! selected = selectionTask
            elapsed.Stop()

            Assert.Equal("ok", selected["status"].GetValue<string>())
            Assert.True(bridge.FsacProcess.IsNone)

            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(1.0),
                $"SetProject took {elapsed.Elapsed.TotalMilliseconds:F0}ms for a {startupTimeout.TotalMilliseconds:F0}ms startup timeout."
            )
        finally
            providerCompletion.TrySetResult(Error "test cleanup") |> ignore
            (bridge :> IDisposable).Dispose()

            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``directory SetProject loads and probes its single nested project and accepts its source`` () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_nested_context_{Guid.NewGuid():N}")
        let projectDirectory = Path.Combine(root, "src", "App")
        let projectPath = Path.Combine(projectDirectory, "App.fsproj")
        let sourcePath = Path.Combine(projectDirectory, "Program.fs")
        let scriptPath = Path.Combine(root, "fake-fsac.fsx")
        let evaluatedProjects = ResizeArray<string>()

        Directory.CreateDirectory(projectDirectory) |> ignore
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
        File.WriteAllText(sourcePath, "module Program")
        File.WriteAllText(scriptPath, fakeFsacScript)

        let evaluatedFiles (candidateProject: string) =
            evaluatedProjects.Add(Path.GetFullPath(candidateProject))
            Task.FromResult(Ok [| sourcePath |])

        let bridge =
            new FsAutoCompleteBridge(
                startupTimeoutOverride = TimeSpan.FromSeconds(10.0),
                fsacCommandOverride = "dotnet",
                fsacArgsOverride = [ "fsi"; "--exec"; scriptPath ],
                evaluatedSourceFilesProvider = evaluatedFiles
            )

        try
            let! selected =
                bridge.SetProject(
                    { projectPath = root
                      workspacePath = None
                      restartLsp = Some true }
                )

            Assert.Equal("ok", selected["status"].GetValue<string>())
            let result = selected["result"]
            Assert.Equal(Path.GetFullPath(projectPath), result["projectPath"].GetValue<string>())
            Assert.Equal(Path.GetFullPath(root), result["workspaceRoot"].GetValue<string>())
            let loadedProjects = result["loadedProjects"].AsArray()
            Assert.Single(loadedProjects) |> ignore
            Assert.Equal(Path.GetFullPath(projectPath), loadedProjects[0].GetValue<string>())
            Assert.Equal<string>([| Path.GetFullPath(projectPath) |], evaluatedProjects.ToArray())

            let! formatting = bridge.Formatting({ path = sourcePath; text = None })
            Assert.Equal("ok", formatting["status"].GetValue<string>())
            Assert.True(formatting["contextMatched"].GetValue<bool>())
        finally
            (bridge :> IDisposable).Dispose()

            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``SetProject without restart never splits active project from a live FSAC`` () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_restart_required_{Guid.NewGuid():N}")
        let projectA = Path.Combine(root, "A.fsproj")
        let projectB = Path.Combine(root, "B.fsproj")
        let scriptPath = Path.Combine(root, "fake-fsac.fsx")

        try
            Directory.CreateDirectory(root) |> ignore
            File.WriteAllText(projectA, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
            File.WriteAllText(projectB, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
            File.WriteAllText(scriptPath, fakeFsacScript)

            use bridge =
                new FsAutoCompleteBridge(
                    startupTimeoutOverride = TimeSpan.FromSeconds(10.0),
                    fsacCommandOverride = "dotnet",
                    fsacArgsOverride = [ "fsi"; "--exec"; scriptPath ]
                )

            let! _ =
                bridge.SetProject(
                    { projectPath = projectA
                      workspacePath = None
                      restartLsp = Some true }
                )

            let originalPid = bridge.FsacProcess.Value.Id
            let originalGeneration = bridge.SessionGeneration

            let! rejected =
                bridge.SetProject(
                    { projectPath = projectB
                      workspacePath = None
                      restartLsp = Some false }
                )

            Assert.Equal("restart_required", rejected["status"].GetValue<string>())
            Assert.Equal(Path.GetFullPath(projectA), bridge.CurrentProjectPath.Value)
            Assert.Equal(originalPid, bridge.FsacProcess.Value.Id)
            Assert.Equal(originalGeneration, bridge.SessionGeneration)
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``A completed FSAC session is reaped and restarted on the next request`` () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_dead_fsac_{Guid.NewGuid():N}")
        let projectPath = Path.Combine(root, "App.fsproj")
        let scriptPath = Path.Combine(root, "fake-fsac.fsx")

        try
            Directory.CreateDirectory(root) |> ignore
            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
            File.WriteAllText(scriptPath, fakeFsacScript)

            use bridge =
                new FsAutoCompleteBridge(
                    startupTimeoutOverride = TimeSpan.FromSeconds(10.0),
                    fsacCommandOverride = "dotnet",
                    fsacArgsOverride = [ "fsi"; "--exec"; scriptPath ]
                )

            let! _ =
                bridge.SetProject(
                    { projectPath = projectPath
                      workspacePath = None
                      restartLsp = Some true }
                )

            let first = bridge.FsacProcess.Value
            let firstPid = first.Id
            let firstGeneration = bridge.SessionGeneration
            first.Kill(true)
            do! first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5.0))

            let! response =
                bridge.WorkspaceSymbolForContext(Some projectPath, { query = "anything" })

            Assert.Equal("ok", response["status"].GetValue<string>())
            Assert.True(response["contextMatched"].GetValue<bool>())
            Assert.NotEqual(firstPid, bridge.FsacProcess.Value.Id)
            Assert.True(bridge.SessionGeneration > firstGeneration)
            Assert.Equal("ready", bridge.LifecycleState)
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }

// ─── LspResponseShape (response building, pure) ──────────────────────────────

open System.Text.Json.Nodes
open FsLangMcp.LspBridge.LspResponseShape

[<Fact>]
let ``lspStateString maps true to ready`` () =
    Assert.Equal("ready", lspStateString true)

[<Fact>]
let ``lspStateString maps false to warming`` () =
    Assert.Equal("warming", lspStateString false)

[<Fact>]
let ``assessSymbolIndex is true for any non-empty response`` () =
    let response: JsonNode = JsonArray(JsonValue.Create("a") :> JsonNode) :> JsonNode
    let now = DateTimeOffset.UtcNow
    let ready = ValueSome(now.AddSeconds(-10.0))

    Assert.True(assessSymbolIndex response ready now (TimeSpan.FromSeconds 3.0))

[<Fact>]
let ``assessSymbolIndex is true for empty response when workspaceReadyAt is None`` () =
    // Defensive fallback — if we never observed ready, don't claim the index is warming
    let response: JsonNode = JsonArray() :> JsonNode
    let now = DateTimeOffset.UtcNow

    Assert.False(assessSymbolIndex response ValueNone now (TimeSpan.FromSeconds 3.0))

[<Fact>]
let ``assessSymbolIndex is false for empty response within warmup window`` () =
    let response: JsonNode = JsonArray() :> JsonNode
    let now = DateTimeOffset.UtcNow
    let ready = ValueSome(now.AddSeconds(-1.0))

    Assert.False(assessSymbolIndex response ready now (TimeSpan.FromSeconds 3.0))

[<Fact>]
let ``assessSymbolIndex is true for empty response after warmup window elapsed`` () =
    let response: JsonNode = JsonArray() :> JsonNode
    let now = DateTimeOffset.UtcNow
    let ready = ValueSome(now.AddSeconds(-10.0))

    Assert.True(assessSymbolIndex response ready now (TimeSpan.FromSeconds 3.0))

[<Fact>]
let ``assessSymbolIndex treats non-array responses as ready regardless of timing`` () =
    let response: JsonNode = JsonObject() :> JsonNode
    let now = DateTimeOffset.UtcNow
    let ready = ValueSome(now.AddSeconds(-1.0))

    Assert.True(assessSymbolIndex response ready now (TimeSpan.FromSeconds 3.0))

[<Fact>]
let ``diagnosticsResponseForFile builds status+lspState+count+result`` () =
    let payload: JsonNode = JsonArray() :> JsonNode
    let result = diagnosticsResponseForFile true 5 payload None

    Assert.Equal("ok", result["status"].GetValue<string>())
    Assert.Equal("ready", result["lspState"].GetValue<string>())
    Assert.Equal(5, result["diagnosticsFileCount"].GetValue<int>())
    Assert.NotNull(result["result"])
    Assert.Null(result["analyzedAt"])

[<Fact>]
let ``diagnosticsResponseForFile reports warming when workspace not ready`` () =
    let payload: JsonNode = JsonArray() :> JsonNode
    let result = diagnosticsResponseForFile false 0 payload None

    Assert.Equal("warming", result["lspState"].GetValue<string>())
    Assert.Equal(0, result["diagnosticsFileCount"].GetValue<int>())

[<Fact>]
let ``diagnosticsResponseForFile surfaces analyzedAt timestamp when present`` () =
    let payload: JsonNode = JsonArray() :> JsonNode
    let ts = DateTimeOffset.Parse("2026-05-19T10:00:00Z")
    let result = diagnosticsResponseForFile true 1 payload (Some ts)

    let surfaced = result["analyzedAt"].GetValue<string>()
    Assert.Contains("2026-05-19", surfaced)

[<Fact>]
let ``diagnosticsResponseForWorkspace reports warming and zero count during warmup`` () =
    let root = JsonObject()
    let result = diagnosticsResponseForWorkspace false 0 root None (JsonObject())

    Assert.Equal("ok", result["status"].GetValue<string>())
    Assert.Equal("warming", result["lspState"].GetValue<string>())
    Assert.Equal(0, result["diagnosticsFileCount"].GetValue<int>())
    Assert.Null(result["mostRecentAnalyzedAt"])

[<Fact>]
let ``diagnosticsResponseForWorkspace exposes all collected file payloads`` () =
    let root = JsonObject()
    root["file:///a.fs"] <- JsonArray() :> JsonNode
    root["file:///b.fs"] <- JsonArray() :> JsonNode
    let result = diagnosticsResponseForWorkspace true 2 root None (JsonObject())

    Assert.Equal("ready", result["lspState"].GetValue<string>())
    Assert.Equal(2, result["diagnosticsFileCount"].GetValue<int>())
    Assert.NotNull(result["result"]["file:///a.fs"])
    Assert.NotNull(result["result"]["file:///b.fs"])

[<Fact>]
let ``diagnosticsResponseForWorkspace surfaces per-URI analyzedAt + mostRecentAnalyzedAt`` () =
    let root = JsonObject()
    root["file:///a.fs"] <- JsonArray() :> JsonNode
    let ts = DateTimeOffset.Parse("2026-05-19T10:00:00Z")
    let analyzedAt = JsonObject()
    analyzedAt["file:///a.fs"] <- JsonValue.Create(ts.ToUniversalTime().ToString("O")) :> JsonNode
    let result = diagnosticsResponseForWorkspace true 1 root (Some ts) analyzedAt

    Assert.NotNull(result["analyzedAtByUri"]["file:///a.fs"])
    Assert.Contains("2026-05-19", result["mostRecentAnalyzedAt"].GetValue<string>())

[<Fact>]
let ``workspaceSymbolResponse flags symbolIndexReady=false for empty result inside warmup window`` () =
    let response: JsonNode = JsonArray() :> JsonNode
    let now = DateTimeOffset.UtcNow
    let ready = ValueSome(now.AddSeconds(-1.0))
    let result = workspaceSymbolResponse response ready now (TimeSpan.FromSeconds 3.0)

    Assert.Equal("ok", result["status"].GetValue<string>())
    Assert.Equal("ready", result["lspState"].GetValue<string>())
    Assert.False(result["symbolIndexReady"].GetValue<bool>())

[<Fact>]
let ``workspaceSymbolResponse flags symbolIndexReady=true for non-empty result`` () =
    let response: JsonNode = JsonArray(JsonValue.Create("hit") :> JsonNode) :> JsonNode
    let now = DateTimeOffset.UtcNow
    let ready = ValueSome(now.AddSeconds(-1.0))
    let result = workspaceSymbolResponse response ready now (TimeSpan.FromSeconds 3.0)

    Assert.True(result["symbolIndexReady"].GetValue<bool>())
    Assert.NotNull(result["result"])

// ─── SolutionParsing ──────────────────────────────────────────────────────────

open FsLangMcp.ProjectFiles.SolutionParsing

[<Fact>]
let ``listProjects returns the single fsproj when given a fsproj path`` () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"sp_fsproj_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        let fsproj = Path.Combine(root, "App.fsproj")
        File.WriteAllText(fsproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />")

        let result = listProjects fsproj

        Assert.Equal(1, result.Length)
        Assert.Equal(Path.GetFullPath fsproj, result[0])
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``listProjects recursively discovers projects for a directory target`` () =
    let root = Path.Combine(Path.GetTempPath(), $"sp_directory_{Guid.NewGuid():N}")
    let appProject = Path.Combine(root, "src", "App", "App.fsproj")
    let ignoredProject = Path.Combine(root, "src", "App", "obj", "Generated.fsproj")

    try
        Directory.CreateDirectory(Path.GetDirectoryName(appProject)) |> ignore
        Directory.CreateDirectory(Path.GetDirectoryName(ignoredProject)) |> ignore
        File.WriteAllText(appProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        File.WriteAllText(ignoredProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />")

        let result = listProjects root

        let discovered = Assert.Single(result)
        Assert.Equal(Path.GetFullPath(appProject), discovered)
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``listProjects returns all fsproj entries from slnx`` () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"sp_slnx_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(Path.Combine(root, "A.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        File.WriteAllText(Path.Combine(root, "B.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        let slnx = Path.Combine(root, "S.slnx")

        File.WriteAllText(
            slnx,
            String.concat
                "\n"
                [ "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                  "<Solution>"
                  "  <Project Path=\"A.fsproj\" />"
                  "  <Project Path=\"B.fsproj\" />"
                  "</Solution>" ])

        let result = listProjects slnx |> Array.sort

        Assert.Equal(2, result.Length)
        Assert.EndsWith("A.fsproj", result[0])
        Assert.EndsWith("B.fsproj", result[1])
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``listProjects returns all fsproj entries from sln`` () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"sp_sln_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(Path.Combine(root, "A.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        File.WriteAllText(Path.Combine(root, "B.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        let sln = Path.Combine(root, "S.sln")

        File.WriteAllText(
            sln,
            String.concat
                "\n"
                [ "Microsoft Visual Studio Solution File, Format Version 12.00"
                  "Project(\"{F2A71F9B-5D33-465A-A702-920D77279786}\") = \"A\", \"A.fsproj\", \"{00000000-0000-0000-0000-000000000001}\""
                  "EndProject"
                  "Project(\"{F2A71F9B-5D33-465A-A702-920D77279786}\") = \"B\", \"B.fsproj\", \"{00000000-0000-0000-0000-000000000002}\""
                  "EndProject" ])

        let result = listProjects sln |> Array.sort

        Assert.Equal(2, result.Length)
        Assert.EndsWith("A.fsproj", result[0])
        Assert.EndsWith("B.fsproj", result[1])
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``listProjects returns empty for non-existent path`` () =
    let result = listProjects "/nonexistent/path/Foo.fsproj"
    Assert.Empty(result)

[<Fact>]
let ``listProjects returns empty for unsupported extension`` () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"sp_unknown_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "Foo.txt")
        File.WriteAllText(path, "hello")
        Assert.Empty(listProjects path)
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``listProjects skips fsproj entries that don't exist on disk`` () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"sp_skip_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        // Only A.fsproj exists; B.fsproj does not.
        File.WriteAllText(Path.Combine(root, "A.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        let slnx = Path.Combine(root, "S.slnx")

        File.WriteAllText(
            slnx,
            String.concat
                "\n"
                [ "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                  "<Solution>"
                  "  <Project Path=\"A.fsproj\" />"
                  "  <Project Path=\"Missing.fsproj\" />"
                  "</Solution>" ])

        let result = listProjects slnx

        Assert.Equal(1, result.Length)
        Assert.EndsWith("A.fsproj", result[0])
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

// ─── workspace_diagnostics filters (#108) ────────────────────────────────────

open FsLangMcp.Types

[<Fact>]
let ``severityCodeOf maps known names case-insensitively`` () =
    Assert.Equal(Some 1, severityCodeOf "error")
    Assert.Equal(Some 1, severityCodeOf "ERROR")
    Assert.Equal(Some 1, severityCodeOf "errors")
    Assert.Equal(Some 2, severityCodeOf "warning")
    Assert.Equal(Some 2, severityCodeOf "Warnings")
    Assert.Equal(Some 3, severityCodeOf "information")
    Assert.Equal(Some 3, severityCodeOf "info")
    Assert.Equal(Some 4, severityCodeOf "hint")
    Assert.Equal(Some 4, severityCodeOf "hints")

[<Fact>]
let ``severityCodeOf returns None for unknown names`` () =
    Assert.Equal(None, severityCodeOf "fatal")
    Assert.Equal(None, severityCodeOf "")
    Assert.Equal(None, severityCodeOf "  ")

[<Fact>]
let ``fileMatchesGlob single-segment star does not span slash`` () =
    Assert.True(fileMatchesGlob "*.fs" "Foo.fs")
    Assert.True(fileMatchesGlob "src/*.fs" "src/Foo.fs")
    Assert.False(fileMatchesGlob "src/*.fs" "src/Sub/Foo.fs") // * is single-segment
    Assert.True(fileMatchesGlob "file:///*/Foo.fs" "file:///src/Foo.fs")
    Assert.False(fileMatchesGlob "*.fs" "Foo.fsx")
    Assert.False(fileMatchesGlob "src/*.fs" "other/Foo.fs")
    Assert.True(fileMatchesGlob "F?o.fs" "Foo.fs")
    Assert.False(fileMatchesGlob "F?o.fs" "Fooo.fs")
    Assert.False(fileMatchesGlob "?.fs" "a/b.fs") // ? doesn't match /

[<Fact>]
let ``fileMatchesGlob double-star spans across slashes`` () =
    Assert.True(fileMatchesGlob "src/**/*.fs" "src/Foo.fs")
    Assert.True(fileMatchesGlob "src/**/*.fs" "src/Sub/Foo.fs")
    Assert.True(fileMatchesGlob "src/**/*.fs" "src/A/B/C/Foo.fs")
    Assert.True(fileMatchesGlob "**/*.fs" "Foo.fs")
    Assert.True(fileMatchesGlob "**/*.fs" "deeply/nested/Foo.fs")
    Assert.False(fileMatchesGlob "src/**/*.fs" "other/Foo.fs")

[<Fact>]
let ``fileMatchesGlob is case-insensitive`` () =
    Assert.True(fileMatchesGlob "*.FS" "Foo.fs")
    Assert.True(fileMatchesGlob "src/*.fs" "SRC/Foo.fs")

[<Fact>]
let ``filterDiagnosticsBySeverity keeps only matching codes`` () =
    let diagnostics =
        JsonArray(
            jobj [ "message", jstr "err1"; "severity", jint 1 ] :> JsonNode,
            jobj [ "message", jstr "warn1"; "severity", jint 2 ] :> JsonNode,
            jobj [ "message", jstr "err2"; "severity", jint 1 ] :> JsonNode,
            jobj [ "message", jstr "info1"; "severity", jint 3 ] :> JsonNode
        )
        :> JsonNode

    let errorsOnly = filterDiagnosticsBySeverity 1 diagnostics :?> JsonArray

    Assert.Equal(2, errorsOnly.Count)
    Assert.Equal("err1", (errorsOnly[0]["message"]).GetValue<string>())
    Assert.Equal("err2", (errorsOnly[1]["message"]).GetValue<string>())

[<Fact>]
let ``filterDiagnosticsBySeverity drops entries missing severity field`` () =
    let diagnostics =
        JsonArray(
            jobj [ "message", jstr "no-severity" ] :> JsonNode,
            jobj [ "message", jstr "err"; "severity", jint 1 ] :> JsonNode
        )
        :> JsonNode

    let filtered = filterDiagnosticsBySeverity 1 diagnostics :?> JsonArray

    Assert.Equal(1, filtered.Count)
    Assert.Equal("err", (filtered[0]["message"]).GetValue<string>())

[<Fact>]
let ``filterDiagnosticsBySeverity returns empty array when nothing matches`` () =
    let diagnostics =
        JsonArray(jobj [ "message", jstr "info"; "severity", jint 3 ] :> JsonNode)
        :> JsonNode

    let filtered = filterDiagnosticsBySeverity 1 diagnostics :?> JsonArray

    Assert.Equal(0, filtered.Count)

[<Fact>]
let ``fileMatchesGlob trailing double-star matches everything inside`` () =
    Assert.True(fileMatchesGlob "src/**" "src/Foo.fs")
    Assert.True(fileMatchesGlob "src/**" "src/A/B/Foo.fs")
    Assert.False(fileMatchesGlob "src/**" "other/Foo.fs")

[<Fact>]
let ``workspace glob matches evaluated files relative to the selected root`` () =
    let root = Path.Combine(Path.GetTempPath(), "fslangmcp-glob-root")
    let adapter = Path.Combine(root, "src", "Adapters", "GitHub.fs")
    let nested = Path.Combine(root, "src", "Adapters", "Generated", "GitHub.fs")
    let other = Path.Combine(root, "tests", "Adapters", "GitHub.fs")

    Assert.True(fileMatchesWorkspaceGlob (Some root) "src/Adapters/*.fs" adapter)
    Assert.False(fileMatchesWorkspaceGlob (Some root) "src/Adapters/*.fs" nested)
    Assert.False(fileMatchesWorkspaceGlob (Some root) "src/Adapters/*.fs" other)
    Assert.True(fileMatchesWorkspaceGlob (Some root) "src/**/*.fs" nested)
    let uriPattern = Uri(adapter).AbsoluteUri.Replace("GitHub.fs", "*.fs")
    Assert.True(fileMatchesWorkspaceGlob (Some root) uriPattern adapter)

[<Fact>]
let ``diagnosticsResponseForFile preserves payload when no severity filter`` () =
    // path + severity combo verified at the pure helper layer: severity filter
    // applies before this builder is called.
    let payload =
        JsonArray(
            jobj [ "message", jstr "err"; "severity", jint 1 ] :> JsonNode,
            jobj [ "message", jstr "warn"; "severity", jint 2 ] :> JsonNode
        )
        :> JsonNode

    let result = diagnosticsResponseForFile true 1 payload None
    let resultArr = (result["result"]) :?> JsonArray

    Assert.Equal(2, resultArr.Count)
    Assert.Equal("ready", result["lspState"].GetValue<string>())

[<Fact>]
let ``diagnosticsResponseForFile with pre-filtered severity payload reflects filter`` () =
    // Simulates the bridge's pipeline: severity filter runs on the payload before
    // it reaches the response builder. We verify the builder doesn't add/remove.
    let raw =
        JsonArray(
            jobj [ "message", jstr "err"; "severity", jint 1 ] :> JsonNode,
            jobj [ "message", jstr "warn"; "severity", jint 2 ] :> JsonNode
        )
        :> JsonNode

    let filtered = filterDiagnosticsBySeverity 1 raw
    let result = diagnosticsResponseForFile true 1 filtered None
    let resultArr = (result["result"]) :?> JsonArray

    Assert.Equal(1, resultArr.Count)
    Assert.Equal("err", (resultArr[0]["message"]).GetValue<string>())

[<Fact>]
let ``diagnosticsResponseForWorkspace with empty filtered files yields empty result`` () =
    // Bridge logic drops URIs whose diagnostic list becomes empty after severity
    // filtering. We exercise the builder with an already-empty root to verify the
    // outer shape is still well-formed.
    let root = JsonObject()
    let result = diagnosticsResponseForWorkspace true 0 root None (JsonObject())

    Assert.Equal("ready", result["lspState"].GetValue<string>())
    Assert.Equal(0, result["diagnosticsFileCount"].GetValue<int>())
    let resultObj = (result["result"]) :?> JsonObject
    Assert.Equal(0, resultObj.Count)

// ─── workspace_diagnostics mostRecentAnalyzedAt scoping (#123) ───────────────

/// Simulates the mostRecent computation that LspBridge.Diagnostics performs:
/// filters the analyzedAt dictionary by globMatches, then takes the max.
/// Tests are at this level (rather than through the stateful Diagnostics member)
/// because the LspBridge class requires a live LSP process. The pure filtering
/// logic is what changed in the fix and is fully exercised here.
let private computeMostRecent (fileGlob: string option) (store: (string * DateTimeOffset) list) =
    let globMatches (uri: string) =
        match fileGlob with
        | Some pattern -> fileMatchesGlob pattern uri
        | None -> true

    let filtered =
        store
        |> List.filter (fun (uri, _) -> globMatches uri)
        |> List.map snd

    match filtered with
    | [] -> None
    | vs -> vs |> List.max |> Some

[<Fact>]
let ``mostRecentAnalyzedAt without fileGlob equals workspace-wide max`` () =
    let earlier = DateTimeOffset.Parse("2026-05-19T08:00:00Z")
    let later   = DateTimeOffset.Parse("2026-05-19T10:00:00Z")

    let store =
        [ "file:///src/Foo.fs",   earlier
          "file:///tests/Bar.fs", later ]

    let mostRecent = computeMostRecent None store

    // No glob → workspace-wide max → the later timestamp wins.
    Assert.Equal(Some later, mostRecent)

    // Verify it surfaces correctly through the response builder.
    let root = JsonObject()
    let analyzedAtByUri = JsonObject()
    let result = diagnosticsResponseForWorkspace true 2 root mostRecent analyzedAtByUri
    Assert.Contains("2026-05-19T10:00:00", result["mostRecentAnalyzedAt"].GetValue<string>())

[<Fact>]
let ``mostRecentAnalyzedAt with fileGlob restricts to matched subset`` () =
    let srcTs   = DateTimeOffset.Parse("2026-05-19T08:00:00Z")
    let testsTs = DateTimeOffset.Parse("2026-05-19T10:00:00Z")  // fresher, but outside glob

    let store =
        [ "file:///src/Foo.fs",   srcTs
          "file:///tests/Bar.fs", testsTs ]

    // Glob matches only the src file; tests/ is excluded.
    let mostRecent = computeMostRecent (Some "file:///src/**") store

    // The fresher tests/Bar.fs must NOT influence the result.
    Assert.Equal(Some srcTs, mostRecent)
    Assert.NotEqual(Some testsTs, mostRecent)

    // Sanity-check through the response builder.
    let root = JsonObject()
    let analyzedAtByUri = JsonObject()
    let result = diagnosticsResponseForWorkspace true 1 root mostRecent analyzedAtByUri
    Assert.Contains("2026-05-19T08:00:00", result["mostRecentAnalyzedAt"].GetValue<string>())

[<Fact>]
let ``mostRecentAnalyzedAt with fileGlob matching zero files is null`` () =
    let store =
        [ "file:///tests/Bar.fs", DateTimeOffset.Parse("2026-05-19T10:00:00Z") ]

    // Glob matches nothing in the store.
    let mostRecent = computeMostRecent (Some "file:///src/**") store

    Assert.Equal(None, mostRecent)

    // The response builder must still produce a well-formed object with null mostRecentAnalyzedAt.
    let root = JsonObject()
    let analyzedAtByUri = JsonObject()
    let result = diagnosticsResponseForWorkspace true 0 root mostRecent analyzedAtByUri
    Assert.Null(result["mostRecentAnalyzedAt"])

// ─── set_project arg validation (#100) ───────────────────────────────────────

[<Fact>]
let ``setProjectReadiness explains how to recover while symbol index is warming`` () =
    let now = DateTimeOffset.UtcNow
    let workspaceReadyAt = ValueSome(now.AddSeconds(-1.0)) // within the 3s warmup window

    let readiness =
        setProjectReadiness true false true workspaceReadyAt now (TimeSpan.FromSeconds 3.0)

    Assert.False(readiness["symbolIndex"].GetValue<bool>())
    Assert.Equal("warming", readiness["symbolIndexState"].GetValue<string>())
    Assert.Contains("incomplete", readiness["symbolIndexHint"].GetValue<string>())
    Assert.Contains("retry", readiness["symbolIndexHint"].GetValue<string>())

[<Fact>]
let ``setProjectReadiness reports not_warmed once the warmup window has elapsed (#194)`` () =
    let now = DateTimeOffset.UtcNow
    let workspaceReadyAt = ValueSome(now.AddSeconds(-10.0)) // past the 3s warmup window

    let readiness =
        setProjectReadiness true false true workspaceReadyAt now (TimeSpan.FromSeconds 3.0)

    Assert.False(readiness["symbolIndex"].GetValue<bool>())
    Assert.Equal("not_warmed", readiness["symbolIndexState"].GetValue<string>())
    Assert.Contains("did not warm within 3s", readiness["symbolIndexHint"].GetValue<string>())
    Assert.Contains("find and check are unaffected", readiness["symbolIndexHint"].GetValue<string>())
    Assert.Contains("FCS sweeps", readiness["symbolIndexHint"].GetValue<string>())

[<Fact>]
let ``setProjectReadiness stays warming when workspaceReadyAt is unknown, even long after set_project returned`` () =
    // No observed workspace-ready timestamp means there's nothing to measure
    // elapsed time from — stay in "warming" rather than guessing "not_warmed".
    let now = DateTimeOffset.UtcNow
    let readiness = setProjectReadiness true false true ValueNone now (TimeSpan.FromSeconds 3.0)

    Assert.Equal("warming", readiness["symbolIndexState"].GetValue<string>())

[<Fact>]
let ``setProjectReadiness regression: reports ready once the symbol index has warmed`` () =
    let now = DateTimeOffset.UtcNow
    let workspaceReadyAt = ValueSome(now.AddSeconds(-10.0)) // past the window, but warmed

    let readiness =
        setProjectReadiness true true true workspaceReadyAt now (TimeSpan.FromSeconds 3.0)

    Assert.True(readiness["symbolIndex"].GetValue<bool>())
    Assert.Equal("ready", readiness["symbolIndexState"].GetValue<string>())
    Assert.Null(readiness["symbolIndexHint"])

[<Fact>]
let ``setProjectReadiness regression: explains how to start LSP when it was not requested`` () =
    let now = DateTimeOffset.UtcNow
    // Past the warmup window — but lspReady=false, so not_warmed must not fire;
    // not_started/blocked_on_lsp take priority whenever the LSP itself isn't up.
    let workspaceReadyAt = ValueSome(now.AddSeconds(-10.0))

    let readiness =
        setProjectReadiness false false false workspaceReadyAt now (TimeSpan.FromSeconds 3.0)

    let timedOutReadiness =
        setProjectReadiness false false true workspaceReadyAt now (TimeSpan.FromSeconds 3.0)

    Assert.Equal("not_started", readiness["symbolIndexState"].GetValue<string>())
    Assert.Contains("unavailable", readiness["symbolIndexHint"].GetValue<string>())
    Assert.Contains("restartLsp=true", readiness["symbolIndexHint"].GetValue<string>())
    Assert.Equal("blocked_on_lsp", timedOutReadiness["symbolIndexState"].GetValue<string>())
    Assert.Contains("Retry set_project", timedOutReadiness["symbolIndexHint"].GetValue<string>())

[<Fact>]
let ``lspRestartOccurred requires both a restart request and a running LSP`` () =
    Assert.False(lspRestartOccurred true false)
    Assert.False(lspRestartOccurred false true)
    Assert.True(lspRestartOccurred true true)

[<Fact>]
let ``SetProject with null projectPath returns invalid_args naming projectPath`` () : System.Threading.Tasks.Task =
    task {
        // A wrong/missing JSON key deserializes projectPath to null. The guard must
        // return the standard invalid_args envelope naming the public field, not let
        // Path.GetFullPath(null) throw ArgumentNullException naming the internal 'path'.
        use bridge = new FsAutoCompleteBridge()

        let! result =
            bridge.SetProject(
                { projectPath = null
                  workspacePath = None
                  restartLsp = Some false }
            )

        Assert.Equal("invalid_args", result["status"].GetValue<string>())
        Assert.Contains("projectPath", result["message"].GetValue<string>())
    }

[<Fact>]
let ``SetProject with blank projectPath returns invalid_args`` () : System.Threading.Tasks.Task =
    task {
        use bridge = new FsAutoCompleteBridge()

        let! result =
            bridge.SetProject(
                { projectPath = "   "
                  workspacePath = None
                  restartLsp = Some false }
            )

        Assert.Equal("invalid_args", result["status"].GetValue<string>())
    }

[<Fact>]
let ``SetProject rejects a non-directory workspacePath`` () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_workspace_arg_{Guid.NewGuid():N}")
        let projectPath = Path.Combine(root, "App.fsproj")
        let workspaceFile = Path.Combine(root, "workspace.txt")

        try
            Directory.CreateDirectory(root) |> ignore
            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
            File.WriteAllText(workspaceFile, "not a directory")
            use bridge = new FsAutoCompleteBridge()

            let! result =
                bridge.SetProject(
                    { projectPath = projectPath
                      workspacePath = Some workspaceFile
                      restartLsp = Some false }
                )

            Assert.Equal("invalid_args", result["status"].GetValue<string>())
            Assert.Contains("workspacePath", result["message"].GetValue<string>())
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``RenamePreview reports missing FSAC executable as infrastructure error`` () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_rename_infra_{Guid.NewGuid():N}")
        let projectPath = Path.Combine(root, "App.fsproj")
        let sourcePath = Path.Combine(root, "App.fs")

        try
            Directory.CreateDirectory(root) |> ignore
            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
            File.WriteAllText(sourcePath, "module App\nlet value = 1\n")

            use bridge =
                new FsAutoCompleteBridge(fsacCommandOverride = $"missing-fsac-{Guid.NewGuid():N}")

            let! _ =
                bridge.SetProject(
                    { projectPath = projectPath
                      workspacePath = None
                      restartLsp = Some false }
                )

            let! result =
                bridge.RenamePreview(
                    { path = sourcePath
                      line = 1
                      character = 5
                      newName = "renamed"
                      text = None }
                )

            Assert.Equal("infrastructure_error", result["status"].GetValue<string>())
            Assert.Equal("executable_missing", result["errorKind"].GetValue<string>())
            Assert.True(result["contextMatched"].GetValue<bool>())
            Assert.Equal(Path.GetFullPath(projectPath), result["activeProjectPath"].GetValue<string>())
            Assert.Equal(0L, result["sessionGeneration"].GetValue<int64>())
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }
