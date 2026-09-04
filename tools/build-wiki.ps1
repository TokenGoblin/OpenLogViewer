<#
.SYNOPSIS
    Builds a GitHub or GitLab wiki from docs/.

.DESCRIPTION
    The documentation set under docs/ is the source. This produces the wiki as a
    build artifact from it, rather than a second copy anybody has to keep in
    step — which is the failure this exists to avoid. A wiki edited in the
    browser is a fork nobody notices until the two disagree.

    What it changes, and nothing else:

      - File names. A wiki has one flat namespace and takes its page title from
        the file name, so docs/tune-editing.md becomes Editing-a-tune.md and
        shows as "Editing a tune".

      - Links. docs/ links to "user-guide.md#export"; a wiki wants
        "User-guide#export". Links out of docs/ — the licence, the changelog —
        have no wiki equivalent at all and become absolute repository URLs.

      - Navigation. A sidebar, and on GitHub a footer, neither of which docs/
        needs because GitHub renders its directory listing.

    The page text itself is copied through untouched. If something reads wrong
    on the wiki, fix it in docs/ and run this again.

.PARAMETER Flavour
    GitHub or GitLab. They differ in three places: the home page is Home.md
    against home.md, the sidebar is _Sidebar.md against _sidebar.md, and GitLab
    has no footer, so the "edit in docs/, not here" note moves into its sidebar.

.PARAMETER Check
    Verify the committed wiki/ matches what docs/ would produce, and fail if it
    does not. For CI, so the two cannot drift silently.

.EXAMPLE
    .\build-wiki.ps1
    .\build-wiki.ps1 -Flavour GitLab -OutputDirectory ..\OpenLogViewer.wiki
    .\build-wiki.ps1 -Check
#>

[CmdletBinding()]
param(
    [ValidateSet('GitHub', 'GitLab')]
    [string] $Flavour = 'GitHub',

    [string] $OutputDirectory,

    [string] $RepositoryUrl = 'https://github.com/TokenGoblin/OpenLogViewer',

    [switch] $Check
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$docs = Join-Path $root 'docs'

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'wiki' }

# The page each document becomes.
#
# Explicit rather than derived from the heading, because the headings carry
# punctuation a wiki page name should not: "AI agent access (MCP)" and "Firmware
# definitions, channels and roles" would become page names with brackets and a
# comma in them, which every link then has to URL-encode. The heading inside the
# page is left exactly as it is.
$pages = [ordered] @{
    'README.md'                = 'Home'
    'getting-started.md'       = 'Getting-started'
    'installation.md'          = 'Installation'
    'user-guide.md'            = 'User-guide'
    'histogram-and-scatter.md' = 'Histogram-and-scatter'
    've-calibration.md'        = 'VE-calibration'
    'calculated-channels.md'   = 'Calculated-channels'
    'live-connection.md'       = 'Live-connection'
    'obd2.md'                  = 'OBD2'
    'subaru-ssm.md'            = 'Subaru-SSM'
    'tune-editing.md'          = 'Editing-a-tune'
    'configuration.md'         = 'Configuration'
    'command-line.md'          = 'Command-line'
    'troubleshooting.md'       = 'Troubleshooting'
    'mcp-server.md'            = 'AI-agent-access-MCP'
    'ini-and-channels.md'      = 'Firmware-definitions-and-channels'
    'mlg-format.md'            = 'MLG-log-format'
    'architecture.md'          = 'Architecture'
    'development.md'           = 'Development'
}

# Where a repository file lives. GitLab's canonical path carries a "/-/" segment
# that GitHub's does not; GitLab redirects the GitHub form, but a generated link
# should not rely on somebody else's redirect still being there.
$blob = if ($Flavour -eq 'GitLab') { "$RepositoryUrl/-/blob/main" } else { "$RepositoryUrl/blob/main" }
$tree = if ($Flavour -eq 'GitLab') { "$RepositoryUrl/-/tree/main" } else { "$RepositoryUrl/tree/main" }

# Links that leave docs/ and have nowhere to land in a wiki.
$external = @{
    '../LICENSE'                = "$blob/LICENSE"
    '../CHANGELOG.md'           = "$blob/CHANGELOG.md"
    '../THIRD-PARTY-NOTICES.md' = "$blob/THIRD-PARTY-NOTICES.md"
}

# The repository this documentation was written against. Where it is being
# published somewhere else, the URLs written into the prose — the clone command,
# the issues link — have to move too, and those are not links this rewrites.
$writtenAgainst = 'https://github.com/TokenGoblin/OpenLogViewer'

# The sidebar's running order. Grouped the way docs/README.md groups them,
# because somebody who has read one should recognise the other.
$sections = [ordered] @{
    'Start here'      = @('Home', 'Getting-started', 'Installation', 'User-guide')
    'Analysis'        = @('Histogram-and-scatter', 'VE-calibration', 'Calculated-channels')
    'Live connection' = @('Live-connection', 'OBD2', 'Subaru-SSM', 'Editing-a-tune')
    'Reference'       = @('Configuration', 'Command-line', 'Troubleshooting',
                          'AI-agent-access-MCP', 'Firmware-definitions-and-channels',
                          'MLG-log-format')
    'Developers'      = @('Architecture', 'Development')
}

# Sidebar titles that do not fall out of the page name. A page name cannot carry
# brackets without every link having to encode them, but a sidebar entry can.
$titles = @{
    'Home'               = 'Documentation home'
    'AI-agent-access-MCP' = 'AI agent access (MCP)'
    'OBD2'               = 'OBD2'
    'VE-calibration'     = 'VE calibration'
    'Subaru-SSM'         = 'Subaru SSM'
    'MLG-log-format'     = 'MLG log format'
}

# What a page is called when it is written out and when it is linked to. GitHub
# capitalises Home and _Sidebar; GitLab does not.
function Get-FileName([string] $page) {
    if ($Flavour -eq 'GitLab' -and $page -eq 'Home') { return 'home.md' }
    return "$page.md"
}

function Get-LinkTarget([string] $page) {
    if ($Flavour -eq 'GitLab' -and $page -eq 'Home') { return 'home' }
    return $page
}

# Turns one document's body into a wiki page's body.
#
# Only links are touched. A link is rewritten when it points at another document
# in the set, or out of docs/ to a file in the repository; a bare "#anchor"
# within the same page, and anything already absolute, is left alone.
function Convert-Links([string] $text) {
    $result = [regex]::Replace($text, '(?<=\]\()([^)\s]+)(?=\))', {
        param($match)

        $target = $match.Groups[1].Value

        if ($target -match '^(https?:|mailto:|#)') { return $target }

        # Split the anchor off, rewrite the path, put the anchor back. Anchors
        # survive as they are: a wiki derives them from the headings the same
        # way, and the headings have not moved.
        $path = $target
        $anchor = ''

        if ($target.Contains('#')) {
            $split = $target.Split('#', 2)
            $path = $split[0]
            $anchor = '#' + $split[1]
        }

        if ($external.ContainsKey($path)) { return $external[$path] + $anchor }

        if ($pages.Contains($path)) { return (Get-LinkTarget $pages[$path]) + $anchor }

        # Anything else points at something the wiki does not carry — a source
        # file, a folder. Send it to the repository rather than leaving a link
        # that resolves to a wiki page nobody wrote.
        $clean = $path -replace '^\.\./', ''
        return "$blob/$clean$anchor"
    })

    return $result
}

function Build-Sidebar {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('### OpenLogViewer')
    $lines.Add('')

    foreach ($section in $sections.Keys) {
        $lines.Add("**$section**")
        $lines.Add('')

        foreach ($page in $sections[$section]) {
            if ($titles.ContainsKey($page)) { $title = $titles[$page] }
            else { $title = $page -replace '-', ' ' }
            $lines.Add("- [$title]($(Get-LinkTarget $page))")
        }

        $lines.Add('')
    }

    $lines.Add("[Repository]($RepositoryUrl)")

    # GitLab has no footer page, so the warning that matters most has to go
    # somewhere a person editing the wiki will actually see it.
    if ($Flavour -eq 'GitLab') {
        $lines.Add('')
        $lines.Add('---')
        $lines.Add('')
        $lines.Add("_Generated from ``docs/`` in the repository. Edit there, not here._")
    }

    return ($lines -join "`n") + "`n"
}

function Build-Footer {
    return @"
---

Generated from [``docs/``]($tree/docs) in the repository.
**Edit the documentation there, not here** — an edit made in this wiki is
overwritten the next time it is built.
"@ + "`n"
}

# Line endings, flattened to LF.
#
# Not cosmetic. .gitattributes stores markdown with LF and checks it out native,
# so on Windows docs/ arrives as CRLF while the sidebar and footer are built here
# with LF — and -Check, which compares the two as text, would report a wiki that
# is perfectly correct as out of date. Normalising both sides means the check
# tests the content and not the checkout.
function Convert-Endings([string] $text) {
    return $text.Replace("`r`n", "`n").Replace("`r", "`n")
}

# Everything the wiki should contain, as name → text. Built in memory first so
# -Check can compare without writing anything.
function Build-Wiki {
    $built = [ordered] @{}

    foreach ($source in $pages.Keys) {
        $path = Join-Path $docs $source

        if (-not (Test-Path $path)) { throw "docs/$source is missing." }

        # Read through .NET rather than Get-Content, which in Windows PowerShell
        # defaults to the system codepage for a file with no byte-order mark —
        # and every page here is UTF-8 without one. That read turned every "—"
        # into "â€"" and every "●" into two characters of nonsense, consistently
        # enough that -Check compared one corrupted copy against another and
        # reported them identical.
        $body = [System.IO.File]::ReadAllText($path)

        # Before the links, so a rewritten link is not rewritten twice.
        if ($RepositoryUrl -ne $writtenAgainst) {
            $body = $body.Replace($writtenAgainst, $RepositoryUrl)
        }

        $built[(Get-FileName $pages[$source])] = Convert-Endings (Convert-Links $body)
    }

    $sidebar = if ($Flavour -eq 'GitLab') { '_sidebar.md' } else { '_Sidebar.md' }
    $built[$sidebar] = Convert-Endings (Build-Sidebar)

    if ($Flavour -eq 'GitHub') { $built['_Footer.md'] = Convert-Endings (Build-Footer) }

    return $built
}

$wiki = Build-Wiki

# UTF-8 without a BOM. A wiki renderer copes either way, but a BOM shows up as a
# stray character in the first heading on some of them.
$utf8 = New-Object System.Text.UTF8Encoding($false)

if ($Check) {
    $problems = New-Object System.Collections.Generic.List[string]

    foreach ($name in $wiki.Keys) {
        $path = Join-Path $OutputDirectory $name

        if (-not (Test-Path $path)) {
            $problems.Add("missing: $name")
            continue
        }

        $current = Convert-Endings ([System.IO.File]::ReadAllText($path))
        if ($current -ne $wiki[$name]) { $problems.Add("out of date: $name") }
    }

    if (Test-Path $OutputDirectory) {
        foreach ($file in Get-ChildItem $OutputDirectory -Filter *.md) {
            if (-not $wiki.Contains($file.Name)) { $problems.Add("not from docs/: $($file.Name)") }
        }
    }

    if ($problems.Count -gt 0) {
        Write-Host "The wiki does not match docs/:" -ForegroundColor Red
        foreach ($p in $problems) { Write-Host "  $p" -ForegroundColor Red }
        Write-Host ""
        Write-Host "Run tools\build-wiki.ps1 and commit the result." -ForegroundColor Yellow
        exit 1
    }

    Write-Host "The wiki matches docs/. $($wiki.Count) pages." -ForegroundColor Green
    exit 0
}

if (Test-Path $OutputDirectory) {
    Get-ChildItem $OutputDirectory -Filter *.md | Remove-Item -Force
}
else {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

foreach ($name in $wiki.Keys) {
    [System.IO.File]::WriteAllText((Join-Path $OutputDirectory $name), $wiki[$name], $utf8)
}

Write-Host "$Flavour wiki: $($wiki.Count) pages in $OutputDirectory" -ForegroundColor Cyan
foreach ($name in $wiki.Keys) { Write-Host "  $name" -ForegroundColor DarkGray }
