module FsLangMcp.Docs.Site

open System
open Feliz.ViewEngine
open Nacara.Core
open Nacara.Plugins
open Nacara.Theme

[<Literal>]
let RepositoryUrl = "https://github.com/Neftedollar/FsLangMCP"

[<Literal>]
let SiteOrigin = "https://neftedollar.com"

[<Literal>]
let SiteBaseUrl = "/FsLangMCP/"

let private metaProperty name content =
    Html.meta
        [
            prop.custom ("property", name)
            prop.content content
        ]

let private metaName name content =
    Html.meta
        [
            prop.name name
            prop.content content
        ]

let theme =
    Theme.defaults
    |> Theme.navbar
        [
            NavbarSection("Get started", "guide", "getting-started.md")
            NavbarSection("Tools", "tools", "tools-reference.md")
            NavbarSection("Why semantic F#", "concepts", "why-agents-grep-fsharp.md")
            NavbarLink("Troubleshooting", "/guide/troubleshooting/")
            NavbarLink("Release notes", "/changelog/releases/")
        ]
    |> Theme.navbarEnd
        [
            NavbarDynamicWidget Search.trigger
            NavbarIcon("GitHub", RepositoryUrl, Icons.github)
        ]
    |> Theme.menu
        "guide"
        [
            Menu.section
                "Start here"
                [
                    Menu.page "getting-started.md"
                    Menu.page "configuration.md"
                ]
            Menu.section "When something goes wrong" [ Menu.page "troubleshooting.md" ]
        ]
    |> Theme.menu
        "tools"
        [
            Menu.section
                "Tool surface"
                [
                    Menu.page "tools-reference.md"
                    Menu.page "tools-detailed.md"
                ]
        ]
    |> Theme.menu
        "concepts"
        [
            Menu.section "The case for semantics" [ Menu.page "why-agents-grep-fsharp.md" ]
        ]
    |> Theme.editUrl $"%s{RepositoryUrl}/edit/main/docs"
    |> Theme.favIcon "/favicon.svg"
    |> Theme.headExtra
        [
            metaProperty "og:image" $"%s{SiteOrigin}%s{SiteBaseUrl}og.png"
            metaProperty "og:image:width" "1280"
            metaProperty "og:image:height" "640"
            metaProperty "og:image:alt" "FsLangMCP — Stop grepping F#. Give your AI agent the compiler."
            metaName "twitter:image" $"%s{SiteOrigin}%s{SiteBaseUrl}og.png"
            metaName "twitter:image:alt" "FsLangMCP — semantic F# for AI coding agents"
        ]
    |> Theme.footer (
        Html.p
            [
                Html.text "FsLangMCP is open source under MIT · "
                Html.a
                    [
                        prop.href RepositoryUrl
                        prop.text "Source on GitHub"
                    ]
            ]
    )

let private publicPages =
    [
        "index.md"
        "getting-started.md"
        "configuration.md"
        "troubleshooting.md"
        "tools-reference.md"
        "tools-detailed.md"
        "why-agents-grep-fsharp.md"
    ]

let private pageRoutes =
    Map
        [
            "index.md", ""
            "getting-started.md", "guide/getting-started"
            "configuration.md", "guide/configuration"
            "troubleshooting.md", "guide/troubleshooting"
            "tools-reference.md", "tools/reference"
            "tools-detailed.md", "tools/details"
            "why-agents-grep-fsharp.md", "concepts/why-semantic-fsharp"
        ]

let private routePage (page: PageInfo<DocFrontMatter>) =
    let path = RelativePath.value page.RelativePath

    pageRoutes
    |> Map.tryFind path
    |> Option.map (Route.ofPath page.Locale)
    |> Option.defaultValue (Collection.defaultRoute page)

let content =
    Theme.docs theme "docs"
    |> Collection.source "." publicPages
    |> Collection.route routePage

let private changelogSources =
    [
        ChangelogSource.create "FsLangMCP" "../CHANGELOG.md"
        |> ChangelogSource.slug "releases"
    ]

let changelog =
    Changelog.collection "changelog" DocFrontMatter.decoder changelogSources
    |> Collection.title _.Title
    |> Collection.routePrefix "changelog"
    |> Collection.layout (Theme.layout theme)

let site =
    Site.create "FsLangMCP"
    |> Site.description "Semantic F# tools for AI coding agents, powered by the compiler rather than text search."
    |> Site.origin SiteOrigin
    |> Site.baseUrl SiteBaseUrl
    |> Site.output "output"
    |> Site.staticFiles "static"
    |> Site.stylesheet "assets/site.css"
    |> Site.script "assets/site.js"
    |> Markdown.registerWith (fun options ->
        { options with
            GithubRepo = Some "Neftedollar/FsLangMCP"
            StrictLinks = true
            WarnOnUnknownLanguage = true
        }
    )
    |> TreeSitter.register
    |> Changelog.registerWith "changelog" changelogSources
    |> Search.register
    |> Sitemap.register
    |> LinkValidator.registerWith (fun options ->
        { options with
            CheckExternal = Environment.GetEnvironmentVariable "FSLANGMCP_DOCS_CHECK_EXTERNAL" = "1"
            FailOnExternal = false
            Ignore = [ @"^https://neftedollar\.com/FsLangMCP/" ]
        }
    )
    |> LightningCss.register
    |> Nuglify.minifyHtml
    |> GitHubPages.register
    |> Theme.register theme
    |> Site.collection content
    |> Site.collection changelog

[<EntryPoint>]
let main argv = Nacara.run site argv
