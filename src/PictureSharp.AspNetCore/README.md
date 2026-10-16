# PictureSharp.AspNetCore

Returns [PictureSharp](https://www.nuget.org/packages/PictureSharp) images from ASP.NET Core endpoints and reads uploaded images.

```csharp
using PictureSharp;
using PictureSharp.AspNetCore;

app.MapGet("/thumb/{name}", async (string name, CancellationToken ct) =>
{
    Image image = await Image.LoadAsync(Path.Combine("images", Path.GetFileName(name)),
        new DecoderOptions { TargetSize = new(400, 0) }, ct);
    return Results.Extensions.Image(image, new ImageResultOptions { CacheControl = "public, max-age=86400" });
});

app.MapPost("/upload", async (HttpRequest request) =>
{
    using Image image = await request.ReadImageAsync(new DecoderOptions { MaxInputBytes = 10_000_000 });
    return Results.Ok(new { image.Width, image.Height });
});
```

- Chooses WebP, PNG or JPEG (configurable, in server order) from the `Accept` header with quality values and wildcards;
  optional 406 Not Acceptable.
- Encodes into memory first: `Content-Type`, `Content-Length`, `Vary: Accept`, optional `Cache-Control`, HEAD support
  and no partial body after a failure. Stops on `HttpContext.RequestAborted`. Disposes the image unless told not to.
- `ReadImageAsync` throws `BadHttpRequestException` (413) for bodies over `DecoderOptions.MaxInputBytes` before
  buffering them; other errors are those of `Image.LoadAsync`.

Depends only on `PictureSharp` and the ASP.NET Core shared framework. Guide: [Adapters](https://github.com/art-ix/PictureSharp/blob/main/docs/adapters.md). License: MIT.
