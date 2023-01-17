using System;
using System.IO;
using System.Text;
using LanternExtractor.Infrastructure.Logger;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Pfim;
using ImageFormat = System.Drawing.Imaging.ImageFormat;
using LanternExtractor.Infrastructure;
using static System.Net.WebRequestMethods;

namespace LanternExtractor.Infrastructure
{
    public static class ImageWriter
    {
        public static void WriteImageAsPng(byte[] bytes, string filePath, string fileName, bool isMasked,
            ILogger logger)
        {
            //Console.WriteLine("write that shit: {0}", fileName);
            // https://docs.microsoft.com/en-us/windows/win32/direct3ddds/dx-graphics-dds-pguide#dds-file-layout
            bool ddsMagic = Encoding.ASCII.GetString(bytes, 0, 4) == "DDS ";
            if (fileName.EndsWith(".bmp") && !ddsMagic)
            {
                WriteBmpAsPng(bytes, filePath, Path.GetFileNameWithoutExtension(fileName) + ".png", isMasked, false,
                    logger);
            }
            else
            {
                WriteDdsAsPng(bytes, filePath, Path.GetFileNameWithoutExtension(fileName) + ".png");
            }
        }

        private static void WriteBmpAsPng(byte[] bytes, string filePath, string fileName, bool isMasked, bool rotate,
            ILogger logger)
        {
            // Console.WriteLine("Writing {0} ({1} bytes)", fileName, bytes.Length);
            var byteStream = new MemoryStream(bytes);

            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            Directory.CreateDirectory(filePath);

            Bitmap image;

            try
            {
                image = new Bitmap(byteStream);
            }
            catch (Exception e)
            {
                logger.LogError("Caught exception while creating bitmap: " + e);
                return;
            }

            // The filename is misspelled in the archive
            // It only works because there is a matching canwall1.png in the objects archive
            // If we find more like this, we can create a function to fix them.
            if (fileName == "canwall1a.png")
            {
                fileName = "canwall1.png";
            }

            Bitmap cloneBitmap;

            if (isMasked)
            {
                cloneBitmap = image.Clone(new Rectangle(0, 0, image.Width, image.Height),
                    PixelFormat.Format8bppIndexed);

                int paletteIndex = GetPaletteIndex(fileName);
                var palette = cloneBitmap.Palette;

                if (Environment.OSVersion.Platform != PlatformID.MacOSX &&
                    Environment.OSVersion.Platform != PlatformID.Unix)
                {
                    palette.Entries[paletteIndex] = Color.FromArgb(0, 0, 0, 0);
                    cloneBitmap.Palette = palette;
                }
                else
                {
                    // Due to a bug with the MacOS implementation of System.Drawing, setting a color palette value to
                    // transparent does not work. The workaround is to ensure that the first palette value (the transparent
                    // key) is unique and then use MakeTransparent()

                    // Console.WriteLine("paletteIndex: {0}", paletteIndex);
                    // Console.WriteLine("palette.Entries: {0}, {1}", palette.Entries, palette.Entries.Length);

                    Color transparencyColor = palette.Entries.Length > paletteIndex ? palette.Entries[paletteIndex] : Color.Transparent;
                    bool isUnique = false;
                    
                    // Console.WriteLine("transparencyColor: {0}", transparencyColor);

                    while (!isUnique)
                    {
                        isUnique = true;

                        for (var i = 1; i < cloneBitmap.Palette.Entries.Length; i++)
                        {
                            Color paletteValue = cloneBitmap.Palette.Entries[i];

                            if (paletteValue == transparencyColor)
                            {
                                Random random = new Random();
                                transparencyColor = Color.FromArgb(random.Next(256), random.Next(256), random.Next(256));
                                isUnique = false;
                                break;
                            }
                        }
                    }

                    if (palette.Entries.Length > paletteIndex) {
                        palette.Entries[paletteIndex] = transparencyColor;
                        cloneBitmap.Palette = palette;
                    }
                    else {
                        Console.WriteLine("funny business");
                    }
                    cloneBitmap.MakeTransparent(transparencyColor);

                    // Console.WriteLine("Width: {0}, Height: {1}", cloneBitmap.Width, cloneBitmap.Height);

                    // For some reason, this now has to be done to ensure the pixels are actually set to transparent
                    // Another head scratching MacOS bug
                    for (int i = 0; i < cloneBitmap.Width; ++i)
                    {
                        for (int j = 0; j < cloneBitmap.Height; ++j)
                        {
                            if (cloneBitmap.GetPixel(i, j) == transparencyColor)
                            {
                                cloneBitmap.SetPixel(i, j, Color.FromArgb(0, 0, 0, 0));
                            }
                        }
                    }
                }
            }
            else
            {
                cloneBitmap = image.Clone(new Rectangle(0, 0, image.Width, image.Height), PixelFormat.Format32bppArgb);
                if (image.PixelFormat != PixelFormat.Format8bppIndexed)
                {
                    cloneBitmap.MakeTransparent(Color.Magenta);
                }
            }

            cloneBitmap.Save(Path.Combine(filePath, fileName), ImageFormat.Png);
        }

        private static void WriteDdsAsPng(byte[] bytes, string filePath, string fileName)
        {
            //Console.WriteLine("DDS yo: {0}", fileName);

            DDS.DDSImage ddsImage = DDS.DDSImage.Load(bytes);
            //Console.WriteLine("ddsImage {0}", ddsImage);
            //Console.WriteLine("ddsImage format {0} ({1})", ddsImage.Format, ddsImage.FormatName);

            {
                PixelFormat format;

                //Console.WriteLine("image dimensions: {0} x {1}", image.Width, image.Height);
                //Console.WriteLine("image bits per pixel: {0}", image.BitsPerPixel);
                //Console.WriteLine("image DataLen: {0}", image.DataLen);
                //Console.WriteLine("image format: {0}", image.Format);

                // Convert from Pfim's backend agnostic image format into GDI+'s image format
                switch (ddsImage.Format)
                {
                    case DDS.DDSImage.CompressionMode.RGB32:
                        format = PixelFormat.Format32bppArgb;
                        break;
                    case DDS.DDSImage.CompressionMode.R5G6B5:
                        format = PixelFormat.Format16bppRgb565;
                        break;
                    case DDS.DDSImage.CompressionMode.A1R5G5B5:
                        format = PixelFormat.Format16bppArgb1555;
                        break;
                    default:
                        return;
                        // throw new Exception("unknown image format: " + image.Format);
                }
                //Console.WriteLine("format: {0}", format);


                // Pin pfim's data array so that it doesn't get reaped by GC, unnecessary
                // in this snippet but useful technique if the data was going to be used in
                // control like a picture box
                //var handle = GCHandle.Alloc(image.Data, GCHandleType.Pinned);
                //try
                //{
                //    var data = Marshal.UnsafeAddrOfPinnedArrayElement(image.Data, 0);
                if (ddsImage.Images.Length < 1)
                {
                    return;
                }
                var bitmap = ddsImage.Images[0];
                //Console.WriteLine("dds formattypoo: {0}", ddsImage.Format);
                //Console.WriteLine("bmp formattypoo: {0}", bitmap.PixelFormat);
                //Console.WriteLine("image dimensions: {0} x {1}", bitmap.Width, bitmap.Height);
                var folderPath = Path.Combine("/Users/gtp/git/eq/LanternExtractor/LanternExtractor/", filePath);
                Directory.CreateDirectory(folderPath); 
                var path = Path.Combine(folderPath, fileName);
                Console.WriteLine(path);
                //Console.WriteLine("path {0}", path);
                bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);
                bitmap.Save(path, ImageFormat.Png);


                //    bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);
                //    Directory.CreateDirectory(filePath); 
                //    Console.WriteLine("writing to {0}", Path.Combine(filePath, fileName));
                // using (Bitmap newBmp = new Bitmap(bitmap))
                // using (Bitmap targetBmp = newBmp.Clone(new Rectangle(0, 0, newBmp.Width, newBmp.Height), PixelFormat.Format32bppArgb))
                // {
                //     // targetBmp is now in the desired format.
                //     targetBmp.Save(Path.Combine(filePath, fileName), ImageFormat.Bmp);
                // }
                //    Console.WriteLine("wrote something");
                //}
                //catch(Exception ex) {
                //    Console.WriteLine("exc: {0}", ex);
                //    // throw ex;
                //}
                //finally
                //{
                //    Console.WriteLine("free");
                //    handle.Free();
                //}
            }
        }

        private static int GetPaletteIndex(string fileName)
        {
            switch (fileName)
            {
                case "clhe0004.png":
                case "kahe0001.png":
                    return 255;
                case "furpile1.png":
                    return 250;
                case "bearrug.png":
                    return 47;
                default:
                    return 0;
            }
        }
    }
}
