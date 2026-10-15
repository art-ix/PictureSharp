using PictureSharp.Composition;

namespace PictureSharp;

/// <summary>The application-wide default <see cref="ImageConfiguration"/>.</summary>
public static class ImageConfigurationExtensions
{
    extension(ImageConfiguration)
    {
        /// <summary>
        /// The configuration used by <c>Image.Load</c>, <c>Image.Identify</c>, <c>image.Save</c> and format lookup
        /// when no other configuration is given. Initially it contains the built-in formats. Replace it once at
        /// application startup; loads already running keep the configuration they started with.
        /// </summary>
        /// <example>
        /// <code>
        /// ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().RemoveFormat(ImageFormat.Pcx).Build();
        /// </code>
        /// </example>
        /// <exception cref="ArgumentNullException">The value is null.</exception>
        public static ImageConfiguration Default
        {
            get => LibraryServices.Configuration;
            set => LibraryServices.Configuration = value;
        }
    }
}
