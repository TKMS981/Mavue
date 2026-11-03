# Mavue Product Specification

## 1. Product Goal

Mavue is a Windows 11 desktop application intended to provide the functionality and comfort of macOS Preview and Finder Quick Look on Windows.

The application should combine:

- instant file preview
- image viewing and editing
- PDF viewing and editing
- PDF annotation
- PDF page management
- OCR
- signatures
- forms
- scanning
- printing
- file conversion
- metadata management
- Windows Explorer integration

The product should feel native to Windows rather than like a web application.

## 2. Core Principle

Anything not explicitly listed in the exclusion section is an implementation target.

A feature may be technically difficult, but difficulty alone is not a reason to remove or permanently defer it.

If implementation requires investigation, mark it as Investigating rather than deleting it from the specification.

---

# 3. Quick View / Quick Look

Implement:

- Explorer file selected + Space → instant preview
- Multiple selected files
- Previous/next file navigation
- Escape to close
- Fullscreen
- Zoom
- Rotate
- Thumbnail/index sheet
- File name
- File information
- Open with another application
- Copy
- Share
- Print
- Markup from Quick View
- GIF preview
- Audio preview
- Video preview
- HDR support where practical
- Explorer Preview Pane integration
- File association integration
- Context-menu integration
- Drag & drop
- Clipboard integration
- Windows Search integration where practical
- Thumbnail Provider
- Preview Handler

Quick View startup speed is a major product requirement.

## 3.1 Quick Preview extensions

Quick View should become a universal preview layer: select any file in Explorer, press Space, and see its content
(or at least its information) almost at once. The following are implementation targets in addition to the list
above (approved 2026-10-06; plan and phases in `docs/ARCHITECTURE.md` §4.7):

- Folder filmstrip
- Pixel inspector in Quick View
- Similar image search
- Wheel zoom without Ctrl as a user setting
- Video seek with the mouse wheel
- Audio waveform
- ZIP archive preview without extraction
- Preview of images inside ZIP archives
- Text preview
- Code preview with syntax highlighting
- Markdown preview
- Safe HTML preview
- Universal file information preview
- EXIF inspector
- Privacy check before sharing
- Copy without private metadata
- Drag out of Quick View
- Preview pin
- Quick View window design (light, dark, glass)
- Adaptive performance tiers

Requirements for all Quick Preview features ("Always Ready, Never Always Working"):

1. Space → preview latency and the resident process's idle CPU and memory must not get worse. New work happens only
   while Quick View is shown and only for the shown item (and, within limits, its neighbors).
2. Every file type is shown by its own replaceable component; the Quick View controller does not branch on file types.
   Heavy decoding or analysis can move to a separate, resource-limited process without changing the user interface.
3. Every cache has an upper bound; every expensive operation can be cancelled; resources are released when Quick View
   closes. Limits adapt automatically to the machine (no settings required from the user).
4. Quick View only reads: it never modifies, moves or executes the previewed file, never accesses the network by
   itself, never runs scripts from previewed content, never launches other programs without an explicit user action,
   and never removes Mark-of-the-Web. Files it creates (for example a copy without private metadata) are new files
   that keep the original's Mark-of-the-Web.
5. Archives are previewed from their central directory without extraction, entries are read on demand, and archive
   names and sizes are distrusted (path traversal, decompression bombs, abnormal compression ratios, resource
   exhaustion). A file that cannot be previewed safely shows a clear message; Quick View itself keeps running.
6. The mouse wheel keeps its meaning per content (zoom for images, scrolling for documents, seeking for media);
   Ctrl+wheel zooms around the pointer everywhere it applies.
7. Dragging out of Quick View copies by default; pinning keeps the current preview from following Explorer's selection.

Quick View window design: the window opens at a moderate size for the monitor it appears on (large enough to see
the image well, without covering most of the screen), keeping DPI and multi-monitor behavior. Its background can be
made lightly see-through in settings, from fully opaque to a light, blurred translucency; the default is a light
translucency chosen so the window looks good without any setting. Transparency applies to the background only: images,
PDF pages and text keep their own colors and quality, and text, icons and controls stay readable. High contrast,
Windows' transparency effects being off and low-performance machines show an opaque window. The setting must not slow
down Space → preview.

---

# 4. Image Formats

Implement support for:

- JPG
- JPEG
- PNG
- WebP
- GIF
- BMP
- TIFF
- HEIC
- HEIF
- ICO
- SVG
- AVIF
- RAW formats
- JPEG 2000
- other practical image formats where appropriate

Explicitly excluded:

- EPS
- OpenEXR

---

# 5. Image Viewing

Implement:

- Zoom
- Pan
- Fit to Window
- Actual Size
- Rotate
- Flip horizontal
- Flip vertical
- Fullscreen
- Slideshow
- Image information
- Pixel-level inspection where useful
- Transparency checkerboard
- Drag & drop
- Copy/paste

---

# 6. Image Editing

Implement:

- Crop
- Resize
- Rotate
- Flip
- Brightness
- Contrast
- Saturation
- Tint
- Exposure
- Gamma
- Sharpness
- Temperature
- Auto Color
- Background removal
- Other practical non-lasso selection tools
- Copy
- Paste
- Markup
- Text
- Shapes
- Freehand drawing
- Signature
- Undo
- Redo

Explicitly excluded:

- Lasso
- Smart Lasso

---

# 7. Image Conversion

Implement:

- JPG → PNG
- PNG → JPG
- WebP
- HEIC → JPG
- HEIC → PNG
- TIFF
- BMP
- GIF
- AVIF
- JPEG 2000
- other practical formats

Provide:

- JPEG quality
- PNG compression
- metadata preservation
- metadata removal
- batch conversion where practical

---

# 8. Metadata

Implement:

- File size
- Resolution
- DPI
- Creation time
- Modification time
- Author
- Keywords
- GPS
- EXIF
- EXIF removal
- ICC profile metadata handling where practical

ColorSync and Soft Proof are excluded.

GPS/map visualization is not excluded and should be treated as an implementation target.

Metadata removal follows the same rules in Quick View ("Copy without private metadata", §3.1) and in the main
application ("Export without metadata…", §7 "metadata removal", §8 "EXIF removal"):

1. The original file is only read. It is never the destination of a copy and is never modified.
2. A copy is written to a temporary file, verified (none of the removed kinds left, decodable with the expected size)
   and only then given its name. A failed or cancelled copy leaves nothing behind.
3. Types that can carry the removal without re-encoding (JPEG, PNG, WebP, GIF) are copied without re-encoding. Other
   types (HEIF/AVIF, TIFF/RAW and others Windows can decode) are converted to JPEG or PNG in a separate,
   memory- and CPU-limited process, and the user is told that the copy is converted.
4. Both places show the same findings, the same choices of what to remove, the same words and the same destinations
   (next to the original, the clipboard). The main application also offers a place chosen in the save dialog, the
   file type of the copy and the JPEG quality.
5. When no copy can be made (SVG, a missing codec, a cloud-only file, a destination that cannot be written), the
   reason is shown; the application keeps working.
6. The copy keeps the original's Mark-of-the-Web. Nothing is uploaded; the values found are shown but never logged.

---

# 9. PDF Viewer

Implement:

- Fast PDF rendering
- Page navigation
- Thumbnail sidebar
- Table of contents
- Page numbers
- Jump to page
- Continuous scroll
- Single-page view
- Two-page spread
- Zoom
- Fit Width
- Fit Page
- Actual Size
- Ctrl+F search
- Search result list
- Text selection
- Copy
- Internal PDF links
- External URLs
- Bookmarks
- Print
- Presentation/slideshow where practical
- PDF-as-image rendering where useful

---

# 10. PDF Page Management

Implement:

- Add page
- Blank page
- Add pages from another PDF/file
- Delete page
- Move page
- Reorder pages
- Drag-and-drop page reorder
- Duplicate page
- Extract pages
- Split PDF
- Merge PDFs
- Move pages between PDFs
- Copy pages between PDFs
- Rotate one page
- Rotate multiple pages
- Crop pages
- Change page size

---

# 11. PDF Markup and Annotations

Implement:

- Highlight
- Underline
- Strikethrough
- Text box
- Note/comment
- Speech bubble
- Rectangle
- Ellipse/circle
- Line
- Arrow
- Freehand
- Pen
- Marker
- Image insertion
- Stamp
- Color
- Line width
- Fill
- Opacity
- Move annotations
- Resize annotations
- Delete annotations
- Annotation list/sidebar
- Zoom lens where practical
- Other Preview-like annotation features

Explicitly excluded:

- Lasso
- Smart Lasso

---

# 12. PDF Signing

Implement:

- Signature creation
- Mouse signature
- Windows Ink signature
- Signature image import
- Save signatures
- Place signature
- Move signature
- Resize signature
- Delete signature
- Multiple saved signatures
- Webcam signature where practical

Cryptographic/digital signatures may be implemented as a later stage, but are not excluded from the product scope.

---

# 13. PDF Forms

Implement:

- Text fields
- Checkboxes
- Radio buttons
- Dropdowns
- Form saving
- Printing
- AutoFill
- Reset where available

---

# 14. PDF OCR

Implement:

- OCR
- Scanned PDF OCR
- Select OCR text
- Copy OCR text
- Japanese OCR
- English OCR
- Multilingual OCR
- Search OCR text
- Embed OCR text
- OCR settings

Japanese OCR is an important requirement.

---

# 15. PDF Security

Implement:

- Password protection
- Encryption
- Print restrictions
- Copy restrictions
- Edit restrictions
- Annotation restrictions
- True redaction
- Flatten annotations

Redaction must remove the underlying content and must not merely place a black rectangle over it.

---

# 16. PDF Compression and Conversion

Implement:

- PDF compression
- Quality controls
- Image compression
- Web optimization / linearization
- PDF → JPG
- PDF → PNG
- PDF → TIFF
- JPG → PDF
- PNG → PDF
- Multiple images → PDF
- Multiple PDFs → PDF

PDF → Office formats:

- PDF → Word
- PDF → Excel
- PDF → PowerPoint

These are future/backlog features and are not part of the initial implementation.

---

# 17. Scanning

Implement:

- WIA scanner support
- TWAIN scanner support
- Scanner → PDF
- Scanner → JPG
- ADF
- Duplex scanning
- Color
- Grayscale
- Black & white
- DPI selection
- Auto deskew
- OCR
- Multi-page PDF

---

# 18. Printing

Implement:

- Print
- Page selection
- Copies
- Paper size
- Orientation
- Scaling
- Pages per sheet
- Fit to paper
- Color
- Black & white
- Print preview

---

# 19. Audio and Video Preview

Implement:

- Audio preview
- Video preview
- Playback controls
- Seek
- Volume
- Fullscreen
- Supported Windows/media formats where practical

Explicitly excluded:

- Video trimming

---

# 20. GIF

Implement:

- Animated GIF preview
- Playback
- Pause
- Frame navigation where practical
- Fullscreen
- Zoom

---

# 21. File Operations

Implement:

- Save
- Save As
- Export
- Auto Save
- Undo
- Redo
- Duplicate
- Rename
- Lock
- File properties/info
- Recent files
- Drag & drop
- Clipboard
- Version/history/backup where practical
- Tabs
- Multiple windows

---

# 22. Windows Explorer Integration

Implement:

- Space-key Quick View
- Right-click “Open in Mavue”
- Right-click “Mavue Quick View”
- File associations
- PDF association
- Image associations
- Thumbnail Provider
- Preview Pane
- Preview Handler
- Shell extension
- Windows Search integration
- Drag & drop
- Clipboard
- Context actions

Context actions should eventually include practical operations such as:

- Merge PDFs
- Split PDFs
- Compress PDFs
- Convert images
- Create PDF from images
- Other relevant file operations

---

# 23. System and UI

Implement:

- Dark mode
- Light mode
- Settings
- Keyboard shortcuts
- High-DPI support
- Multi-monitor support
- Accessibility
- Touch support
- Windows Ink support
- Japanese localization
- English localization

---

# 24. Performance

Requirements:

- Fast startup
- Fast Quick View
- Responsive UI
- Efficient large-image rendering
- Efficient large-PDF rendering
- Efficient multi-page PDF navigation
- RAW image handling
- Background decoding
- Incremental rendering where appropriate
- GPU acceleration where appropriate
- CPU/software fallback
- Thumbnail caching
- Preview caching
- Low unnecessary memory consumption

Avoid blocking the UI thread for expensive operations.

---

# 25. Large Files

Support large:

- images
- PDFs
- multi-page documents
- RAW files

Use streaming, asynchronous operations, cancellation, and incremental processing where appropriate.

---

# 26. Unicode

Correctly support:

- Japanese filenames
- Unicode filenames
- Japanese OCR
- multilingual PDF text
- multilingual metadata

---

# 27. Privacy

Mavue should operate locally by default.

Do not upload user files to external services unless explicitly required and disclosed.

Avoid unnecessary telemetry.

Treat OCR text, signatures, scanned documents, images, PDFs, EXIF, and GPS metadata as potentially sensitive.

---

# 28. Architecture Requirements

The application should remain modular and extensible.

Suggested modules:

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

Windows-specific code should be isolated where practical.

---

# 29. Explicit Exclusions

The following are explicitly excluded from the initial implementation:

## Image / media

- Video trimming
- Copy Subject
- Live Photo
- EPS
- OpenEXR
- Lasso
- Smart Lasso

## 3D

All 3D functionality is excluded from the initial implementation:

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
- 3D viewing
- 3D editing
- 3D animation
- 3D annotations
- 3D export

## Apple-specific

- Soft Proof
- ColorSync
- Vision Pro integration
- Apple-specific 3D integration

## PDF → Office

- PDF → Word
- PDF → Excel
- PDF → PowerPoint

These PDF → Office functions remain future/backlog features rather than permanent exclusions.

---

# 30. Feature Status

Maintain feature status separately in `docs/FEATURES.md`.

Suggested statuses:

- Planned
- Investigating
- In Progress
- Implemented
- Tested
- Blocked

A feature must not be marked Implemented merely because a UI control exists.

---

# 31. Definition of Done

A feature is considered complete only when:

1. Core functionality works.
2. Relevant error cases are handled.
3. The UI does not unnecessarily block.
4. Files are saved safely.
5. Undo/redo works where applicable.
6. Relevant tests exist.
7. Real Windows behavior has been tested when Windows integration is involved.
8. Documentation/status has been updated.
9. Licensing implications are understood for third-party components.
10. The feature does not silently break other implemented functionality.

---

# 32. Product Philosophy

Mavue should provide the Windows equivalent of Preview + Quick Look.

The target is not a superficial clone.

The product should provide:

- speed
- native Windows integration
- reliable document handling
- comprehensive image/PDF functionality
- strong Explorer integration
- safe editing
- high-DPI correctness
- accessibility
- a polished desktop workflow
