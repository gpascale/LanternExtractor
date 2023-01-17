using System;
using System.Collections.Generic;
using System.IO;
using Ionic.Zlib;
using LanternExtractor.Infrastructure;
using LanternExtractor.Infrastructure.Logger;
using static System.Net.WebRequestMethods;

namespace LanternExtractor.EQ.Pfs
{
    /// <summary>
    /// Loads and can extract files in the PFS archive
    /// </summary>
    public class PfsArchive
    {
        public string FilePath { get; }
        public string FileName { get; }

        private List<PfsFile> _files = new List<PfsFile>();
        private Dictionary<string, PfsFile> _fileNameReference = new Dictionary<string, PfsFile>();
        private ILogger _logger;

        public bool IsWldArchive { get; set; }

        public Dictionary<string, string> FilenameChanges = new Dictionary<string, string>();

        public PfsArchive(string filePath, ILogger logger)
        {
            FilePath = filePath;
            FileName = Path.GetFileName(filePath);
            _logger = logger;
        }

        public byte[] GetFileContents(BinaryReader reader, uint offset, uint size) {
            var fileBytes = new byte[size];

            reader.BaseStream.Position = offset;

            uint inflatedSize = 0;

            while (inflatedSize != size)
            {
                uint deflatedLength = reader.ReadUInt32();
                uint inflatedLength = reader.ReadUInt32();

                if (deflatedLength >= reader.BaseStream.Length)
                {
                    _logger.LogError("PfsArchive: Corrupted file length detected!");
                    throw new Exception("PfsArchive: Corrupted file length detected!");
                }

                byte[] compressedBytes = reader.ReadBytes((int)deflatedLength);
                byte[] inflatedBytes;

                if (!InflateBlock(compressedBytes, (int)inflatedLength, out inflatedBytes, _logger))
                {
                    _logger.LogError("PfsArchive: Error occured inflating data");
                    throw new Exception("PfsArchive: Error occured inflating data");
                }

                inflatedBytes.CopyTo(fileBytes, inflatedSize);
                inflatedSize += inflatedLength;
            }

            return fileBytes;
        }

        public bool Initialize()
        {
            _logger.LogInfo("PfsArchive: Started initialization of archive: " + FileName);

            if (!System.IO.File.Exists(FilePath))
            {
                _logger.LogError("PfsArchive: File does not exist at: " + FilePath);
                return false;
            }

            using (var fileStream = new FileStream(FilePath, FileMode.Open, FileAccess.Read))
            {
                var reader = new BinaryReader(fileStream);
                int yoOffset = reader.ReadInt32();
                reader.BaseStream.Position = yoOffset;

                int fileCount = reader.ReadInt32();
                var fileNames = new List<string>();

                uint? directoryOffset = null;
                uint? directorySize = null;
                int? directoryIndex = null;

                for (int i = 0; i < fileCount; i++)
                {
                    // Console.WriteLine("Line {0}", i);
                    uint crc = reader.ReadUInt32();
                    uint offset = reader.ReadUInt32();
                    uint size = reader.ReadUInt32();

                    if ((directoryOffset == null) || (offset > directoryOffset))
                    {
                        //Console.WriteLine("directoryOffset = {0} ({1})", offset, i);
                        directoryOffset = offset;
                        directorySize = size;
                        directoryIndex = i;
                    }

                    if (offset > reader.BaseStream.Length)
                    {
                        _logger.LogError("PfsArchive: Corrupted PFS length detected!");
                        return false;
                    }

                    long cachedOffset = reader.BaseStream.Position;
                    var fileBytes = GetFileContents(reader, offset, size);

                    
                    //if (crc == 0x61580AC9)
                    //{
                    //    var dictionaryStream = new MemoryStream(fileBytes);
                    //    var dictionary = new BinaryReader(dictionaryStream);
                    //    uint filenameCount = dictionary.ReadUInt32();

                    //    for (uint j = 0; j < filenameCount; ++j)
                    //    {
                    //        uint fileNameLength = dictionary.ReadUInt32();
                    //        string filename = new string(dictionary.ReadChars((int)fileNameLength));
                    //        fileNames.Add(filename.Substring(0, filename.Length - 1));
                    //    }

                    //    reader.BaseStream.Position = cachedOffset;

                    //    continue;
                    //}

                    _files.Add(new PfsFile(crc, size, offset, fileBytes));

                    reader.BaseStream.Position = cachedOffset;
                }

                if (directoryOffset != null && directorySize != null && directoryIndex != null) {
                    var fileBytes = GetFileContents(reader, directoryOffset.Value, directorySize.Value);
                    var dictionaryStream = new MemoryStream(fileBytes);
                    var dictionary = new BinaryReader(dictionaryStream);
                    uint filenameCount = dictionary.ReadUInt32();

                    for (uint j = 0; j < filenameCount; ++j)
                    {
                        uint fileNameLength = dictionary.ReadUInt32();
                        string filename = new string(dictionary.ReadChars((int)fileNameLength));
                        fileNames.Add(filename.Substring(0, filename.Length - 1));
                    }

                    _files.RemoveAt(directoryIndex.Value);
                }

                Console.WriteLine("fileNames: {0}", string.Join(" ", fileNames));

                // Sort files by offset so we can assign names
                _files.Sort((x, y) => x.Offset.CompareTo(y.Offset));

                // Assign file names
                for (int i = 0; i < _files.Count; ++i)
                {
                    _files[i].Name = fileNames[i];
                    _fileNameReference[fileNames[i]] = _files[i];

                    if (!IsWldArchive && fileNames[i].EndsWith(".wld"))
                    {
                        IsWldArchive = true;
                    }
                }

                _logger.LogInfo("PfsArchive: Finished initialization of archive: " + FileName);
            }

            return true;
        }

        private static bool InflateBlock(byte[] deflatedBytes, int inflatedSize, out byte[] inflatedBytes,
            ILogger logger)
        {
            var output = new byte[inflatedSize];

            using (var memoryStream = new MemoryStream())
            {
                var zlibCodec = new ZlibCodec();
                zlibCodec.InitializeInflate(true);

                zlibCodec.InputBuffer = deflatedBytes;
                zlibCodec.AvailableBytesIn = deflatedBytes.Length;
                zlibCodec.NextIn = 0;
                zlibCodec.OutputBuffer = output;

                foreach (FlushType f in new[] { FlushType.None, FlushType.Finish })
                {
                    int bytesToWrite;

                    do
                    {
                        zlibCodec.AvailableBytesOut = inflatedSize;
                        zlibCodec.NextOut = 0;
                        try
                        {
                            zlibCodec.Inflate(f);
                        }
                        catch (Exception e)
                        {
                            inflatedBytes = null;
                            logger.LogError("PfsArchive: Exception caught while inflating bytes: " + e);
                            return false;
                        }

                        bytesToWrite = inflatedSize - zlibCodec.AvailableBytesOut;
                        if (bytesToWrite > 0)
                            memoryStream.Write(output, 0, bytesToWrite);
                    }
                    while (f == FlushType.None &&
                             (zlibCodec.AvailableBytesIn != 0 || zlibCodec.AvailableBytesOut == 0) ||
                             f == FlushType.Finish && bytesToWrite != 0);
                }

                zlibCodec.EndInflate();

                inflatedBytes = output;
                return true;
            }
        }

        public PfsFile GetFile(string fileName)
        {
            return !_fileNameReference.ContainsKey(fileName) ? null : _fileNameReference[fileName];
        }

        public PfsFile GetFile(int index)
        {
            if (index < 0 || index >= _files.Count)
            {
                return null;
            }

            return _files[index];
        }

        public PfsFile[] GetAllFiles()
        {
            return _files.ToArray();
        }

        public void WriteAllFiles(string folder)
        {
            foreach (var file in _files)
            {
                FileWriter.WriteBytesToDisk(file.Bytes, folder, file.Name);
            }
        }

        public void RenameFile(string originalName, string newName)
        {
            if (!_fileNameReference.ContainsKey(originalName))
            {
                return;
            }

            var file = _fileNameReference[originalName];
            _fileNameReference.Remove(originalName);
            file.Name = newName;
            _fileNameReference[newName] = file;
        }
    }
}