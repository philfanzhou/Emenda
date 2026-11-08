using ImageMagick;
using ImageMagick.Configuration;

namespace Emenda.Common.Oss;

/// <summary>One immutable process-wide native policy, initialized before any raster read.</summary>
internal static class ImageDecoderPolicy
{
    static ImageDecoderPolicy()
    {
        var configuration = ConfigurationFiles.Default;
        configuration.Policy.Data = """
            <policymap>
              <policy domain="delegate" rights="none" pattern="*" />
              <policy domain="module" rights="none" pattern="*" />
              <policy domain="module" rights="read|write" pattern="{JPEG,PNG,GIF,BMP,PNM,TGA,TIFF,WEBP,QOI}" />
              <policy domain="coder" rights="none" pattern="*" />
              <policy domain="coder" rights="read|write" pattern="{JPEG,JPG,JPE,PNG,PNG8,PNG24,PNG32,PNG48,PNG64,GIF,BMP,BMP2,BMP3,PBM,PGM,PPM,PNM,TGA,TIFF,TIFF64,WEBP,QOI}" />
              <policy domain="path" rights="none" pattern="@*" />
              <policy domain="resource" name="memory" value="256MiB" />
              <policy domain="resource" name="map" value="512MiB" />
              <policy domain="resource" name="disk" value="2GiB" />
              <policy domain="resource" name="area" value="128MP" />
              <policy domain="resource" name="width" value="2147483647" />
              <policy domain="resource" name="height" value="2147483647" />
              <policy domain="resource" name="thread" value="1" />
              <policy domain="resource" name="list-length" value="65534" />
              <policy domain="resource" name="time" value="120" />
            </policymap>
            """;
        MagickNET.Initialize(configuration);
        // ImageMagick uses its dedicated temporary cache, never an OSS/business directory.
        MagickNET.SetTempDirectory(Path.GetTempPath());
    }

    internal static void EnsureInitialized() { }
}
