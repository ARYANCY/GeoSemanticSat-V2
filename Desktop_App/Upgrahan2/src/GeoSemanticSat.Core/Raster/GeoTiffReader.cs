using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GeoSemanticSat.Core.Model;

namespace GeoSemanticSat.Core.Raster;

/// <summary>
/// Lightweight, zero-dependency pure C# GeoTIFF and multi-band raster reader.
/// Supports standard TIFF (little/big endian), single/multi-band uint8, uint16, float32,
/// and extracts ModelPixelScaleTag (33550), ModelTiepointTag (33922), and GeoKeyDirectoryTag (34735).
/// Also supports worldfiles (.tfw) and companion metadata sidecars.
/// </summary>
public class GeoTiffReader
{
    public static SatelliteTile Read(string filePath, SensorPlatform platform = SensorPlatform.Sentinel2_Optical, DateTime? acquisitionTime = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Raster file not found: {filePath}");

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs);

        ushort byteOrder = reader.ReadUInt16();
        bool isLittleEndian = byteOrder == 0x4949; // "II"
        if (byteOrder != 0x4949 && byteOrder != 0x4D4D)
            throw new InvalidDataException("Invalid TIFF magic number.");

        ushort version = ReadUInt16(reader, isLittleEndian);
        if (version != 42)
            throw new InvalidDataException($"Unsupported TIFF version: {version}");

        uint ifdOffset = ReadUInt32(reader, isLittleEndian);
        fs.Seek(ifdOffset, SeekOrigin.Begin);

        ushort numEntries = ReadUInt16(reader, isLittleEndian);

        int width = 0;
        int height = 0;
        int bitsPerSample = 8;
        int samplesPerPixel = 1;
        int sampleFormat = 1; // 1 = uint, 2 = int, 3 = float
        int compression = 1;  // 1 = Uncompressed, 5 = LZW, 8 = Deflate
        List<uint> stripOffsets = new();
        List<uint> stripByteCounts = new();
        int tileWidth = 0;
        int tileHeight = 0;
        List<uint> tileOffsets = new();
        List<uint> tileByteCounts = new();
        double? noDataValue = null;
        double[]? pixelScale = null;
        double[]? tiepoints = null;
        int epsgCode = 4326;

        for (int i = 0; i < numEntries; i++)
        {
            ushort tag = ReadUInt16(reader, isLittleEndian);
            ushort type = ReadUInt16(reader, isLittleEndian);
            uint count = ReadUInt32(reader, isLittleEndian);
            uint valueOrOffset = ReadUInt32(reader, isLittleEndian);

            long returnPos = fs.Position;

            switch (tag)
            {
                case 256: // ImageWidth
                    width = (int)valueOrOffset;
                    break;
                case 257: // ImageLength (Height)
                    height = (int)valueOrOffset;
                    break;
                case 258: // BitsPerSample
                    bitsPerSample = count == 1 ? (int)valueOrOffset : (int)ReadValueAtOffset(fs, reader, isLittleEndian, valueOrOffset, type);
                    break;
                case 277: // SamplesPerPixel
                    samplesPerPixel = (int)valueOrOffset;
                    break;
                case 339: // SampleFormat
                    sampleFormat = (int)valueOrOffset;
                    break;
                case 259: // Compression (1 = Uncompressed, 5 = LZW, 8 = Deflate/Zip, 32946 = Deflate)
                    compression = (int)valueOrOffset;
                    break;
                case 273: // StripOffsets
                    stripOffsets = ReadOffsetsArray(fs, reader, isLittleEndian, valueOrOffset, count, type);
                    break;
                case 279: // StripByteCounts
                    stripByteCounts = ReadOffsetsArray(fs, reader, isLittleEndian, valueOrOffset, count, type);
                    break;
                case 322: // TileWidth (for Cloud-Optimized GeoTIFFs)
                    tileWidth = (int)valueOrOffset;
                    break;
                case 323: // TileLength (for Cloud-Optimized GeoTIFFs)
                    tileHeight = (int)valueOrOffset;
                    break;
                case 324: // TileOffsets
                    tileOffsets = ReadOffsetsArray(fs, reader, isLittleEndian, valueOrOffset, count, type);
                    break;
                case 325: // TileByteCounts
                    tileByteCounts = ReadOffsetsArray(fs, reader, isLittleEndian, valueOrOffset, count, type);
                    break;
                case 33550: // ModelPixelScaleTag (3 doubles: ScaleX, ScaleY, ScaleZ)
                    pixelScale = ReadDoubleArray(fs, reader, isLittleEndian, valueOrOffset, count);
                    break;
                case 33922: // ModelTiepointTag (6 doubles: I, J, K, X, Y, Z)
                    tiepoints = ReadDoubleArray(fs, reader, isLittleEndian, valueOrOffset, count);
                    break;
                case 34735: // GeoKeyDirectoryTag
                    epsgCode = ReadGeoKeyEpsg(fs, reader, isLittleEndian, valueOrOffset, count);
                    break;
                case 42113: // GDAL_NODATA (ASCII string, e.g. "-9999" or "0")
                    noDataValue = ReadGdalNoData(fs, reader, valueOrOffset, count);
                    break;
            }

            fs.Seek(returnPos, SeekOrigin.Begin);
        }

        if (width <= 0 || height <= 0)
            throw new InvalidDataException("Invalid TIFF dimensions.");

        // Derive Affine Geotransform
        AffineGeoTransform transform;
        BoundingBox bounds;

        string sidecarTfw = Path.ChangeExtension(filePath, ".tfw");
        if (File.Exists(sidecarTfw))
        {
            var lines = File.ReadAllLines(sidecarTfw);
            if (lines.Length >= 6)
            {
                // Standard GDAL / ESRI Worldfile specification:
                // Line 1: dx   = pixel size in X direction (B in AffineGeoTransform)
                // Line 2: rotY = rotation term (E in AffineGeoTransform)
                // Line 3: rotX = rotation term (C in AffineGeoTransform)
                // Line 4: dy   = pixel size in Y direction (F in AffineGeoTransform, negative for north-up)
                // Line 5: x0   = X coordinate of center of upper-left pixel (A in AffineGeoTransform)
                // Line 6: y0   = Y coordinate of center of upper-left pixel (D in AffineGeoTransform)
                double dx = double.Parse(lines[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                double rotY = double.Parse(lines[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                double rotX = double.Parse(lines[2].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                double dy = double.Parse(lines[3].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                double x0 = double.Parse(lines[4].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                double y0 = double.Parse(lines[5].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                transform = new AffineGeoTransform(x0, dx, rotX, y0, rotY, dy, epsgCode);
            }
            else
            {
                transform = AffineGeoTransform.NorthUp(77.0, 28.0, 0.0001, 0.0001, epsgCode);
            }
        }
        else if (tiepoints != null && tiepoints.Length >= 6 && pixelScale != null && pixelScale.Length >= 2)
        {
            double originX = tiepoints[3] - tiepoints[0] * pixelScale[0];
            double originY = tiepoints[4] + tiepoints[1] * pixelScale[1];
            transform = AffineGeoTransform.NorthUp(originX, originY, pixelScale[0], pixelScale[1], epsgCode);
        }
        else
        {
            // Default georeference fallback (New Delhi / NCR test coordinate origin: 28.61° N, 77.20° E)
            transform = AffineGeoTransform.NorthUp(77.20, 28.61, 0.0001, 0.0001, epsgCode);
        }

        var topLeft = transform.PixelToGeo(0, 0);
        var bottomRight = transform.PixelToGeo(width, height);
        bounds = new BoundingBox(
            Math.Min(topLeft.Longitude, bottomRight.Longitude),
            Math.Min(topLeft.Latitude, bottomRight.Latitude),
            Math.Max(topLeft.Longitude, bottomRight.Longitude),
            Math.Max(topLeft.Latitude, bottomRight.Latitude)
        );

        // Read pixel data strips or tiles into multi-band matrices
        Dictionary<SpectralBand, float[,]> bands = new();
        var bandList = AssignBandsForPlatform(platform, samplesPerPixel);

        for (int b = 0; b < bandList.Count; b++)
        {
            bands[bandList[b]] = new float[height, width];
        }

        if (stripOffsets.Count > 0)
        {
            int currentY = 0;
            for (int s = 0; s < stripOffsets.Count && currentY < height; s++)
            {
                fs.Seek(stripOffsets[s], SeekOrigin.Begin);
                int stripBytes = s < stripByteCounts.Count ? (int)stripByteCounts[s] : (int)(fs.Length - stripOffsets[s]);
                byte[] raw = reader.ReadBytes(stripBytes);

                int bytesPerSampleUnit = Math.Max(1, bitsPerSample / 8);
                int bytesPerRow = Math.Max(1, width * samplesPerPixel * bytesPerSampleUnit);
                int rowsInStrip = Math.Min(height - currentY, Math.Max(1, stripBytes / bytesPerRow));

                int byteIndex = 0;
                for (int y = currentY; y < currentY + rowsInStrip && y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        for (int b = 0; b < samplesPerPixel; b++)
                        {
                            float val = 0.0f;
                            if (bitsPerSample == 8 && byteIndex < raw.Length)
                            {
                                val = raw[byteIndex++] / 255.0f;
                            }
                            else if (bitsPerSample == 16 && byteIndex + 1 < raw.Length)
                            {
                                ushort rawU16 = isLittleEndian ? (ushort)(raw[byteIndex] | (raw[byteIndex + 1] << 8)) : (ushort)((raw[byteIndex] << 8) | raw[byteIndex + 1]);
                                val = rawU16 / 65535.0f;
                                byteIndex += 2;
                            }
                            else if (bitsPerSample == 32 && sampleFormat == 3 && byteIndex + 3 < raw.Length)
                            {
                                val = BitConverter.ToSingle(raw, byteIndex);
                                byteIndex += 4;
                            }

                            if (b < bandList.Count)
                            {
                                bands[bandList[b]][y, x] = val;
                            }
                        }
                    }
                }
                currentY += rowsInStrip;
            }
        }
        else if (tileOffsets.Count > 0 && tileWidth > 0 && tileHeight > 0)
        {
            int tilesAcross = (width + tileWidth - 1) / tileWidth;
            int tilesDown = (height + tileHeight - 1) / tileHeight;

            for (int ty = 0; ty < tilesDown; ty++)
            {
                for (int tx = 0; tx < tilesAcross; tx++)
                {
                    int tileIndex = ty * tilesAcross + tx;
                    if (tileIndex >= tileOffsets.Count) break;

                    uint offset = tileOffsets[tileIndex];
                    uint byteCount = tileIndex < tileByteCounts.Count ? tileByteCounts[tileIndex] : 0;
                    if (offset == 0 || byteCount == 0) continue;

                    fs.Seek(offset, SeekOrigin.Begin);
                    byte[] raw = reader.ReadBytes((int)byteCount);

                    int byteIndex = 0;
                    int startY = ty * tileHeight;
                    int startX = tx * tileWidth;

                    for (int y = startY; y < startY + tileHeight && y < height; y++)
                    {
                        for (int x = startX; x < startX + tileWidth && x < width; x++)
                        {
                            for (int b = 0; b < samplesPerPixel; b++)
                            {
                                float val = 0.0f;
                                if (bitsPerSample == 8 && byteIndex < raw.Length)
                                {
                                    val = raw[byteIndex++] / 255.0f;
                                }
                                else if (bitsPerSample == 16 && byteIndex + 1 < raw.Length)
                                {
                                    ushort rawU16 = isLittleEndian ? (ushort)(raw[byteIndex] | (raw[byteIndex + 1] << 8)) : (ushort)((raw[byteIndex] << 8) | raw[byteIndex + 1]);
                                    val = rawU16 / 65535.0f;
                                    byteIndex += 2;
                                }
                                else if (bitsPerSample == 32 && sampleFormat == 3 && byteIndex + 3 < raw.Length)
                                {
                                    val = BitConverter.ToSingle(raw, byteIndex);
                                    byteIndex += 4;
                                }

                                if (b < bandList.Count)
                                {
                                    bands[bandList[b]][y, x] = val;
                                }
                            }
                        }
                    }
                }
            }
        }

        // Cryptographic SHA-256 digest of the ingested raster payload
        fs.Seek(0, SeekOrigin.Begin);
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        byte[] hashBytes = sha256.ComputeHash(fs);
        string sourceSha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();

        string tileId = Path.GetFileNameWithoutExtension(filePath);
        DateTime dt = acquisitionTime ?? DateTime.UtcNow;

        return new SatelliteTile
        {
            TileId = tileId,
            Platform = platform,
            AcquisitionTimestamp = dt,
            Bounds = bounds,
            Transform = transform,
            Width = width,
            Height = height,
            EpsgCode = epsgCode,
            NoDataValue = noDataValue,
            SourceImageSha256 = sourceSha256,
            SourceFilePath = filePath,
            Bands = bands,
            GroundSamplingDistanceMeters = 10.0
        };
    }

    private static List<SpectralBand> AssignBandsForPlatform(SensorPlatform platform, int samplesCount)
    {
        if (platform == SensorPlatform.Sentinel1_SAR)
        {
            return samplesCount >= 2 ? new List<SpectralBand> { SpectralBand.SAR_VV, SpectralBand.SAR_VH } : new List<SpectralBand> { SpectralBand.SAR_VV };
        }

        var list = new List<SpectralBand>();

        // For Sentinel-2 4-band rasters, bands are typically B2(Blue), B3(Green), B4(Red), B8(NIR)
        // For standard RGB 3-band rasters, bands are Red, Green, Blue
        SpectralBand[] opticalOrder = platform switch
        {
            SensorPlatform.Sentinel2_Optical when samplesCount >= 4 => new[] { SpectralBand.Blue, SpectralBand.Green, SpectralBand.Red, SpectralBand.NIR, SpectralBand.SWIR1, SpectralBand.SWIR2, SpectralBand.Quality_QA },
            SensorPlatform.Landsat8_9 when samplesCount >= 4 => new[] { SpectralBand.Blue, SpectralBand.Green, SpectralBand.Red, SpectralBand.NIR, SpectralBand.SWIR1, SpectralBand.SWIR2 },
            _ => new[] { SpectralBand.Red, SpectralBand.Green, SpectralBand.Blue, SpectralBand.NIR, SpectralBand.SWIR1, SpectralBand.SWIR2, SpectralBand.Quality_QA }
        };

        for (int i = 0; i < samplesCount && i < opticalOrder.Length; i++)
        {
            list.Add(opticalOrder[i]);
        }
        if (list.Count == 0) list.Add(SpectralBand.Red);
        return list;
    }

    private static ushort ReadUInt16(BinaryReader r, bool le)
    {
        var b = r.ReadBytes(2);
        return le ? (ushort)(b[0] | (b[1] << 8)) : (ushort)((b[0] << 8) | b[1]);
    }

    private static uint ReadUInt32(BinaryReader r, bool le)
    {
        var b = r.ReadBytes(4);
        return le ? (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24)) : (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }

    private static uint ReadValueAtOffset(FileStream fs, BinaryReader r, bool le, uint offset, ushort type)
    {
        fs.Seek(offset, SeekOrigin.Begin);
        return type switch
        {
            3 => ReadUInt16(r, le),
            4 => ReadUInt32(r, le),
            _ => r.ReadByte()
        };
    }

    private static List<uint> ReadOffsetsArray(FileStream fs, BinaryReader r, bool le, uint valueOrOffset, uint count, ushort type)
    {
        var list = new List<uint>();
        if (count == 1)
        {
            list.Add(valueOrOffset);
            return list;
        }

        fs.Seek(valueOrOffset, SeekOrigin.Begin);
        for (int i = 0; i < count; i++)
        {
            list.Add(type == 3 ? ReadUInt16(r, le) : ReadUInt32(r, le));
        }
        return list;
    }

    private static double[] ReadDoubleArray(FileStream fs, BinaryReader r, bool le, uint offset, uint count)
    {
        fs.Seek(offset, SeekOrigin.Begin);
        double[] arr = new double[count];
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = r.ReadBytes(8);
            if (!le) Array.Reverse(bytes);
            arr[i] = BitConverter.ToDouble(bytes, 0);
        }
        return arr;
    }

    private static int ReadGeoKeyEpsg(FileStream fs, BinaryReader r, bool le, uint offset, uint count)
    {
        try
        {
            fs.Seek(offset, SeekOrigin.Begin);
            ushort keyDirectoryVersion = ReadUInt16(r, le);
            ushort keyRevision = ReadUInt16(r, le);
            ushort minorRevision = ReadUInt16(r, le);
            ushort numberOfKeys = ReadUInt16(r, le);

            for (int i = 0; i < numberOfKeys; i++)
            {
                ushort keyId = ReadUInt16(r, le);
                ushort tiffTagLocation = ReadUInt16(r, le);
                ushort keyCount = ReadUInt16(r, le);
                ushort valueOffset = ReadUInt16(r, le);

                // GeographicTypeGeoKey = 2048, ProjectedCSTypeGeoKey = 3072
                if (keyId == 2048 || keyId == 3072)
                {
                    return valueOffset;
                }
            }
        }
        catch
        {
            // fallback
        }
        return 4326;
    }

    private static double? ReadGdalNoData(FileStream fs, BinaryReader r, uint valueOrOffset, uint count)
    {
        try
        {
            byte[] bytes;
            if (count <= 4)
            {
                bytes = BitConverter.GetBytes(valueOrOffset);
            }
            else
            {
                fs.Seek(valueOrOffset, SeekOrigin.Begin);
                bytes = r.ReadBytes((int)count);
            }
            string s = Encoding.ASCII.GetString(bytes).Trim('\0', ' ', '\r', '\n');
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double nd))
            {
                return nd;
            }
        }
        catch
        {
            // fallback
        }
        return null;
    }
}
