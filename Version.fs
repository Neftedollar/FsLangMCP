module FsLangMcp.Version

open System.Reflection

/// Single source of truth for the FsLangMCP product version. Reads the
/// AssemblyInformationalVersion baked in by the SDK from <Version> in the
/// .fsproj at build time. Falls back to AssemblyVersion (X.Y.Z.W) if the
/// informational attribute is missing.
///
/// Exposed via:
///   * MCP serverInfo.version (set in Program.fs at server boot)
///   * set_project response field `fslangmcpVersion`
///   * fsharp_runtime_status response field `fslangmcpVersion`
///   * dedicated fslangmcp_version tool
let current: string =
    let asm = Assembly.GetExecutingAssembly()

    match asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
    | null ->
        match asm.GetName().Version with
        | null -> "0.0.0"
        | v -> v.ToString()
    | attr ->
        // AssemblyInformationalVersion can carry a +commit suffix
        // (e.g. "0.7.0+abc1234") — strip it for clean SemVer surfacing.
        let raw = attr.InformationalVersion

        if System.String.IsNullOrWhiteSpace raw then
            match asm.GetName().Version with
            | null -> "0.0.0"
            | v -> v.ToString()
        else
            match raw.IndexOf('+') with
            | -1 -> raw
            | idx -> raw.Substring(0, idx)

/// Product name surfaced alongside the version. Pulled from AssemblyProduct
/// when present, otherwise falls back to a static "FsLangMCP".
let productName: string =
    let asm = Assembly.GetExecutingAssembly()

    match asm.GetCustomAttribute<AssemblyProductAttribute>() with
    | null -> "FsLangMCP"
    | attr ->
        if System.String.IsNullOrWhiteSpace(attr.Product) then
            "FsLangMCP"
        else
            attr.Product
