/// Pre-flight for the unsatisfiable-`global.json` wall (#192).
///
/// `Ionide.ProjInfo.Init.init` resolves the SDK by shelling out to
/// `dotnet --version` **in the target project's directory**, so it inherits that
/// project's `global.json`. When the pin cannot be satisfied the muxer exits 155
/// and ProjInfo raises. Inside this host that exception is caught and degrades to
/// a generic "Unable to load F# project options"; inside `fsautocomplete` — which
/// calls `Init.init` on its own startup path, before it answers `initialize` —
/// nothing catches it, the child dies, and the only thing the host can report is
/// StreamJsonRpc's "The JSON-RPC connection with the remote party was lost",
/// which names neither the SDK nor the `global.json`.
///
/// This module answers the question before anything touches MSBuild, and it is
/// deliberately one-sided: it only ever hard-fails on the single case it can prove
/// (`rollForward: "disable"` + an exact version that is provably not installed).
/// Every uncertainty — no pin, another policy, an `sdk.paths` redirect, an
/// unreadable file, an SDK list we could not obtain — resolves to `Proceed`.
module FsLangMcp.SdkPreflight

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open FsLangMcp.Types

/// The part of a `global.json` `sdk` block this pre-flight can reason about.
type internal SdkPin =
    { Version: string
      RollForward: string option
      GlobalJsonPath: string }

type internal Verdict =
    /// The normal load path may run: nothing here is provably broken.
    | Proceed
    /// A `rollForward: "disable"` pin names a version that is not installed.
    | SdkNotFound of pin: SdkPin * installedSdks: string list

let private describeInstalled (installedSdks: string list) =
    if List.isEmpty installedSdks then
        "none"
    else
        String.concat ", " installedSdks

let internal describe (pin: SdkPin) (installedSdks: string list) =
    $".NET SDK %s{pin.Version} is pinned by %s{pin.GlobalJsonPath} with rollForward \"disable\", "
    + $"but it is not installed (installed SDKs: %s{describeInstalled installedSdks}). "
    + $"Remedy: install the .NET SDK %s{pin.Version}; or relax %s{pin.GlobalJsonPath} — pin an installed "
    + "version, or use a rollForward policy other than \"disable\". FsLangMCP stopped before MSBuild "
    + "project load: fsautocomplete and Ionide.ProjInfo both fail fatally on this pin."

/// House error shape: `status`/`errorKind`/`message` as elsewhere, plus the
/// structured fields an agent needs to act without re-reading `global.json`.
let internal toEnvelope (pin: SdkPin) (installedSdks: string list) : JsonNode =
    jobj
        [ "status", jstr "infrastructure_error"
          "errorKind", jstr "sdk_not_found"
          "message", jstr (describe pin installedSdks)
          "requestedSdkVersion", jstr pin.Version
          "rollForward", jstr (pin.RollForward |> Option.defaultValue "disable")
          "globalJsonPath", jstr pin.GlobalJsonPath
          "installedSdks", JsonArray(installedSdks |> List.map jstr |> List.toArray) :> JsonNode
          "remedies",
          JsonArray(
              [| jstr $"Install the .NET SDK %s{pin.Version}."
                 jstr
                     $"Or edit %s{pin.GlobalJsonPath}: pin an installed SDK version, or replace rollForward \"disable\" with a policy that allows a newer SDK (for example \"latestMajor\")." |]
          )
          :> JsonNode ]
    :> JsonNode

/// Raised from the FCS project-load path. Every tool funnelling through
/// `EnsureProjectResults` therefore reports the same typed envelope (rendered
/// centrally in `Tools.toolResult`) instead of a generic load failure.
type internal SdkNotFoundException(pin: SdkPin, installedSdks: string list) =
    inherit InvalidOperationException(describe pin installedSdks)

    member _.Pin = pin
    member _.InstalledSdks = installedSdks
    member _.Envelope = toEnvelope pin installedSdks

/// The pre-flight raises deep inside the project-load task, so a caller can observe
/// it nested behind an AggregateException or an InnerException. Mirrors FcsBridge's
/// own `findFailureIsTimeout` unwrapping.
let rec private tryFindSdkNotFound (ex: exn) : SdkNotFoundException option =
    match ex with
    | :? SdkNotFoundException as failure -> Some failure
    | :? AggregateException as aggregate -> aggregate.Flatten().InnerExceptions |> Seq.tryPick tryFindSdkNotFound
    | _ when not (isNull ex.InnerException) -> tryFindSdkNotFound ex.InnerException
    | _ -> None

/// Pattern form for the exception handlers that classify a caught failure.
[<return: Struct>]
let internal (|SdkPinUnsatisfiable|_|) (ex: exn) =
    match tryFindSdkNotFound ex with
    | Some failure -> ValueSome failure
    | None -> ValueNone

/// Nearest `global.json` at or above `startDirectory`, matching the muxer's own
/// search order (closest ancestor wins).
let internal tryFindGlobalJson (startDirectory: string) : string option =
    let rec walk (current: DirectoryInfo option) =
        match current with
        | None -> None
        | Some directory ->
            let candidate = Path.Combine(directory.FullName, "global.json")

            if File.Exists candidate then
                Some candidate
            else
                walk (Option.ofObj directory.Parent)

    if String.IsNullOrWhiteSpace startDirectory then
        None
    else
        try
            walk (Some(DirectoryInfo(Path.GetFullPath startDirectory)))
        with _ ->
            // A path the OS refuses to canonicalise decides nothing; the normal
            // load path will surface whatever the real problem is.
            None

let private readTrimmedString (node: JsonNode) (name: string) =
    match node[name] with
    | null -> None
    | value ->
        try
            value.GetValue<string>()
            |> Option.ofObj
            |> Option.map (fun raw -> raw.Trim())
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
        with _ ->
            // Wrong JSON type for the field — not a pin we can reason about.
            None

/// Reads the pin, or `None` whenever the file decides nothing: unreadable,
/// invalid JSON, no `sdk.version`, or an `sdk.paths` block — the latter redirects
/// SDK resolution to locations `dotnet --list-sdks` does not enumerate, so absence
/// there would not prove absence for the muxer.
let internal tryReadPin (globalJsonPath: string) : SdkPin option =
    try
        let documentOptions =
            JsonDocumentOptions(CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)

        match JsonNode.Parse(File.ReadAllText globalJsonPath, documentOptions = documentOptions) with
        | null -> None
        | root ->
            match root["sdk"] with
            | null -> None
            | sdk when not (isNull sdk["paths"]) -> None
            | sdk ->
                readTrimmedString sdk "version"
                |> Option.map (fun version ->
                    { Version = version
                      RollForward = readTrimmedString sdk "rollForward"
                      GlobalJsonPath = globalJsonPath })
    with _ ->
        // Missing, locked, or malformed global.json: the pre-flight abstains rather
        // than blocking a project the real SDK resolver might load fine.
        None

/// Mirrors `Ionide.ProjInfo.Paths.dotnetRoot`: DOTNET_HOST_PATH, then
/// DOTNET_ROOT[(x86)], then the bare name for PATH resolution. Probing the same
/// binary the loader will use keeps our SDK list and MSBuild's view of the machine
/// in agreement.
let internal resolveDotnetBinary () : string =
    let binaryName =
        if OperatingSystem.IsWindows() then
            "dotnet.exe"
        else
            "dotnet"

    let fromEnvironment (variable: string) (toBinaryPath: string -> string) =
        match Environment.GetEnvironmentVariable(variable) with
        | value when String.IsNullOrWhiteSpace value -> None
        | value ->
            let candidate = toBinaryPath value
            if File.Exists candidate then Some candidate else None

    fromEnvironment "DOTNET_HOST_PATH" id
    |> Option.orElseWith (fun () -> fromEnvironment "DOTNET_ROOT" (fun root -> Path.Combine(root, binaryName)))
    |> Option.orElseWith (fun () -> fromEnvironment "DOTNET_ROOT(x86)" (fun root -> Path.Combine(root, binaryName)))
    |> Option.defaultValue binaryName

let private listSdksTimeout = TimeSpan.FromSeconds(20.0)

/// Parses `dotnet --list-sdks` lines of the form `10.0.400 [/path/to/sdk]`.
let internal parseInstalledSdks (stdout: string) : string list =
    stdout.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.choose (fun line ->
        match line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries) with
        | [||] -> None
        | parts -> Some parts[0])
    |> Array.toList

/// `dotnet --list-sdks` does not read `global.json`, so it stays answerable from
/// inside the very directory whose pin we are about to reject. `None` means "could
/// not determine" and always resolves to `Proceed` upstream.
let internal enumerateInstalledSdks () : string list option =
    try
        let result =
            ProcessRunner.run (resolveDotnetBinary ()) [ "--list-sdks" ] listSdksTimeout

        if result.ExitCode <> 0 then
            None
        else
            Some(parseInstalledSdks result.StandardOutput)
    with _ ->
        // No dotnet on PATH, a hung muxer, a restricted host: our own probe must
        // never become the reason a project fails to load.
        None

/// Pure verdict for one directory, given a lister for the installed SDK set.
let internal verdictForDirectory (listInstalledSdks: unit -> string list option) (directory: string) : Verdict =
    match tryFindGlobalJson directory |> Option.bind tryReadPin with
    | None -> Proceed
    | Some pin ->
        // Only "disable" makes the exact version mandatory. Every other policy —
        // including an absent one, which defaults to latestPatch — can still
        // resolve to a different installed SDK, so it is not ours to reject.
        let pinIsExact =
            pin.RollForward
            |> Option.exists (fun policy -> String.Equals(policy, "disable", StringComparison.OrdinalIgnoreCase))

        if not pinIsExact then
            Proceed
        else
            match listInstalledSdks () with
            | None -> Proceed
            | Some installed ->
                let satisfied =
                    installed
                    |> List.exists (fun sdk -> String.Equals(sdk, pin.Version, StringComparison.OrdinalIgnoreCase))

                if satisfied then Proceed else SdkNotFound(pin, installed)

/// First provable failure across several directories. `set_project` passes the
/// selected project's directory (what `Init.init` sees), the workspace-root
/// candidates (what fsautocomplete runs in), and every project a solution lists —
/// a nested `global.json` under one member is just as fatal as one at the root.
let internal verdictFor (listInstalledSdks: unit -> string list option) (directories: string seq) : Verdict =
    directories
    |> Seq.filter (String.IsNullOrWhiteSpace >> not)
    |> Seq.distinct
    |> Seq.tryPick (fun directory ->
        match verdictForDirectory listInstalledSdks directory with
        | SdkNotFound(pin, installed) -> Some(SdkNotFound(pin, installed))
        | Proceed -> None)
    |> Option.defaultValue Proceed

// The probe costs a child process, so the happy path pays for it at most once per
// host process. ValueNone = never probed; ValueSome None = probed, undeterminable.
let private sdkListGate = obj ()
let mutable private cachedSdkList: string list option voption = ValueNone

let private cachedInstalledSdks () =
    lock sdkListGate (fun () ->
        match cachedSdkList with
        | ValueSome cached -> cached
        | ValueNone ->
            let fresh = enumerateInstalledSdks ()
            cachedSdkList <- ValueSome fresh
            fresh)

let private refreshInstalledSdks () =
    lock sdkListGate (fun () ->
        let fresh = enumerateInstalledSdks ()
        cachedSdkList <- ValueSome fresh
        fresh)

/// Cache-then-confirm: a cached list is enough to let a project through, but never
/// enough to reject one. Someone who installs the missing SDK and retries would
/// otherwise have to restart the MCP server before the cache noticed. The extra
/// probe only ever runs on the failing path.
let internal checkWith
    (cachedList: unit -> string list option)
    (refreshedList: unit -> string list option)
    (directories: string seq)
    : Verdict =
    match verdictFor cachedList directories with
    | Proceed -> Proceed
    | SdkNotFound _ ->
        // One refresh per rejection, not one per candidate directory: a solution's
        // members usually share the same global.json and would each re-probe.
        let refreshed = lazy (refreshedList ())
        verdictFor (fun () -> refreshed.Value) directories

/// Production entry point: cached SDK enumeration, one verdict.
let internal check (directories: string seq) : Verdict =
    checkWith cachedInstalledSdks refreshInstalledSdks directories

/// Throwing form for the FCS load path, where the caller has no envelope to return.
let internal ensure (directories: string seq) : unit =
    match check directories with
    | Proceed -> ()
    | SdkNotFound(pin, installed) -> raise (SdkNotFoundException(pin, installed))
