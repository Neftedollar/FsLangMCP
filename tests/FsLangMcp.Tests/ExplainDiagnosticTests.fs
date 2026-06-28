module FsLangMcp.Tests.ExplainDiagnosticTests

// ─── #61: fcs_explain_diagnostic — diagnostic → plain-language explanation + repair ──
//
// The tool resolves a diagnostic code from `code` / `errorNumber` (or a path+position
// auto-fetch) and returns a curated explanation with actionable repair hints. These
// tests exercise the three contract pillars without a project context:
//   1. a known code returns a populated explanation + hints,
//   2. an FS0039 message enriches repairHints with the extracted name + fcs_suggest_open,
//   3. an unknown code degrades gracefully to status=unknown_code, echoing the message.

open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsLangMcp.Types
open FsLangMcp.FcsBridge

// ── JSON helpers ────────────────────────────────────────────────────────────────

let private gs (node: JsonNode) (key: string) = node[key].GetValue<string>()

let private strings (node: JsonNode) (key: string) =
    (node[key] :?> JsonArray)
    |> Seq.map (fun n -> n.GetValue<string>())
    |> Seq.toList

// ── Arg builder ───────────────────────────────────────────────────────────────────

let private bare: FcsExplainDiagnosticArgs =
    { errorNumber = None
      code = None
      message = None
      path = None
      line = None
      character = None
      text = None
      projectPath = None }

// ───────────────────────────────────────────────────────────────────────────────────

type ExplainDiagnosticTests() =

    [<Fact>]
    member _.``a known code returns a populated explanation, causes, hints, and tools``() : Task =
        task {
            let bridge = FcsBridge()

            let! result = bridge.ExplainDiagnostic({ bare with code = Some "FS0025" })

            Assert.Equal("ok", gs result "status")
            Assert.Equal("FS0025", gs result "code")
            Assert.False(System.String.IsNullOrWhiteSpace(gs result "title"))
            Assert.False(System.String.IsNullOrWhiteSpace(gs result "explanation"))
            Assert.NotEmpty(strings result "likelyCauses")
            Assert.NotEmpty(strings result "repairHints")
            // `check` is the companion tool — every entry should route back to it.
            Assert.Contains("check", strings result "relatedTools")
        }

    [<Fact>]
    member _.``errorNumber resolves the same entry as the FS-prefixed code``() : Task =
        task {
            let bridge = FcsBridge()

            let! byNumber = bridge.ExplainDiagnostic({ bare with errorNumber = Some 72 })

            Assert.Equal("ok", gs byNumber "status")
            Assert.Equal("FS0072", gs byNumber "code")
            Assert.Contains("indeterminate", (gs byNumber "title").ToLowerInvariant())
        }

    [<Fact>]
    member _.``code takes precedence over errorNumber when both are supplied``() : Task =
        task {
            let bridge = FcsBridge()

            let! result =
                bridge.ExplainDiagnostic(
                    { bare with
                        code = Some "FS0039"
                        errorNumber = Some 72 }
                )

            Assert.Equal("FS0039", gs result "code")
        }

    [<Fact>]
    member _.``FS0039 with a message enriches repairHints with the name and fcs_suggest_open``() : Task =
        task {
            let bridge = FcsBridge()

            let! result =
                bridge.ExplainDiagnostic(
                    { bare with
                        code = Some "FS0039"
                        message = Some "The value or constructor 'Encoding' is not defined." }
                )

            Assert.Equal("ok", gs result "status")
            let hints = strings result "repairHints"
            // The extracted name and the suggest-open call must appear in some hint.
            Assert.Contains(hints, fun h -> h.Contains("Encoding") && h.Contains("fcs_suggest_open"))
            // The raw message is echoed back on the envelope.
            Assert.Equal("The value or constructor 'Encoding' is not defined.", gs result "message")
        }

    [<Fact>]
    member _.``FS0039 without a message returns base hints and no name-specific enrichment``() : Task =
        task {
            let bridge = FcsBridge()

            let! result = bridge.ExplainDiagnostic({ bare with code = Some "FS0039" })

            Assert.Equal("ok", gs result "status")
            let hints = strings result "repairHints"
            Assert.NotEmpty(hints)
            Assert.DoesNotContain(hints, fun h -> h.Contains("The unresolved name is"))
        }

    [<Fact>]
    member _.``an unknown code degrades gracefully to status=unknown_code``() : Task =
        task {
            let bridge = FcsBridge()

            let! result = bridge.ExplainDiagnostic({ bare with code = Some "FS9999" })

            Assert.Equal("unknown_code", gs result "status")
            Assert.Equal("FS9999", gs result "code")
            Assert.Contains("no curated entry", (gs result "explanation").ToLowerInvariant())
        }

    [<Fact>]
    member _.``an unknown code echoes the supplied raw message``() : Task =
        task {
            let bridge = FcsBridge()

            let! result =
                bridge.ExplainDiagnostic(
                    { bare with
                        code = Some "FS9999"
                        message = Some "some brand new diagnostic text" }
                )

            Assert.Equal("unknown_code", gs result "status")
            Assert.Equal("some brand new diagnostic text", gs result "message")
        }

    [<Fact>]
    member _.``no code, errorNumber, or path is rejected with invalid_args``() : Task =
        task {
            let bridge = FcsBridge()

            let! result = bridge.ExplainDiagnostic(bare)

            Assert.Equal("invalid_args", gs result "status")
        }

    [<Fact>]
    member _.``an unparseable code string is rejected with invalid_args``() : Task =
        task {
            let bridge = FcsBridge()

            let! result = bridge.ExplainDiagnostic({ bare with code = Some "banana" })

            Assert.Equal("invalid_args", gs result "status")
            Assert.Contains("banana", gs result "message")
        }
