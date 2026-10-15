using PictureSharp.Application.Configuration;
using PictureSharp.Formats;

namespace PictureSharp;

/// <summary>
/// Collects the formats of a new <see cref="ImageConfiguration"/>. Obtain one from
/// <see cref="ImageConfiguration.ToBuilder"/>; a builder is not thread-safe, the configurations it builds are.
/// </summary>
public sealed class ImageConfigurationBuilder
{
    private readonly ImagePorts _ports;
    private readonly List<FormatRegistration> _registrations;

    internal ImageConfigurationBuilder(ImagePorts ports, IEnumerable<FormatRegistration> registrations)
    {
        _ports = ports;
        _registrations = [.. registrations];
    }

    /// <summary>The formats the configuration will contain, in detection order.</summary>
    public IReadOnlyList<ImageFormat> Formats => [.. _registrations.Select(static registration => registration.Format)];

    /// <summary>
    /// Adds a format plugin after the formats already registered, so built-in signatures are checked first.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The module is invalid, or its format, name, MIME type or a file extension is already registered.
    /// </exception>
    public ImageConfigurationBuilder AddFormat(IImageFormatModule module)
    {
        FormatRegistration registration = FormatRegistration.FromModule(module);
        EnsureUnique(registration.Format);
        _registrations.Add(registration);
        return this;
    }

    /// <summary>
    /// Replaces the codecs of a registered format, keeping its detection position. The module's
    /// <see cref="IImageFormatModule.Format"/> must be the registered instance, e.g. <see cref="ImageFormat.Png"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The module is invalid or its format is not registered.</exception>
    public ImageConfigurationBuilder ReplaceFormat(IImageFormatModule module)
    {
        FormatRegistration registration = FormatRegistration.FromModule(module);
        int index = _registrations.FindIndex(existing => existing.Format == registration.Format);
        if (index < 0)
        {
            throw new ArgumentException($"The format {registration.Format.Name} is not registered; use AddFormat.", nameof(module));
        }

        _registrations[index] = registration;
        return this;
    }

    /// <summary>Removes a format; a format that is not registered is ignored.</summary>
    public ImageConfigurationBuilder RemoveFormat(ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        _registrations.RemoveAll(registration => registration.Format == format);
        return this;
    }

    /// <summary>Removes every format except <paramref name="formats"/>, keeping the detection order.</summary>
    /// <exception cref="ArgumentException">No format is given, or one of them is not registered.</exception>
    public ImageConfigurationBuilder KeepOnly(params ReadOnlySpan<ImageFormat> formats)
    {
        if (formats.IsEmpty)
        {
            throw new ArgumentException("At least one format must be kept.", nameof(formats));
        }

        var kept = new HashSet<ImageFormat>();
        foreach (ImageFormat format in formats)
        {
            ArgumentNullException.ThrowIfNull(format, nameof(formats));
            if (!_registrations.Exists(registration => registration.Format == format))
            {
                throw new ArgumentException($"The format {format.Name} is not registered.", nameof(formats));
            }

            kept.Add(format);
        }

        _registrations.RemoveAll(registration => !kept.Contains(registration.Format));
        return this;
    }

    // Lookup by name, MIME type and extension returns the first match, so every key must stay unambiguous.
    private void EnsureUnique(ImageFormat format)
    {
        foreach (FormatRegistration existing in _registrations)
        {
            ImageFormat other = existing.Format;
            string? conflict = other == format ? "format"
                : string.Equals(other.Name, format.Name, StringComparison.OrdinalIgnoreCase) ? $"name {format.Name}"
                : string.Equals(other.MimeType, format.MimeType, StringComparison.OrdinalIgnoreCase) ? $"MIME type {format.MimeType}"
                : format.FileExtensions.FirstOrDefault(other.FileExtensions.Contains) is { } extension ? $"extension {extension}"
                : null;
            if (conflict is not null)
            {
                throw new ArgumentException($"The {conflict} is already registered for {other.Name}.", "module");
            }
        }
    }

    /// <summary>Creates the configuration.</summary>
    /// <exception cref="InvalidOperationException">No format is left.</exception>
    public ImageConfiguration Build() => new(_ports, [.. _registrations]);
}
