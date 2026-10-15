# PictureSharp architecture

PictureSharp ships one assembly and one dependency-free NuGet package. Folders express layers; public namespaces express the operations a caller wants to use. Internal namespaces follow the layer and module.

## Model and boundaries

`Image` owns its frames and pixel buffers. `ImageFrameCollection` enforces frame dimensions and ownership; metadata and pixel layouts are model concepts. Animations contain full-canvas frames of a common size. ICO variants are independent images held by an owning `IcoImageCollection`, not animation frames.

The model has no codec registry, filesystem access or processing orchestration. `Clone()` makes a deep copy; fluent `Clone(operation)` belongs to the processing API and preserves the existing copy-on-write optimization. Pure pixel conversion and the minimal EXIF value reader/patcher belong to the model because pixel layout and metadata orientation are model behavior. This EXIF helper performs no I/O. Codec metadata selection belongs to infrastructure.

| Folder | Responsibility | Allowed dependencies |
| --- | --- | --- |
| `Primitives` | Buffer streams, scalar/SIMD helpers, argument checks, parallel execution | BCL |
| `Domain` | Images, frames, pixels, metadata, format descriptors | Primitives |
| `Algorithms` | Resampling, filters, quantization, compression, color conversion | Domain, Primitives |
| `Application` | Codec ports, read/write/identify workflows, processing contexts, options, ICO orchestration | Algorithms, Domain, Primitives |
| `Infrastructure` | Codec implementations, input/output adapters, metadata selection | Application, Algorithms, Domain, Primitives |
| `Composition` | Codec registration and construction of default services | Infrastructure, Application, Algorithms, Domain, Primitives |
| `PublicApi` | Static entrypoints and fluent extensions | Composition, Application, Algorithms, Domain, Primitives |

Dependencies inside a layer are allowed. Higher layers may not be referenced from lower ones; the layer graph is acyclic. Public API wrappers do not reference concrete infrastructure. BCL `Stream` is allowed in codec contracts; filesystem operations are confined to infrastructure. `Path` operations used for format lookup do not open files.

## Data flow

`ImageReader` delegates to the composed read service. Input adapters enforce byte limits while reading; application services select codecs through `ICodecResolver`, normalize corrupt-input exceptions and apply thumbnail/orientation policies. A decoder returns an internal `DecodedImage` value carrying the stored size and JPEG scale; these transient details are absent from `Image`.

`ImageWriter` accepts the image explicitly and delegates to the write service. File output is buffered before writing, and direct stream output retains its existing partial-write behavior on cancellation. The input and output ports have BCL-only implementations.

`ImageProcessingExtensions` delegates to the processing service and context; contexts invoke numeric algorithms. Scalar and SIMD files remain together by responsibility, with architecture dispatch outside hot loops where previously implemented.

`IcoFile` delegates to an icon service through an ICO-specific codec port. Infrastructure validates directory entries and sums variant pixel budgets; application orchestration handles orientation and cleanup. PNG embedded in ICO and PNG/JPEG embedded in BMP reuse injected codecs without referencing composition.

Only `Composition/CodecRegistry.cs` registers formats. `LibraryServices` constructs the default resolver and services. These are internal implementation details; no external DI container is required.

## Enforcement and development

Architecture tests map compiled types to source-layer folders, then inspect type signatures, generic arguments, fields, interfaces, method IL, local variables and catch clauses. They reject forbidden directions, layer cycles, filesystem access outside infrastructure and references to codec registration outside composition. An inspector fixture verifies that method calls, fields, generic arguments and exception handlers are observed.

Public types remain documented and implementation types remain internal. See [API migration](migration-layered-api.md). Run `dotnet test -c Release`; architecture checks are included. The library remains BCL-only, managed, compatible with trimming and Native AOT, with existing limits, cancellation and buffer ownership semantics.

## Aggregate invariants and lifetime

`Image` is the aggregate boundary for the frame canvas. Application prepares replacement buffers and checks cancellation; `Image.CommitFrames` validates the complete count, dimensions and buffer lengths before publishing any frame. Publication has no callbacks or cancellation points. `ImageDimensions` is an internal validated value; its default value is invalid. Direct frame publication is reserved for the aggregate and enforced by compiled IL tests. Copy-on-write uses a separate operation that cannot change dimensions.

Frame ownership uses a value token identifying an image or read session, without an extra token allocation. The collection alone attaches/transfers image frames; the session attaches its borrowed current frame. `Add` takes an unattached frame, `AddClone` copies it, and `ExportFrame` prepares metadata and the result before detaching the source frame. A frame duration is non-negative; codec-specific timing limits and rounding remain in codecs.

After disposing an image, its state accessors and collection operations throw `ObjectDisposedException`. `Dispose` is idempotent and `ToString` remains diagnostic. Existing pixel views, previously obtained frame references and metadata references are not revocable. Buffer replacement detaches old views. Concurrent mutation is unsupported. `HasAlpha` is a hint; use `IsOpaque()` to inspect actual pixels.

Options are immutable records; each decode creates a separate `DecodeContext` and monotonic `DecodeBudget`. Embedded codecs share the operation budget through child contexts. `DecodedImage` carries stored dimensions and reduction scale through nested containers, including JPEG embedded in BMP.

Sequential reader/writer contracts and lifecycle are internal foundations. They define borrowed frames, cancellation, declared counts, capabilities and stream ownership, with contract tests using controlled readers/writers. Production sequential codecs and public streaming entrypoints remain planned for 0.6.0. No full-buffer implementation is advertised as streaming.

Use C# 14 `field` for local property validation and `extension` blocks for facade operations. Keep required invariants as model methods, mutable models as classes, immutable options as records and small internal values as readonly structs. C# 14 span overload resolution is verified by compiling a consumer with the actual SDK compiler. See [extension guide](extending-picturesharp.md).
