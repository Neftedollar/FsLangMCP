module FsLangMcp.Tests.CodeActionTests

// Regression guard for #43 (P1, fixed in PR #98) + the #53 codeAction handshake.
//
// PR #98 fixed a P1: LspBridge.CodeAction attached the SAME JsonNode instance as
// both `range.start` and `range.end`. Each JsonNode has a single Parent reference,
// so the second attach threw InvalidOperationException — every
// `textDocument_codeAction` call failed at construction, before the RPC was even
// sent, and no test caught it. The request-params builder is now extracted to the
// pure `CodeActionRequest.buildParams` seam so the EXACT construction path is
// exercised here deterministically, with no live FSAC (mirrors DiagnosticFixesTests
// / RenamePreviewTests — the suite stays independent of a fsautocomplete install).
//
// No live end-to-end test is included: CI does not provision fsautocomplete, and the
// whole LspBridge test family deliberately tests the pure shaping/building seams
// rather than starting FSAC. The #43 invariant is pure JsonNode object identity, so a
// live process would add nothing the deterministic guards below don't already pin.

open System
open System.Text.Json
open Xunit
open FsLangMcp.LspBridge.CodeActionRequest

// ── #43 regression guard: construction must not throw ────────────────────────────

[<Fact>]
let ``buildParams constructs the codeAction request without throwing (#43)`` () =
    // The original bug threw InvalidOperationException here, at construction time,
    // because one position node was attached as both range.start and range.end.
    let ex = Record.Exception(fun () -> buildParams "file:///Lib.fs" 2 14 |> ignore)
    Assert.Null(ex)

// ── #43 regression guard: start and end are DISTINCT node instances ──────────────

[<Fact>]
let ``buildParams range.start and range.end are separate JsonNode instances (#43)`` () =
    let p = buildParams "file:///Lib.fs" 2 14
    let startNode = p["range"]["start"]
    let endNode = p["range"]["end"]

    Assert.NotNull(startNode)
    Assert.NotNull(endNode)
    // The #43 bug shared ONE instance across both slots. A JsonNode has a single
    // Parent, so reusing it is exactly what threw. Distinct references are the
    // invariant that keeps codeAction constructible.
    Assert.False(Object.ReferenceEquals(startNode, endNode))

// ── Behaviour preservation: positions and uri carry the requested values ─────────

[<Fact>]
let ``buildParams populates both positions and the textDocument uri`` () =
    let p = buildParams "file:///Lib.fs" 7 3
    let uriNode = p["textDocument"]["uri"]
    let startNode = p["range"]["start"]
    let endNode = p["range"]["end"]

    Assert.Equal("file:///Lib.fs", uriNode.GetValue<string>())
    Assert.Equal(7, startNode["line"].GetValue<int>())
    Assert.Equal(3, startNode["character"].GetValue<int>())
    Assert.Equal(7, endNode["line"].GetValue<int>())
    Assert.Equal(3, endNode["character"].GetValue<int>())

// ── Serialization round-trip: distinct start/end + context.diagnostics (#53) ─────

[<Fact>]
let ``buildParams serializes to valid JSON with distinct start/end and a context diagnostics array (#53)`` () =
    let p = buildParams "file:///Lib.fs" 2 14

    // Serializing would itself throw if start/end shared a parent, so the round-trip
    // is a second, independent proof of the #43 invariant on top of the reference check.
    let json = p.ToJsonString()
    use parsed = JsonDocument.Parse(json)
    let root = parsed.RootElement

    Assert.Equal("file:///Lib.fs", root.GetProperty("textDocument").GetProperty("uri").GetString())

    let range = root.GetProperty("range")
    let s = range.GetProperty("start")
    let e = range.GetProperty("end")
    Assert.Equal(2, s.GetProperty("line").GetInt32())
    Assert.Equal(14, s.GetProperty("character").GetInt32())
    Assert.Equal(2, e.GetProperty("line").GetInt32())
    Assert.Equal(14, e.GetProperty("character").GetInt32())

    // #53: the initialize handshake declared codeActionLiteralSupport /
    // publishDiagnostics; FSAC only returns real fixes when context.diagnostics is
    // present (empty here — the DiagnosticFixes wrapper populates it per diagnostic).
    let diagnostics = root.GetProperty("context").GetProperty("diagnostics")
    Assert.Equal(JsonValueKind.Array, diagnostics.ValueKind)
    Assert.Equal(0, diagnostics.GetArrayLength())
