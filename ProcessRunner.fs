module FsLangMcp.ProcessRunner

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks

type internal ProcessOutput =
    { ExitCode: int
      StandardOutput: string
      StandardError: string }

let private cleanupTimeout = TimeSpan.FromSeconds(5.0)

let private observeFault (operation: Task) =
    operation.ContinueWith(
        (fun (faulted: Task) -> faulted.Exception |> ignore),
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted ||| TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default
    )
    |> ignore

let private terminate (proc: Process) (stdoutTask: Task<string>) (stderrTask: Task<string>) =
    task {
        try
            if not proc.HasExited then
                proc.Kill(true)
        with _ ->
            ()

        try
            do! proc.WaitForExitAsync().WaitAsync(cleanupTimeout)
        with _ ->
            ()

        // Observe both tasks after closing the process so pipe failures do not become
        // unobserved exceptions during timeout cleanup.
        let pipesTask = Task.WhenAll(stdoutTask, stderrTask)
        observeFault pipesTask

        try
            let! _ = pipesTask.WaitAsync(cleanupTimeout)
            ()
        with _ ->
            ()
    }

let internal runAsync
    (fileName: string)
    (args: string seq)
    (timeout: TimeSpan)
    (cancellationToken: CancellationToken)
    : Task<ProcessOutput> =
    task {
        if String.IsNullOrWhiteSpace(fileName) then
            invalidArg (nameof fileName) "Process file name must not be blank."

        if timeout <= TimeSpan.Zero then
            invalidArg (nameof timeout) "Process timeout must be positive."

        let psi = ProcessStartInfo()
        psi.FileName <- fileName
        psi.UseShellExecute <- false
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.CreateNoWindow <- true

        for arg in args do
            psi.ArgumentList.Add(arg)

        use proc = new Process(StartInfo = psi)

        if not (proc.Start()) then
            invalidOp $"Unable to start process: %s{fileName}"

        // Start both reads before waiting. Reading either stream synchronously first can
        // deadlock when the child fills the other OS pipe buffer.
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()
        use timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
        timeoutCts.CancelAfter(timeout)

        try
            do! proc.WaitForExitAsync(timeoutCts.Token)
        with :? OperationCanceledException ->
            do! terminate proc stdoutTask stderrTask

            if cancellationToken.IsCancellationRequested then
                raise (OperationCanceledException(cancellationToken))
            else
                raise (
                    TimeoutException($"Process '%s{fileName}' timed out after %d{int64 timeout.TotalMilliseconds}ms.")
                )

        let! stdout = stdoutTask
        let! stderr = stderrTask

        return
            { ExitCode = proc.ExitCode
              StandardOutput = stdout
              StandardError = stderr }
    }

let internal run (fileName: string) (args: string seq) (timeout: TimeSpan) : ProcessOutput =
    runAsync fileName args timeout CancellationToken.None
    |> fun operation -> operation.GetAwaiter().GetResult()
