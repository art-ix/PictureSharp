# PictureSharp architecture

PictureSharp ships one assembly and one dependency-free NuGet package. Folders express layers; public namespaces express the operations a caller wants to use. Internal namespaces follow the layer and module.

Callers start from `Image` with one `using PictureSharp;`: `Image.Load`, `Image.Identify`, `image.Save`, `image.Mutate` and `ImageFormat.FromExtension` are C# 14 extension members declared in `PublicApi`, so the model type itself has no I/O or processing members. `PictureSharp.Processing` holds processing modes and options, `PictureSharp.Formats` the plugin interfaces and `PictureSharp.Formats.<Format>` format options and shortcuts.

## Model and boundaries

`Image` owns its frames and pixel buffers. `ImageFrameCollection` enforces frame dimensions and ownership; metadata and pixel layouts are model concepts. Animations contain full-canvas frames of a common size. ICO variants are independent images held by an owning `IcoImageCollection`, not animation frames.

The model has no codec registry, filesystem access or processing orchestration. `Clone()` makes a deep copy; fluent `Clone(operation)` belongs to the processing API and preserves the existing copy-on-write optimization. Pure pixel conversion and the typed EXIF reader/editor belong to the model because pixel layout and metadata orientation are model behavior. The EXIF parser and editor perform no I/O. Codec metadata selection belongs to infrastructure.

| Folder | Responsibility | Allowed dependencies |
| --- | --- | --- |
| `Primitives` | Buffer streams, scalar/SIMD helpers, argument checks, parallel execution | BCL |
| `Domain` | Images, frames, pixels, metadata, format descriptors | Primitives |
| `Algorithms` | Resampling, filters, quantization, compression, color conversion | Domain, Primitives |
| `Application` | Codec ports, read/write/identify workflows, processing contexts, options, ICO orchestration | Algorithms, Domain, Primitives |
| `Infrastructure` | Codec implementations, input/output adapters, metadata selection | Application, Algorithms, Domain, Primitives |
| `Composition` | Codec registration and construction of default services | Infrastructure, Application, Algorithms, Domain, Primitives |
| `PublicApi` | Extension members on `Image`, `ImageFormat` and `ImageConfiguration`, format shortcuts | Composition, Application, Algorithms, Domain, Primitives |

Dependencies inside a layer are allowed. Higher layers may not be referenced from lower ones; the layer graph is acyclic. Public API wrappers do not reference concrete infrastructure. BCL `Stream` is allowed in codec contracts; filesystem operations are confined to infrastructure. `Path` operations used for format lookup do not open files.

## Data flow

The `Image` load and identify members select the configuration (`DecoderOptions.Configuration`, otherwise `ImageConfiguration.Default`) and call its read service. Input adapters enforce byte limits while reading; application services select codecs through `ICodecResolver`, normalize corrupt-input exceptions and apply thumbnail/orientation policies. A decoder returns an internal `DecodedImage` value carrying the stored size and JPEG scale; these transient details are absent from `Image`.

`image.Save` and `image.Encode` call the write service of `ImageConfiguration.Default`. File output is buffered before writing, and direct stream output retains its existing partial-write behavior on cancellation. The input and output ports have BCL-only implementations.

`ImageProcessingExtensions` delegates to the processing service and context; contexts invoke numeric algorithms. Scalar and SIMD files remain together by responsibility, with architecture dispatch outside hot loops where previously implemented.

`IcoFile` delegates to an icon service through an ICO-specific codec port. Infrastructure validates directory entries and sums variant pixel budgets; application orchestration handles orientation and cleanup. PNG embedded in ICO and PNG/JPEG embedded in BMP reuse injected codecs without referencing composition.

Only `Composition/CodecRegistry.cs` registers the built-in formats, as `FormatRegistration` entries with codec factories. `ImageConfiguration` (Application) is immutable: it builds a `CodecResolver` from its registrations, so container codecs receive the resolver of their own configuration, and creates its read and write services from the input/output ports supplied by composition. `LibraryServices` composes the initial default; `ImageConfiguration.Default` is a static extension property in `PublicApi`, so Application never references Composition. Replacing the default publishes a complete snapshot with a volatile write. No external DI container is required.

## Extension points

Format plugins implement the public `IImageFormatModule`, `IImageFormatDecoder` and `IImageFormatEncoder` interfaces and are registered with `ImageConfigurationBuilder.AddFormat`/`ReplaceFormat`. `Application/Extensibility` adapts them to the internal codec contracts: detection failures on malformed headers count as no match, plugin results are checked against `MaxPixels` and `MaxFrames` and receive their `SourceFormat`, and `ImageDecodeContext` exposes the shared decode budget and nested decoding without exposing `DecodeContext`. Built-in codecs keep their internal contracts and are not wrapped.

`IFrameProcessor` operations run through `ProcessingState.ReplaceFrames`, like built-in geometry: every frame is prepared, then the aggregate commits the batch. Destination buffers are zeroed because third-party code writes them.

## Enforcement and development

Architecture tests map compiled types to source-layer folders, then inspect type signatures, generic arguments, fields, interfaces, method IL, local variables and catch clauses. They reject forbidden directions, layer cycles, filesystem access outside infrastructure and references to codec registration outside composition. An inspector fixture verifies that method calls, fields, generic arguments and exception handlers are observed.

Public types remain documented and implementation types remain internal. Run `dotnet test -c Release`; architecture checks are included. The library remains BCL-only, managed, compatible with trimming and Native AOT, with existing limits, cancellation and buffer ownership semantics.

## Aggregate invariants and lifetime

`Image` is the aggregate boundary for the frame canvas. Application prepares replacement buffers and checks cancellation; `Image.CommitFrames` validates the complete count, dimensions and buffer lengths before publishing any frame. Publication has no callbacks or cancellation points. `ImageDimensions` is an internal validated value; its default value is invalid. Direct frame publication is reserved for the aggregate and enforced by compiled IL tests. Copy-on-write uses a separate operation that cannot change dimensions.

Frame ownership uses a value token identifying an image or read session, without an extra token allocation. The collection alone attaches/transfers image frames; the session attaches its borrowed current frame. `Add` takes an unattached frame, `AddClone` copies it, and `ExportFrame` prepares metadata and the result before detaching the source frame. A frame duration is non-negative; codec-specific timing limits and rounding remain in codecs.

After disposing an image, its state accessors and collection operations throw `ObjectDisposedException`. `Dispose` is idempotent and `ToString` remains diagnostic. Existing pixel views, previously obtained frame references and metadata references are not revocable. Buffer replacement detaches old views. Concurrent mutation is unsupported. `HasAlpha` is a hint; use `IsOpaque()` to inspect actual pixels.

Options are immutable records; each decode creates a separate `DecodeContext` and monotonic `DecodeBudget`. Embedded codecs share the operation budget through child contexts. `DecodedImage` carries stored dimensions and reduction scale through nested containers, including JPEG embedded in BMP.

`Image.OpenFrameReaderAsync` and `Image.CreateFrameWriter` delegate to the configuration's `ImageSequenceService`.
Internal `ISequentialImageDecoder`/`ISequentialImageEncoder` ports create sessions for GIF, PNG/APNG and WebP.
The reader owns its current borrowed frame through a session owner token; cloning transfers no ownership from it.
Full and sequential decoders share GIF/APNG/WebP compositing implementations. Limits are checked before canvas
allocation and before decoding each subsequent frame; input bytes and metadata have sequence-wide budgets.

Writers borrow input frames for one call, stage one encoded fragment for async I/O and require explicit completion.
GIF uses per-frame palettes and background disposal; APNG shares PNG serialization and chunk writing; WebP shares
the incremental keyframe-window encoder and muxer. Declaring one frame selects the normal still encoder.
Header patches use caller-relative offsets. Non-seekable output that requires patching is rejected unless the caller
enables a temporary file through the output port. Application owns the temporary-file copy and cleanup; filesystem
operations remain in infrastructure. Aborting a path writer removes its partial file. Caller streams remain open.

Use C# 14 `field` for local property validation and `extension` blocks for facade operations. Keep required invariants as model methods, mutable models as classes, immutable options as records and small internal values as readonly structs. C# 14 span overload resolution is verified by compiling a consumer with the actual SDK compiler. See [extension guide](extending-picturesharp.md).

## Metadata-only operations

`Image.ReadMetadata` and `Image.EditMetadata` use an application metadata service and the internal
`IImageMetadataCodec` port registered per built-in format. JPEG, PNG/APNG and WebP implementations rewrite
containers while copying compressed image/animation data unchanged. No pixel decoder or image encoder is invoked;
an architecture test guards this boundary. Compression is used only for PNG metadata. Existing Identify behavior
is unchanged. The output adapter owns temporary-file creation and atomic replacement in the destination directory.

`ImageMetadata.ExifProfile` owns no independent copy of the EXIF state. The domain TIFF parser validates directory
graphs and values lazily; an edit prepares a complete replacement block before publication. Unknown retained data
keeps its original absolute offsets. Explicit allowlisting rebuilds only selected known fields, and GPS removal
scrubs the known directory and values rather than just removing a pointer.
