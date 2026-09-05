namespace FsLangMcp.Tests

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Reflection
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open Xunit.Abstractions
open Xunit.Sdk

[<assembly: TestFramework("FsLangMcp.Tests.TestHostTraceFramework", "FsLangMcp.Tests")>]
do ()

/// Scales only test-harness watchdogs. Product timeouts stay unchanged, so the
/// tests continue to prove the real deadline while allowing a loaded CI host
/// more time to observe the result. The cap keeps a bad environment value from
/// disabling hang enforcement.
module internal TestTiming =

    let private formatWatchdogDuration (duration: TimeSpan) =
        duration.ToString("c", CultureInfo.InvariantCulture)

    type WatchdogTimeoutException(operationName: string, watchdogDuration: TimeSpan) =
        inherit
            TimeoutException(
                $"Test watchdog '{operationName}' expired after {formatWatchdogDuration watchdogDuration}; underlying producer ownership was transferred for observation and deferred fixture cleanup."
            )

        member _.OperationName = operationName
        member _.WatchdogDuration = watchdogDuration

    let private multiplier =
        lazy
            (let raw = Environment.GetEnvironmentVariable("FSLANGMCP_TEST_TIME_MULTIPLIER")

             match Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) with
             | true, value when Double.IsFinite(value) && value >= 1.0 -> min value 10.0
             | _ -> 1.0)

    let watchdog (duration: TimeSpan) =
        TimeSpan.FromTicks(int64 (float duration.Ticks * multiplier.Value))

    /// Wait for a producer without abandoning it when the watchdog wins. The
    /// transfer callback must retain the producer and arrange observation before
    /// this returns a distinct watchdog exception. A producer-originated
    /// TimeoutException passes through unchanged because completion identity,
    /// not exception type, selects the path.
    let awaitProducerWithWatchdogTask
        (operationName: string)
        (watchdogDuration: TimeSpan)
        (watchdogTask: Task)
        (transferProducerOwnership: Task -> unit)
        (producer: Task<'T>)
        : Task<'T> =
        task {
            let producerTask = producer :> Task
            let! completed = Task.WhenAny(producerTask, watchdogTask)

            if Object.ReferenceEquals(completed, producerTask) then
                return! producer
            else
                // A faulted/cancelled injected watchdog is not an expiry signal.
                do! watchdogTask
                transferProducerOwnership producerTask
                return raise (WatchdogTimeoutException(operationName, watchdogDuration))
        }

    let awaitProducer
        (operationName: string)
        (duration: TimeSpan)
        (transferProducerOwnership: Task -> unit)
        (producer: Task<'T>)
        : Task<'T> =
        task {
            let effectiveDuration = watchdog duration
            use watchdogCancellation = new CancellationTokenSource()
            let watchdogTask = Task.Delay(effectiveDuration, watchdogCancellation.Token)

            try
                return!
                    awaitProducerWithWatchdogTask
                        operationName
                        effectiveDuration
                        watchdogTask
                        transferProducerOwnership
                        producer
            finally
                watchdogCancellation.Cancel()
        }

/// Opt-in, append-free trace for CI recurrence forensics. Each event is flushed
/// immediately so a killed testhost still leaves the last observed lifecycle
/// transition in TestResults. Timestamps record when the xUnit message sink
/// observes an event, not the instant the test body starts or finishes. Tracing
/// must never change a test outcome.
module internal TestRunTrace =

    [<NoComparison; NoEquality>]
    type DeferredCleanupResult =
        { ProducerFailure: exn option
          CleanupFailure: exn option }

    let private writeGate = obj ()

    let private writer =
        lazy
            (try
                let configuredPath = Environment.GetEnvironmentVariable("FSLANGMCP_TEST_TRACE")

                if String.IsNullOrWhiteSpace(configuredPath) then
                    None
                else
                    let tracePath = Path.GetFullPath(configuredPath)
                    let traceDirectory = Path.GetDirectoryName(tracePath)

                    if not (String.IsNullOrWhiteSpace(traceDirectory)) then
                        Directory.CreateDirectory(traceDirectory) |> ignore

                    let stream =
                        new FileStream(
                            tracePath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.ReadWrite,
                            4096,
                            FileOptions.WriteThrough
                        )

                    let output = new StreamWriter(stream, UTF8Encoding(false))
                    output.AutoFlush <- true
                    Some output
             with _ ->
                 None)

    let write (eventName: string) (fields: (string * string) list) =
        try
            match writer.Value with
            | None -> ()
            | Some output ->
                let node = JsonObject()

                node["timestampUtc"] <-
                    JsonValue.Create(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))

                node["timestampSemantics"] <- JsonValue.Create("xunit_message_sink_observation")
                node["monotonicTimestamp"] <- JsonValue.Create(System.Diagnostics.Stopwatch.GetTimestamp())
                node["processId"] <- JsonValue.Create(Environment.ProcessId)
                node["managedThreadId"] <- JsonValue.Create(Environment.CurrentManagedThreadId)
                node["event"] <- JsonValue.Create(eventName)

                for key, value in fields do
                    node[key] <- JsonValue.Create(if isNull value then "" else value)

                lock writeGate (fun () -> output.WriteLine(node.ToJsonString()))
        with _ ->
            ()

    let fixture eventName fixtureName root =
        write eventName [ "fixture", fixtureName; "root", root ]

    /// Delete one fixture-owned tree. Short retries absorb transient released
    /// handles; a persistent failure is surfaced to xUnit instead of being lost.
    let deleteOwnedDirectory fixtureName root =
        fixture "fixture_dispose_start" fixtureName root

        try
            let rec delete attempt =
                if Directory.Exists(root) then
                    try
                        Directory.Delete(root, true)
                    with
                    | :? IOException
                    | :? UnauthorizedAccessException when attempt < 6 ->
                        Thread.Sleep(50)
                        delete (attempt + 1)

            delete 1

            if Directory.Exists(root) then
                raise (IOException($"Fixture directory still exists after cleanup: {root}"))

            fixture "fixture_dispose_complete" fixtureName root
        with ex ->
            write
                "fixture_dispose_failed"
                [ "fixture", fixtureName
                  "root", root
                  "exceptionType", ex.GetType().FullName
                  "message", ex.Message ]

            reraise ()

    /// Transfer fixture ownership to a live producer. This task never faults:
    /// it observes and records a late producer failure, then attempts cleanup
    /// only after that producer reaches a terminal state.
    let deferOwnedDirectoryUntilProducerCompletes fixtureName root (producer: Task) : Task<DeferredCleanupResult> =
        task {
            fixture "fixture_dispose_deferred" fixtureName root

            let! producerFailure =
                task {
                    try
                        do! producer
                        write "fixture_producer_complete" [ "fixture", fixtureName; "root", root ]
                        return None
                    with ex ->
                        write
                            "fixture_producer_failed"
                            [ "fixture", fixtureName
                              "root", root
                              "exceptionType", ex.GetType().FullName
                              "message", ex.Message ]

                        return Some ex
                }

            let cleanupFailure =
                try
                    deleteOwnedDirectory fixtureName root
                    None
                with ex ->
                    Some ex

            return
                { ProducerFailure = producerFailure
                  CleanupFailure = cleanupFailure }
        }

    let private collectionName (message: ITestCollectionMessage) = message.TestCollection.DisplayName

    let private className (message: ITestClassMessage) = message.TestClass.Class.Name

    let private finishedFields (message: IFinishedMessage) =
        [ "executionSeconds", message.ExecutionTime.ToString(CultureInfo.InvariantCulture)
          "testsRun", message.TestsRun.ToString(CultureInfo.InvariantCulture)
          "testsFailed", message.TestsFailed.ToString(CultureInfo.InvariantCulture)
          "testsSkipped", message.TestsSkipped.ToString(CultureInfo.InvariantCulture) ]

    let private failureFields (message: IFailureInformation) =
        [ "exceptionTypes", String.concat " | " message.ExceptionTypes
          "messages", String.concat " | " message.Messages ]

    let recordMessage (message: IMessageSinkMessage) =
        try
            match message with
            | :? ITestStarting as started ->
                write
                    "test_start"
                    [ "test", started.Test.DisplayName
                      "class", className started
                      "collection", collectionName started ]
            | :? ITestFinished as finished ->
                write
                    "test_complete"
                    [ "test", finished.Test.DisplayName
                      "class", className finished
                      "collection", collectionName finished
                      "executionSeconds", finished.ExecutionTime.ToString(CultureInfo.InvariantCulture) ]
            | :? ITestClassCleanupFailure as failed ->
                write
                    "class_cleanup_failed"
                    ([ "class", className failed; "collection", collectionName failed ]
                     @ failureFields failed)
            | :? ITestCollectionCleanupFailure as failed ->
                write "collection_cleanup_failed" ([ "collection", collectionName failed ] @ failureFields failed)
            | :? ITestAssemblyCleanupFailure as failed -> write "assembly_cleanup_failed" (failureFields failed)
            | :? ITestClassStarting as started ->
                write "class_start" [ "class", className started; "collection", collectionName started ]
            | :? ITestClassFinished as finished ->
                write
                    "class_complete"
                    ([ "class", className finished; "collection", collectionName finished ]
                     @ finishedFields finished)
            | :? ITestCollectionStarting as started -> write "collection_start" [ "collection", collectionName started ]
            | :? ITestCollectionFinished as finished ->
                write "collection_complete" ([ "collection", collectionName finished ] @ finishedFields finished)
            | :? ITestAssemblyStarting as started ->
                write
                    "assembly_start"
                    [ "environment", started.TestEnvironment
                      "framework", started.TestFrameworkDisplayName ]
            | :? ITestAssemblyFinished as finished -> write "assembly_complete" (finishedFields finished)
            | _ -> ()
        with _ ->
            ()

type TestHostTraceFrameworkExecutor
    (
        assemblyName: AssemblyName,
        sourceInformationProvider: ISourceInformationProvider,
        diagnosticMessageSink: IMessageSink
    ) =
    inherit XunitTestFrameworkExecutor(assemblyName, sourceInformationProvider, diagnosticMessageSink)

    override _.RunTestCases(testCases, executionMessageSink, executionOptions) =
        let tracingSink =
            new DelegatingMessageSink(executionMessageSink, Action<IMessageSinkMessage>(TestRunTrace.recordMessage))

        base.RunTestCases(testCases, tracingSink, executionOptions)

type TestHostTraceFramework(messageSink: IMessageSink) =
    inherit XunitTestFramework(messageSink)

    override this.CreateExecutor(assemblyName) =
        new TestHostTraceFrameworkExecutor(assemblyName, this.SourceInformationProvider, this.DiagnosticMessageSink)
        :> ITestFrameworkExecutor
