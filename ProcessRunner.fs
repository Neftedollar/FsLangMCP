module FsLangMcp.ProcessRunner

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks

type internal ProcessOutput =
    { ExitCode: int
      StandardOutput: string
      StandardError: string
      StandardOutputTruncated: bool
      StandardErrorTruncated: bool }

let private cleanupTimeout = TimeSpan.FromSeconds(5.0)
let private defaultOutputLimitCharacters = 4 * 1024 * 1024

let private outputLimitFromEnvironment () =
    match Environment.GetEnvironmentVariable("FSLANGMCP_PROCESS_OUTPUT_LIMIT_CHARS") with
    | value when not (String.IsNullOrWhiteSpace value) ->
        match Int32.TryParse value with
        | true, parsed when parsed > 0 -> parsed
        | _ -> defaultOutputLimitCharacters
    | _ -> defaultOutputLimitCharacters

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private JobObjectBasicLimitInformation =
    val mutable PerProcessUserTimeLimit: int64
    val mutable PerJobUserTimeLimit: int64
    val mutable LimitFlags: uint32
    val mutable MinimumWorkingSetSize: unativeint
    val mutable MaximumWorkingSetSize: unativeint
    val mutable ActiveProcessLimit: uint32
    val mutable Affinity: unativeint
    val mutable PriorityClass: uint32
    val mutable SchedulingClass: uint32

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private IoCounters =
    val mutable ReadOperationCount: uint64
    val mutable WriteOperationCount: uint64
    val mutable OtherOperationCount: uint64
    val mutable ReadTransferCount: uint64
    val mutable WriteTransferCount: uint64
    val mutable OtherTransferCount: uint64

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private JobObjectExtendedLimitInformation =
    val mutable BasicLimitInformation: JobObjectBasicLimitInformation
    val mutable IoInfo: IoCounters
    val mutable ProcessMemoryLimit: unativeint
    val mutable JobMemoryLimit: unativeint
    val mutable PeakProcessMemoryUsed: unativeint
    val mutable PeakJobMemoryUsed: unativeint

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private JobObjectBasicAccountingInformation =
    val mutable TotalUserTime: int64
    val mutable TotalKernelTime: int64
    val mutable ThisPeriodTotalUserTime: int64
    val mutable ThisPeriodTotalKernelTime: int64
    val mutable TotalPageFaultCount: uint32
    val mutable TotalProcesses: uint32
    val mutable ActiveProcesses: uint32
    val mutable TotalTerminatedProcesses: uint32

[<Literal>]
let private JobObjectBasicAccountingInformationClass = 1

[<Literal>]
let private JobObjectExtendedLimitInformationClass = 9

[<Literal>]
let private JobObjectLimitKillOnJobClose = 0x00002000u

[<DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
extern nativeint private CreateJobObject(nativeint jobAttributes, string name)

[<DllImport("kernel32.dll", SetLastError = true)>]
extern bool private SetInformationJobObject(
    nativeint job,
    int informationClass,
    JobObjectExtendedLimitInformation& information,
    uint32 informationLength
)

[<DllImport("kernel32.dll", SetLastError = true)>]
extern bool private AssignProcessToJobObject(nativeint job, nativeint childProcessHandle)

[<DllImport("kernel32.dll", SetLastError = true)>]
extern bool private TerminateJobObject(nativeint job, uint32 exitCode)

[<DllImport("kernel32.dll", SetLastError = true)>]
extern bool private QueryInformationJobObject(
    nativeint job,
    int informationClass,
    JobObjectBasicAccountingInformation& information,
    uint32 informationLength,
    nativeint returnLength
)

[<DllImport("kernel32.dll", SetLastError = true)>]
extern bool private CloseHandle(nativeint handle)

[<DllImport("libc", SetLastError = true)>]
extern int private setsid()

[<DllImport("libc", SetLastError = true)>]
extern int private kill(int pid, int signal)

[<Literal>]
let internal InternalProcessSessionWrapperArgument = "--internal-process-session-wrapper"

type internal UnixSessionWrapperPreference =
    | Automatic
    | Managed

let private executableExists (fileName: string) =
    if Path.IsPathRooted fileName || fileName.Contains(Path.DirectorySeparatorChar) then
        File.Exists fileName
    else
        Environment.GetEnvironmentVariable("PATH")
        |> Option.ofObj
        |> Option.defaultValue ""
        |> fun value -> value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        |> Array.exists (fun directory -> File.Exists(Path.Combine(directory, fileName)))

let internal resolveDotnetHost () =
    let existingEnvironmentPath name =
        Environment.GetEnvironmentVariable(name)
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.filter File.Exists

    let dotnetRootHost =
        Environment.GetEnvironmentVariable("DOTNET_ROOT")
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.map (fun root -> Path.Combine(root, "dotnet"))
        |> Option.filter File.Exists

    existingEnvironmentPath "DOTNET_HOST_PATH"
    |> Option.orElse dotnetRootHost
    |> Option.defaultValue "dotnet"

let private configureManagedUnixSessionWrapper (startInfo: ProcessStartInfo) =
    let assemblyPath = typeof<ProcessOutput>.Assembly.Location

    if String.IsNullOrWhiteSpace assemblyPath || not (File.Exists assemblyPath) then
        invalidOp "Unable to locate FsLangMcp.dll for the managed Unix session wrapper."

    let command = startInfo.FileName
    let arguments = startInfo.ArgumentList |> Seq.toArray
    startInfo.FileName <- resolveDotnetHost ()
    startInfo.ArgumentList.Clear()
    startInfo.ArgumentList.Add(assemblyPath)
    startInfo.ArgumentList.Add(InternalProcessSessionWrapperArgument)
    startInfo.ArgumentList.Add(command)

    for argument in arguments do
        startInfo.ArgumentList.Add(argument)

let private configureUnixSessionWrapper
    (preference: UnixSessionWrapperPreference)
    (startInfo: ProcessStartInfo)
    =
    if OperatingSystem.IsWindows() || not (executableExists startInfo.FileName) then
        false
    else
        let externalSetsid =
            match preference with
            | Managed -> None
            | Automatic -> [| "/usr/bin/setsid"; "/bin/setsid" |] |> Array.tryFind File.Exists

        match externalSetsid with
        | None ->
            configureManagedUnixSessionWrapper startInfo
            true
        | Some wrapper ->
            let command = startInfo.FileName
            let arguments = startInfo.ArgumentList |> Seq.toArray
            startInfo.FileName <- wrapper
            startInfo.ArgumentList.Clear()
            startInfo.ArgumentList.Add("--")
            startInfo.ArgumentList.Add(command)

            for argument in arguments do
                startInfo.ArgumentList.Add(argument)

            true

let private unixProcessGroupHasLiveMembers groupId =
    if OperatingSystem.IsLinux() then
        try
            Directory.EnumerateDirectories("/proc")
            |> Seq.exists (fun processDirectory ->
                let mutable pid = 0
                let name = Path.GetFileName processDirectory

                if not (Int32.TryParse(name, &pid)) then
                    false
                else
                    try
                        let stat = File.ReadAllText(Path.Combine(processDirectory, "stat"))
                        let commandEnd = stat.LastIndexOf(')')

                        if commandEnd < 0 || commandEnd + 2 >= stat.Length then
                            false
                        else
                            let fields =
                                stat.Substring(commandEnd + 2).Split(
                                    ' ',
                                    StringSplitOptions.RemoveEmptyEntries
                                )

                            if fields.Length < 3 then
                                false
                            else
                                let mutable processGroup = 0
                                let state = fields[0]

                                Int32.TryParse(fields[2], &processGroup)
                                && processGroup = groupId
                                && state <> "Z"
                                && state <> "X"
                    with _ ->
                        false)
        with _ ->
            // Fall back to the portable process-group existence probe. EPERM still
            // means the group exists; only ESRCH (3) proves it is gone.
            let result = kill(-groupId, 0)
            result = 0 || Marshal.GetLastPInvokeError() <> 3
    else
        let result = kill(-groupId, 0)
        result = 0 || Marshal.GetLastPInvokeError() <> 3

let private windowsJobHasActiveProcesses handle =
    let mutable information = Unchecked.defaultof<JobObjectBasicAccountingInformation>

    let queried =
        QueryInformationJobObject(
            handle,
            JobObjectBasicAccountingInformationClass,
            &information,
            uint32 (Marshal.SizeOf<JobObjectBasicAccountingInformation>()),
            0n
        )

    if not queried then
        let error = Marshal.GetLastPInvokeError()
        raise (Win32Exception(error, $"Unable to query Windows job membership (error {error})."))

    information.ActiveProcesses <> 0u

type internal ProcessContainment private (childProcess: Process, unixProcessGroup: int option, windowsJob: nativeint option) =
    let mutable disposed = false
    let mutable terminationRequested = false
    let mutable terminationDrained = false
    let childProcessId = childProcess.Id

    let signalContainedProcesses () =
        match unixProcessGroup with
        | Some groupId ->
            // The managed session wrapper may not have completed setsid(2) when
            // termination is first requested. A later signal after the direct
            // wrapper exits closes that launch race without widening containment.
            kill (-groupId, 9) |> ignore
        | None -> ()

        match windowsJob with
        | Some handle -> TerminateJobObject(handle, 1u) |> ignore
        | None -> ()

    let hasLiveContainedProcesses () =
        match unixProcessGroup, windowsJob with
        | Some groupId, _ -> unixProcessGroupHasLiveMembers groupId
        | None, Some handle -> windowsJobHasActiveProcesses handle
        | None, None -> false

    let containmentDescription =
        match unixProcessGroup, windowsJob with
        | Some groupId, _ -> $"Unix process group {groupId}"
        | None, Some _ -> $"Windows job for process {childProcessId}"
        | None, None -> $"process {childProcessId}"

    member _.Process = childProcess

    member _.Terminate() =
        terminationRequested <- true
        signalContainedProcesses ()

        try
            if not childProcess.HasExited then
                childProcess.Kill(true)
        with _ ->
            ()

    member _.WaitForTerminationAsync(timeout: TimeSpan) : Task =
        (task {
            if timeout < TimeSpan.Zero then
                invalidArg (nameof timeout) "Process termination timeout must not be negative."

            let elapsed = Stopwatch.StartNew()

            let remaining () =
                let value = timeout - elapsed.Elapsed

                if value > TimeSpan.Zero then value else TimeSpan.Zero

            if not childProcess.HasExited then
                let directProcessWait = remaining ()

                if directProcessWait = TimeSpan.Zero then
                    raise (
                        TimeoutException(
                            $"Process containment did not drain within {int64 timeout.TotalMilliseconds}ms: process {childProcessId} is still active."
                        )
                    )

                try
                    do! childProcess.WaitForExitAsync().WaitAsync(directProcessWait)
                with :? TimeoutException ->
                    raise (
                        TimeoutException(
                            $"Process containment did not drain within {int64 timeout.TotalMilliseconds}ms: process {childProcessId} is still active."
                        )
                    )

            // Termination can race the managed wrapper's setsid(2): the first
            // group signal then sees no group, while the wrapper creates it just
            // before its own asynchronous kill completes. Once the direct wrapper
            // is reaped, the group identity is stable, so signal it again before
            // checking membership.
            if terminationRequested then
                signalContainedProcesses ()

            // TerminateJobObject and kill(2) initiate termination but do not prove
            // that every contained process has left the OS membership set. Keep the
            // containment handle open and query that set until it is empty.
            let mutable live = hasLiveContainedProcesses ()

            while live && remaining () > TimeSpan.Zero do
                if terminationRequested then
                    signalContainedProcesses ()

                let delay = min (TimeSpan.FromMilliseconds(10.0)) (remaining ())
                do! Task.Delay(delay)
                live <- hasLiveContainedProcesses ()

            if live then
                raise (
                    TimeoutException(
                        $"Process containment did not drain within {int64 timeout.TotalMilliseconds}ms: {containmentDescription} still has active members."
                    )
                )

            terminationDrained <- true
         }
         :> Task)

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true

                if not terminationDrained then
                    this.Terminate()

                match windowsJob with
                | Some handle -> CloseHandle(handle) |> ignore
                | None -> ()

                childProcess.Dispose()

    static member Start(startInfo: ProcessStartInfo, preference: UnixSessionWrapperPreference) =
        let wrappedInUnixSession = configureUnixSessionWrapper preference startInfo
        let childProcess = new Process(StartInfo = startInfo)

        if not (childProcess.Start()) then
            childProcess.Dispose()
            invalidOp $"Unable to start process: %s{startInfo.FileName}"

        let unixProcessGroup =
            if OperatingSystem.IsWindows() then
                None
            elif wrappedInUnixSession then
                Some childProcess.Id
            else
                None

        let windowsJob =
            if not (OperatingSystem.IsWindows()) then
                None
            else
                let handle = CreateJobObject(0n, null)

                if handle = 0n then
                    None
                else
                    let mutable info = Unchecked.defaultof<JobObjectExtendedLimitInformation>
                    info.BasicLimitInformation.LimitFlags <- JobObjectLimitKillOnJobClose

                    let configured =
                        SetInformationJobObject(
                            handle,
                            JobObjectExtendedLimitInformationClass,
                            &info,
                            uint32 (Marshal.SizeOf<JobObjectExtendedLimitInformation>())
                        )

                    if configured && AssignProcessToJobObject(handle, childProcess.Handle) then
                        Some handle
                    else
                        CloseHandle(handle) |> ignore
                        None

        new ProcessContainment(childProcess, unixProcessGroup, windowsJob)

let internal startContainedProcess (startInfo: ProcessStartInfo) =
    ProcessContainment.Start(startInfo, UnixSessionWrapperPreference.Automatic)

let private startContainedProcessWithPreference preference (startInfo: ProcessStartInfo) =
    ProcessContainment.Start(startInfo, preference)

let internal terminateContainedProcess (containment: ProcessContainment) = containment.Terminate()

let private observeFault (operation: Task) =
    operation.ContinueWith(
        (fun (faulted: Task) -> faulted.Exception |> ignore),
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted ||| TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default
    )
    |> ignore

let private readBoundedAsync (reader: TextReader) (limitCharacters: int) =
    task {
        let buffer = Array.zeroCreate<char> 8192
        let retained = StringBuilder(min limitCharacters buffer.Length)
        let mutable truncated = false
        let mutable reading = true

        while reading do
            let! count = reader.ReadAsync(buffer, 0, buffer.Length)

            if count = 0 then
                reading <- false
            else
                let remaining = max 0 (limitCharacters - retained.Length)
                let keep = min remaining count

                if keep > 0 then
                    retained.Append(buffer, 0, keep) |> ignore

                if keep < count then
                    truncated <- true

        if truncated then
            retained.Append($"\n[output truncated at {limitCharacters} characters]") |> ignore

        return retained.ToString(), truncated
    }

let private terminate
    (containment: ProcessContainment)
    (stdoutTask: Task<string * bool>)
    (stderrTask: Task<string * bool>)
    =
    task {
        let proc = containment.Process
        containment.Terminate()

        let! containmentError =
            task {
                try
                    do! containment.WaitForTerminationAsync(cleanupTimeout)
                    return None
                with error ->
                    return Some error
            }

        // A direct child can exit after spawning a descendant that inherited one of
        // the redirected pipe handles. In that case WaitForExitAsync has completed,
        // Kill(entireProcessTree=true) can no longer walk the exited parent, and a
        // ReadToEndAsync would otherwise wait forever for EOF. Close our readers to
        // make pipe cleanup bounded even when the descendant outlives its parent.
        try
            proc.StandardOutput.Close()
        with _ ->
            ()

        try
            proc.StandardError.Close()
        with _ ->
            ()

        // Observe both tasks after closing the process so pipe failures do not become
        // unobserved exceptions during timeout cleanup.
        let pipesTask = Task.WhenAll(stdoutTask, stderrTask)
        observeFault pipesTask

        let! pipeError =
            task {
                try
                    let! _ = pipesTask.WaitAsync(cleanupTimeout)
                    return None
                with
                | :? TimeoutException ->
                    return
                        Some(
                            TimeoutException(
                                $"Process output pipes did not drain within {int64 cleanupTimeout.TotalMilliseconds}ms during cleanup."
                            )
                            :> exn
                        )
                | _ ->
                    // Closing a reader intentionally faults an in-flight read. The
                    // task is observed above; completion, rather than success, is the
                    // cleanup invariant on cancellation and timeout paths.
                    return None
            }

        match containmentError, pipeError with
        | None, None -> ()
        | Some error, None
        | None, Some error -> return raise error
        | Some containmentFailure, Some pipeFailure ->
            return
                raise (
                    AggregateException(
                        "Process containment and redirected-pipe cleanup both failed.",
                        [| containmentFailure; pipeFailure |]
                    )
                )
    }

let private runAsyncWithOutputLimitCore
    (fileName: string)
    (args: string seq)
    (timeout: TimeSpan)
    (cancellationToken: CancellationToken)
    (outputLimitCharacters: int)
    (unixSessionWrapperPreference: UnixSessionWrapperPreference)
    : Task<ProcessOutput> =
    task {
        if String.IsNullOrWhiteSpace(fileName) then
            invalidArg (nameof fileName) "Process file name must not be blank."

        if timeout <= TimeSpan.Zero then
            invalidArg (nameof timeout) "Process timeout must be positive."

        if outputLimitCharacters <= 0 then
            invalidArg (nameof outputLimitCharacters) "Process output limit must be positive."

        let psi = ProcessStartInfo()
        psi.FileName <- fileName
        psi.UseShellExecute <- false
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.CreateNoWindow <- true

        for arg in args do
            psi.ArgumentList.Add(arg)

        // Reject cancellation at the last boundary we control before Process.Start.
        // Cancellation that races after this check can still observe child side effects;
        // the containment cleanup below bounds that case but cannot make launch atomic.
        cancellationToken.ThrowIfCancellationRequested()

        use containment = startContainedProcessWithPreference unixSessionWrapperPreference psi
        let proc = containment.Process

        // Start both reads before waiting. Reading either stream synchronously first can
        // deadlock when the child fills the other OS pipe buffer.
        let stdoutTask = readBoundedAsync proc.StandardOutput outputLimitCharacters
        let stderrTask = readBoundedAsync proc.StandardError outputLimitCharacters
        use timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
        timeoutCts.CancelAfter(timeout)

        let! executionResult =
            task {
                try
                    // The same linked token and CancelAfter deadline covers process exit AND
                    // both redirected-pipe drains. Waiting for the parent alone is insufficient:
                    // a grandchild may keep an inherited stdout/stderr handle open after the
                    // parent exits (#164).
                    do! proc.WaitForExitAsync(timeoutCts.Token)
                    let! stdout, stdoutTruncated = stdoutTask.WaitAsync(timeoutCts.Token)
                    let! stderr, stderrTruncated = stderrTask.WaitAsync(timeoutCts.Token)

                    return
                        Ok
                            { ExitCode = proc.ExitCode
                              StandardOutput = stdout
                              StandardError = stderr
                              StandardOutputTruncated = stdoutTruncated
                              StandardErrorTruncated = stderrTruncated }
                with error ->
                    return Error error
            }

        let mutable cleanupError: exn option = None

        try
            // This also runs after an ordinary zero/nonzero exit. Descendants can
            // redirect their own output, letting both parent pipes reach EOF while
            // they remain in the process group/job; do not release the caller's slot
            // until verified containment membership is empty.
            do! terminate containment stdoutTask stderrTask
        with error ->
            cleanupError <- Some error

        let normalizeExecutionError (error: exn) =
            match error with
            | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                OperationCanceledException(cancellationToken) :> exn
            | :? OperationCanceledException ->
                TimeoutException(
                    $"Process '%s{fileName}' timed out after %d{int64 timeout.TotalMilliseconds}ms."
                )
                :> exn
            | _ -> error

        match executionResult, cleanupError with
        | Ok output, None -> return output
        | Error error, None -> return raise (normalizeExecutionError error)
        | Ok _, Some error -> return raise error
        | Error error, Some containmentFailure ->
            return
                raise (
                    AggregateException(
                        "Process execution and containment cleanup both failed.",
                        [| normalizeExecutionError error; containmentFailure |]
                    )
                )
    }

let internal runAsyncWithOutputLimit
    (fileName: string)
    (args: string seq)
    (timeout: TimeSpan)
    (cancellationToken: CancellationToken)
    (outputLimitCharacters: int)
    : Task<ProcessOutput> =
    runAsyncWithOutputLimitCore
        fileName
        args
        timeout
        cancellationToken
        outputLimitCharacters
        UnixSessionWrapperPreference.Automatic

let internal runAsyncWithManagedUnixSessionWrapper
    (fileName: string)
    (args: string seq)
    (timeout: TimeSpan)
    (cancellationToken: CancellationToken)
    : Task<ProcessOutput> =
    runAsyncWithOutputLimitCore
        fileName
        args
        timeout
        cancellationToken
        (outputLimitFromEnvironment ())
        UnixSessionWrapperPreference.Managed

let internal runUnixSessionWrapper (fileName: string) (args: string array) =
    if OperatingSystem.IsWindows() then
        Console.Error.WriteLine("The internal process-session wrapper is only supported on Unix.")
        64
    elif String.IsNullOrWhiteSpace fileName then
        Console.Error.WriteLine("The internal process-session wrapper requires a command.")
        64
    elif setsid () < 0 then
        Console.Error.WriteLine(
            $"Unable to create a Unix process session (errno %d{Marshal.GetLastPInvokeError()})."
        )

        125
    else
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- fileName
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true

        for arg in args do
            startInfo.ArgumentList.Add(arg)

        try
            use target = new Process(StartInfo = startInfo)

            if not (target.Start()) then
                Console.Error.WriteLine($"Unable to start process: %s{fileName}")
                127
            else
                target.WaitForExit()
                target.ExitCode
        with ex ->
            Console.Error.WriteLine($"Unable to start process '%s{fileName}': %s{ex.Message}")
            127

let internal runAsync
    (fileName: string)
    (args: string seq)
    (timeout: TimeSpan)
    (cancellationToken: CancellationToken)
    : Task<ProcessOutput> =
    runAsyncWithOutputLimit fileName args timeout cancellationToken (outputLimitFromEnvironment ())

let internal run (fileName: string) (args: string seq) (timeout: TimeSpan) : ProcessOutput =
    runAsync fileName args timeout CancellationToken.None
    |> fun operation -> operation.GetAwaiter().GetResult()
