using PictureSharp.Composition;

namespace PictureSharp;

public static partial class ImageIOExtensions
{
    extension(Image)
    {
        /// <summary>
        /// Opens a WebP, PNG/APNG or GIF file for reading its frames one at a time, without decoding the whole animation
        /// into memory. The header is read before this returns; dispose the reader to close the file.
        /// </summary>
        /// <exception cref="ImageFormatException">The file is not a recognized image, or its header is corrupt.</exception>
        /// <exception cref="NotSupportedException">The format cannot be read frame by frame.</exception>
        public static Task<ImageFrameReader> OpenFrameReaderAsync(string path, CancellationToken cancellationToken = default) =>
            LibraryServices.Sequences.OpenReaderAsync(path, null, cancellationToken);

        /// <summary>
        /// Opens a WebP, PNG/APNG or GIF file for reading its frames one at a time with the given limits and formats.
        /// <see cref="DecoderOptions.TargetSize"/> and <see cref="DecoderOptions.ApplyExifOrientation"/> are not supported.
        /// </summary>
        public static Task<ImageFrameReader> OpenFrameReaderAsync(string path, DecoderOptions? options, CancellationToken cancellationToken = default) =>
            LibraryServices.SequencesFor(options).OpenReaderAsync(path, options, cancellationToken);

        /// <summary>
        /// Reads the frames of a WebP, PNG/APNG or GIF stream one at a time. The stream need not be seekable, is read
        /// forward only (possibly past the end of the image) and is left open.
        /// </summary>
        public static Task<ImageFrameReader> OpenFrameReaderAsync(Stream stream, CancellationToken cancellationToken = default) =>
            LibraryServices.Sequences.OpenReaderAsync(stream, null, leaveOpen: true, cancellationToken);

        /// <summary>Reads the frames of a stream one at a time with the given limits and formats. The stream is left open.</summary>
        public static Task<ImageFrameReader> OpenFrameReaderAsync(Stream stream, DecoderOptions? options, CancellationToken cancellationToken = default) =>
            LibraryServices.SequencesFor(options).OpenReaderAsync(stream, options, leaveOpen: true, cancellationToken);
        /// <summary>Creates a GIF, APNG or WebP frame writer. Complete it explicitly to finish the file; aborting deletes the path.</summary>
        public static ImageFrameWriter CreateFrameWriter(string path, System.Drawing.Size size, EncoderOptions options, ImageFrameWriterOptions? sequenceOptions = null) =>
            LibraryServices.Sequences.CreateWriter(path, size, options, sequenceOptions);

        /// <summary>Creates a frame writer with the default encoder options for the given format.</summary>
        public static ImageFrameWriter CreateFrameWriter(string path, System.Drawing.Size size, ImageFormat format, ImageFrameWriterOptions? sequenceOptions = null)
        {
            var service = LibraryServices.Sequences;
            return service.CreateWriter(path, size, service.DefaultOptions(format), sequenceOptions);
        }

        /// <summary>Creates a frame writer on a caller-owned stream, which stays open. See <see cref="ImageFrameWriterOptions.UseTemporaryFile"/> for outputs without seek.</summary>
        public static ImageFrameWriter CreateFrameWriter(Stream stream, System.Drawing.Size size, EncoderOptions options, ImageFrameWriterOptions? sequenceOptions = null) =>
            LibraryServices.Sequences.CreateWriter(stream, size, options, sequenceOptions);

        /// <summary>Creates a frame writer on a caller-owned stream with the default options for the given format.</summary>
        public static ImageFrameWriter CreateFrameWriter(Stream stream, System.Drawing.Size size, ImageFormat format, ImageFrameWriterOptions? sequenceOptions = null)
        {
            var service = LibraryServices.Sequences;
            return service.CreateWriter(stream, size, service.DefaultOptions(format), sequenceOptions);
        }
    }
}
