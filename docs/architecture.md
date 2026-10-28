# PictureSharp architecture

PictureSharp ships one dependency-free assembly and NuGet package. Folders express layers, public namespaces express
functionality, internal namespaces follow the layer and module.

Callers start from `Image` with `using PictureSharp;`. `Image.Load`, `Image.Identify`, `image.Save`, `image.Mutate` and
`ImageFormat.FromExtension` are C# 14 extension members declared in `PublicApi`, so the model type has no I/O or
processing members. `PictureSharp.Processing` holds processing modes and options, `PictureSharp.Formats` the plugin
interfaces and `PictureSharp.Formats.<Format>` format options and shortcuts.

## Model and boundaries

`Image` owns its frames and pixel buffers; `ImageFrameCollection` enforces frame dimensions and ownership. Animations
contain full-canvas frames of a common size. ICO variants are independent images in an owning `IcoImageCollection`. The
model has no codec registry, filesystem access or orchestration; pixel conversion and the I/O-free typed EXIF
reader/editor belong to it, codec metadata selection to infrastructure.

| Folder | Responsibility | Allowed dependencies |
| --- | --- | --- |
| `Primitives` | Buffer streams, scalar/SIMD helpers, argument checks, parallel execution | BCL |
| `Domain` | Images, frames, pixels, metadata, format descriptors | Primitives |
| `Algorithms` | Resampling, filters, quantization, compression, color conversion | Domain, Primitives |
| `Application` | Codec ports, read/write/identify workflows, processing contexts, options, ICO orchestration | Algorithms, Domain, Primitives |
| `Infrastructure` | Codec implementations, input/output adapters, metadata selection | Application, Algorithms, Domain, Primitives |
| `Composition` | Codec registration and construction of default services | Infrastructure, Application, Algorithms, Domain, Primitives |
| `PublicApi` | Extension members on `Image`, `ImageFormat` and `ImageConfiguration`, format shortcuts | Composition, Application, Algorithms, Domain, Primitives |

The layer graph is acyclic and lower layers never reference higher ones. Public API wrappers do not reference concrete
infrastructure. Codec contracts may use `Stream`; filesystem access is confined to infrastructure.

## Data flow

Load and identify members take `DecoderOptions.Configuration` (or `ImageConfiguration.Default`) and call its read
service. Input adapters enforce byte limits; application services select codecs through `ICodecResolver`, normalize
corrupt-input exceptions and apply thumbnail and orientation policies. Decoders return an internal `DecodedImage` with
the stored size and JPEG scale, which `Image` does not carry.

`image.Save` and `image.Encode` call the write service of the configuration. Files are written after encoding into
memory; streams are written as the encoder goes. Processing extensions delegate to the processing service and contexts,
which invoke algorithms; scalar and SIMD code of one responsibility share a type across partial files.

Color management follows the same split: `Algorithms/ColorManagement` parses ICC matrix/TRC profiles and converts
pixels, `Application/ColorManagement` chooses the description (ICC, then PNG `sRGB`, then `gAMA`/`cHRM`), and codecs
only read and write it. `IcoFile` uses an ICO-specific codec port; PNG in ICO and PNG/JPEG in BMP reuse injected codecs.

Only `Composition/CodecRegistry.cs` registers built-in formats, as `FormatRegistration` entries. An immutable
`ImageConfiguration` builds its `CodecResolver` (so container codecs use their own configuration) and its services from
ports supplied by composition. `LibraryServices` composes the default; `ImageConfiguration.Default` is a static
extension property in `PublicApi`, so Application never references Composition. Replacing it publishes a complete
snapshot with a volatile write. No DI container is needed.

## Extension points

Plugins implement `IImageFormatModule`, `IImageFormatDecoder` and `IImageFormatEncoder` and are registered with
`AddFormat`/`ReplaceFormat`. `Application/Extensibility` adapts them to the internal contracts: malformed-header
failures count as no match, results are checked against `MaxPixels` and `MaxFrames`, and `ImageDecodeContext` exposes
the shared budget and nested decoding. A plugin gain map becomes the same internal attachment as a JPEG one, and
`IImageFormatMetadataCodec` is adapted to the internal metadata port. Built-in codecs are not wrapped.
`IFrameProcessor<TPixel>` operations run through `ProcessingState.ReplaceFrames` with zeroed destination buffers.

## Adapter packages

`PictureSharp.Drawing`, `PictureSharp.Skia` and `PictureSharp.AspNetCore` are separate packages outside the layer graph.
They use only public API, each depends on PictureSharp and its target API, and their types live in the namespace named
after the package. Tests check the compiled references in both directions. See [Adapters](adapters.md).

## Enforcement

Architecture tests map compiled types to layer folders and inspect signatures, generic arguments, fields, method IL,
locals and catch clauses. They reject forbidden directions, cycles, filesystem access outside infrastructure and codec
registration outside composition; a fixture proves the inspector sees calls, fields, generics and handlers. They run
with `dotnet test -c Release`.

## Aggregate invariants and lifetime

`Image` is the aggregate boundary for the frame canvas. Application prepares replacement buffers and checks
cancellation; `Image.CommitFrames` validates count, dimensions and buffer lengths before publishing any frame, with no
callbacks or cancellation points. Direct frame publication is reserved to the aggregate (checked in IL); copy-on-write
uses a separate operation that cannot change dimensions.

Frame ownership uses a value token for an image or read session. Only the collection attaches or transfers frames, and
the session attaches its borrowed current frame. Durations are non-negative; format timing limits stay in codecs.

After `Dispose` (idempotent) state accessors throw `ObjectDisposedException`; existing pixel views are not revocable and
concurrent mutation is unsupported. `HasAlpha` is a hint; `IsOpaque()` inspects pixels. Options are immutable records;
each decode gets its own `DecodeContext` and monotonic `DecodeBudget`, shared by embedded codecs through child contexts.
A part of an already reserved image (`ImageDecodeContext.DecodeEmbeddedPart`) gets a detached context with its own
budget of the part size, one frame and the remaining metadata; `EmbeddedPartOptions` may replace the part's JPEG
settings and degree of parallelism.

Frame readers and writers use the configuration's `ImageSequenceService` and internal sequential codec ports, sharing
compositing and encoding with the batch codecs; Application owns the optional temporary file of a writer.

Use `field` for local validation and extension blocks for facade operations; keep invariants as model methods, mutable
models as classes, options as records and small internal values as readonly structs. See
[Extending PictureSharp](extending-picturesharp.md).

## Metadata-only operations

`Image.ReadMetadata` and `Image.EditMetadata` use an application metadata service and the internal
`IImageMetadataCodec` port per format. JPEG, PNG/APNG and WebP rewrite containers while copying compressed image data
unchanged; an architecture test guards that no pixel codec is invoked. The output adapter owns temporary files and
atomic replacement. `ExifProfile` shares the EXIF state with `ImageMetadata`; the domain TIFF parser validates lazily,
edits prepare a complete replacement block, unknown data keeps its offsets, and GPS removal scrubs the directory and its
values.
