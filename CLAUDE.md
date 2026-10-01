# Mavue - Claude Code Development Rules

## 1. Project Identity

Mavue is a Windows desktop application intended to provide the functionality and comfort of macOS Preview + Finder Quick Look on Windows 11.

The goal is not to build a minimal viewer. Mavue should become a comprehensive native Windows document, image, PDF, preview, annotation, conversion, scanning, printing, and Explorer-integration application.

The primary development environment is a physical Windows 11 PC:
- CPU: AMD Ryzen 7 7800X3D
- GPU: NVIDIA GeForce RTX 4080 SUPER
- OS: Windows 11

## 2. Highest-Priority Development Rule

Do not remove, silently omit, simplify away, or permanently defer a feature unless:
1. It is explicitly listed in the exclusion section of `docs/SPEC.md`, or
2. The user explicitly requests that it be removed, deferred, or changed.

If a feature is technically difficult, investigate an appropriate implementation rather than silently dropping it.

`docs/SPEC.md` is the product-level source of truth.

## 3. Explicit Initial Exclusions

The following are explicitly excluded from the initial implementation:

- Video trimming
- Copy Subject
- Live Photo
- EPS
- OpenEXR
- Lasso
- Smart Lasso
- All 3D functionality:
  - USD
  - USDZ
  - OBJ
  - STL
  - glTF
  - GLB
  - PLY
  - Alembic
  - MaterialX
  - OpenVDB
  - Gaussian Splat
  - 3D viewing/editing/animation/annotations/export
- Soft Proof
- ColorSync
- Vision Pro / Apple-specific 3D integration
- PDF → Word
- PDF → Excel
- PDF → PowerPoint

PDF → Office conversion is a future/backlog item, not a permanently prohibited feature.

## 4. Default Technology Stack

Prefer:

- C#
- .NET
- WinUI 3
- Windows App SDK

Native technologies are allowed and encouraged where Windows integration requires them:

- C++
- Win32
- COM
- Windows Shell APIs
- Windows Runtime
- Windows Imaging Component (WIC)
- Windows Ink
- WIA
- other appropriate Windows APIs

Do not force everything into managed code if doing so produces inferior Windows integration or performance.

Avoid Electron/Python as the primary application architecture unless a specific subsystem has a compelling reason.

## 5. Architecture

Prefer a modular architecture similar to:

- `Mavue.App`
- `Mavue.Core`
- `Mavue.Pdf`
- `Mavue.Image`
- `Mavue.Ocr`
- `Mavue.Markup`
- `Mavue.QuickView`
- `Mavue.Shell`
- `Mavue.Print`
- `Mavue.Scan`
- `Mavue.Metadata`
- `Mavue.Codecs`
- tests

Keep Windows-specific integrations isolated where practical.

Design APIs so components can be replaced without rewriting the entire application.

## 6. Quick View Is a Critical Feature

Quick View is one of Mavue's primary differentiators.

The target interaction is:

1. User selects a file in Windows Explorer.
2. User presses Space.
3. Mavue displays a preview as quickly as practical.

Investigate architectures such as:

- lightweight Quick View process
- preloaded components
- lazy initialization
- shared caches
- thumbnail/preview caches
- IPC
- memory-mapped or shared data where appropriate
- native shell integration

Measure startup latency instead of assuming performance.

Quick View must support navigation between selected files where technically appropriate.

## 7. Windows Explorer Integration

Treat Windows Explorer integration as a first-class feature, not an optional add-on.

Investigate and implement as appropriate:

- Space-key Quick View
- Explorer context menu
- “Open in Mavue”
- “Mavue Quick View”
- file associations
- PDF association
- image associations
- Preview Pane integration
- Preview Handler
- Thumbnail Provider
- shell extensions
- drag & drop
- clipboard integration
- Windows Search integration
- contextual PDF/image operations

Test these features on a real Windows 11 system.

## 8. Performance

Performance is a product requirement.

Pay particular attention to:

- Quick View startup latency
- large images
- large PDFs
- multi-page PDFs
- RAW images
- high-resolution displays
- high-DPI scaling
- GPU acceleration
- memory usage
- background decoding
- thumbnail generation
- cache behavior

Provide CPU/software fallbacks where GPU acceleration is unavailable or unreliable.

Do not load unnecessarily large resources during application startup.

## 9. Large Files and Stability

The application must be designed for large files and long-running sessions.

Avoid:

- loading entire files into memory unnecessarily
- blocking the UI thread
- synchronous expensive decoding on UI startup
- destructive edits without a recovery path

Use:

- streaming
- asynchronous processing
- cancellation
- background workers
- incremental rendering
- safe temporary files
- crash recovery where appropriate

## 10. PDF Security

PDF security features require real semantic behavior.

For example, redaction must actually remove the underlying content rather than simply drawing a black rectangle over it.

Be careful with:

- password-protected PDFs
- encrypted PDFs
- restricted PDFs
- annotation flattening
- metadata removal
- document rewriting

Never describe a visual masking operation as true redaction.

## 11. Editing and Data Safety

Prefer non-destructive editing internally where practical.

Support:

- Undo
- Redo
- safe Save
- Save As
- Export
- temporary files
- atomic replacement where appropriate
- recovery after crashes

Do not overwrite the original file until the operation has completed successfully.

## 12. Dependencies and Licensing

Before selecting major third-party libraries:

- investigate their licenses
- investigate transitive dependencies
- check redistribution requirements
- check commercial-use restrictions
- check native binary redistribution requirements
- record decisions in `docs/DEPENDENCIES.md`

Do not assume a library is safe to redistribute simply because its source is publicly available.

Prefer well-maintained projects with clear licensing.

## 13. Security and Privacy

The application should operate locally by default.

Do not send user files to external services unless explicitly required and clearly disclosed.

Avoid unnecessary telemetry.

Treat:

- PDF contents
- images
- EXIF/GPS metadata
- OCR text
- signatures
- scanned documents

as potentially sensitive.

Do not log document contents or personal metadata unnecessarily.

## 14. Localization and Accessibility

Initial languages:

- Japanese
- English

Avoid hard-coded user-facing strings.

Support:

- high DPI
- multiple monitors
- keyboard navigation
- screen readers where practical
- touch
- Windows Ink
- scalable UI

## 15. Research Rules

When an implementation depends on Windows APIs, file formats, shell behavior, or third-party libraries:

- investigate official documentation first
- verify current API behavior
- identify Windows-version limitations
- identify architecture limitations
- identify licensing constraints
- document important decisions

Do not invent APIs or assume macOS behavior maps directly to Windows.

## 16. Testing

Testing must include:

- unit tests
- integration tests
- file-format tests
- PDF tests
- image tests
- OCR tests
- shell integration tests
- Quick View tests
- regression tests
- performance tests where appropriate

Real Windows testing is required for:

- Explorer integration
- Shell extensions
- Preview Handler
- Thumbnail Provider
- Windows Ink
- printing
- scanning
- high-DPI behavior
- multi-monitor behavior

## 17. Build and Git

The repository should build reproducibly.

Document:

- required SDKs
- Visual Studio requirements
- Windows SDK requirements
- build commands
- test commands
- packaging
- installation

Keep commits logically scoped.

Do not commit generated build artifacts unless explicitly required.

## 18. Documentation

Maintain:

- `README.md`
- `docs/SPEC.md`
- `docs/ARCHITECTURE.md`
- `docs/DEPENDENCIES.md`
- `docs/WINDOWS-INTEGRATION.md`
- `docs/BUILD.md`
- `docs/TESTING.md`
- `docs/FEATURES.md`

`CLAUDE.md` contains development rules.

`docs/SPEC.md` contains product requirements.

Keep the feature status in `docs/FEATURES.md` synchronized with actual implementation.

## 19. Feature Tracking

Use explicit status values such as:

- Planned
- Investigating
- In Progress
- Implemented
- Tested
- Blocked

Do not mark a feature as Implemented merely because a UI button exists.

A feature should be considered implemented only when its core behavior works.

## 20. Engineering Autonomy

Claude Code may:

- inspect the repository
- research technical options
- create files
- refactor code
- add tests
- improve architecture
- fix bugs
- improve performance

However, Claude Code must not independently change the product scope.

Technical implementation decisions can be made autonomously when they remain consistent with this specification.

Product-level scope changes require the user's approval.

## 21. Recommended Initial Order

Unless a technical dependency requires a different order:

1. Inspect environment
2. Inspect repository
3. Confirm SDK/toolchain
4. Research architecture
5. Research dependencies and licenses
6. Design Quick View architecture
7. Design Windows Shell integration
8. Create minimal project
9. Build and test minimal project
10. Prototype Quick View
11. Implement Explorer integration
12. Implement core file/image/PDF infrastructure
13. Expand editing and annotation features
14. Add OCR/scanning/printing
15. Expand conversion and metadata support
16. Performance optimization
17. Full integration testing
18. Packaging and release preparation

Do not skip architecture research simply because implementation appears straightforward.

## 22. Definition of Quality

Mavue should feel like a native Windows application rather than a web application wrapped in a desktop shell.

Prioritize:

- fast startup
- responsive interaction
- reliable file handling
- accurate rendering
- strong Explorer integration
- predictable editing
- safe saving
- good keyboard support
- high-DPI correctness
- accessibility
- clear error handling

## 23. Final Product Goal

The final product should be a comprehensive Windows equivalent of:

- macOS Preview
- Finder Quick Look

while taking advantage of native Windows capabilities.

The goal is not merely to imitate macOS visually. It is to provide the same class of fast, integrated workflow on Windows, with native Windows Explorer and system integration wherever appropriate.
