# Languages, language tools, and language packs

AutoEnvPlus models a language separately from the programs used to work with it. A language is
a stable navigation and configuration container. A language tool is a concrete compiler,
interpreter, runtime, SDK, package manager, build system, debugger, formatter, linter, language
server, version manager, or virtual-environment implementation. For example, Python is a language;
CPython and PyPy are tools. C and C++ are languages; MSVC, GCC, Clang, CMake, and Ninja are tools.

The embedded schema 1 catalog contains 45 maintained built-in languages, 136 real tool catalog
entries, 140 tool-scoped Provider Profiles, and 83 Provider source slots. The 140 profiles are
catalog capability metadata, not 140 executable plugins. Exactly the
product Top 10 languages are enabled initially: Python, JavaScript, TypeScript, Java, C, C++, C#,
Go, Rust, and PHP. Other built-in languages remain available and can be surfaced when discovery
finds one of their declared commands. A tool capability is an explicit fact, not a promise that
AutoEnvPlus can install every listed program. There are exactly nine real managed-install adapters:
four official archive tools and the five existing allow-listed WinGet operations for MSVC Build
Tools, Clang, WinLibs/GCC, CMake, and Ninja. `capabilities.install` is true only when a Provider
Profile is backed by one of those adapters.

Language visibility is persisted separately from search and filtering. The effective default set is
the Top 10 plus languages supported by tools found on PATH plus explicitly enabled languages, minus
explicitly hidden languages. `LanguageVisibilityStore` serializes only canonical language IDs under
the managed state root, uses a cross-process transaction lock, and rejects unknown, duplicate, or
overlapping IDs. The application-wide visibility policy changes the initial list presentation; only
an explicit enable, hide, or reset action changes this state. Opening the Languages page only reads
the persisted `language-tool-inventory.json` snapshot; PATH discovery updates it only after an
explicit rescan.

## Provider-owned sources

Mirrors belong to a specific `languageToolId + providerId`; they are never global. A Provider source
slot declares its stable ID, endpoint kind,
official HTTPS endpoint, purpose, and whether a user may override it. This lets a CPython Provider
expose a PyPI index, a Node.js Provider expose an npm registry, and a .NET SDK Provider expose a
NuGet feed without pretending that mirror and proxy settings are one global feature. The language
detail page stores overrides against exact tool, Provider, and slot IDs and supports additional
named HTTPS sources. HTTP proxy and `NO_PROXY` remain transport policy rather than mirrors and are
configured only in application settings.

## Data-only language packs

[`language-pack.schema.json`](../schemas/language-pack.schema.json) defines schema 1. A pack may add
new language definitions and language tools, or add tools that refer to an existing built-in
language. It cannot replace a built-in or already imported ID. Imported packs are disabled by
default and must be explicitly enabled before their entries join the effective catalog.

Language packs are deliberately data-only. The parser rejects unknown or duplicate fields and the
schema has no DLL, script, arbitrary command, install hook, registry mutation, environment mutation,
or executable download field. Discovery commands are bare executable names used only for PATH
inspection; they are never executed as shell text. All URIs must use HTTPS and cannot contain
credentials, query strings, or fragments. Imported files, state, and lock paths reject reparse
points, and manifest and entry counts are bounded.

The template is [`language-pack.template.json`](../examples/language-pack.template.json). It uses a
manual Provider so it accurately reports discovery without claiming managed installation.

## Managed adapter and plugin bridge

`LanguageToolRuntimeBridge` maps the nine real managed-install adapters and official Provider IDs
into the hierarchy. Python maps to CPython; Node.js maps to the Node.js tool under JavaScript and
TypeScript; Adoptium maps to Eclipse Temurin; .NET maps to the .NET SDK. C/C++ adapters map to MSVC
Build Tools, Clang, GCC, CMake, and Ninja under their language pages. Declarative Runtime Provider
plugin schema 2 binds one of those tools directly with `languageToolId`; legacy schema 1
`runtimeKind` manifests remain import-compatible and are normalized to schema 2. Provider IDs
(`plugin:<id>`) follow the same exact tool mapping, including C/C++ ZIP Providers.
`WindowsSdkInstallation` discovery evidence maps separately to the `windows-sdk` tool. The current
WinGet allow-list has no standalone Windows SDK operation, so that tool intentionally reports
discovery support without claiming independent managed installation.
