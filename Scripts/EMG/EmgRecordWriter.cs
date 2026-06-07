using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HDF.PInvoke;

public enum EmgRecordFormat
{
    Hdf5 = 0,
    Csv = 1
}

public sealed class EmgRecordWriter : IDisposable
{
    private const int CsvFlushInterval = 128;
    private const int Hdf5ChunkFrames = 512;

    private readonly object syncRoot = new object();

    private EmgRecordFormat format = EmgRecordFormat.Hdf5;
    private StreamWriter csvWriter;

    private long h5FileId = -1;
    private long h5EmgGroupId = -1;
    private long h5MetaGroupId = -1;
    private long h5DataSetId = -1;
    private long h5TimeSetId = -1;
    private long h5SecSetId = -1;

    private int[] activeChannels = Array.Empty<int>();
    private string signalLabel = "envelope";
    private float samplingInterval = 0.0009f;
    private float[] dataChunkBuffer = Array.Empty<float>();
    private double[] timeChunkBuffer = Array.Empty<double>();
    private int[] secChunkBuffer = Array.Empty<int>();
    private int bufferedFrames = 0;
    private int channelCount = 0;
    private long writtenFrames = 0;
    private int csvFlushCounter = 0;
    private bool opened = false;

    public void Open(string path, EmgRecordFormat targetFormat, string targetSignalLabel, int[] targetActiveChannels, float targetSamplingInterval)
    {
        lock (syncRoot)
        {
            CloseInternal();

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            format = targetFormat;
            signalLabel = string.IsNullOrWhiteSpace(targetSignalLabel) ? "envelope" : targetSignalLabel;
            activeChannels = targetActiveChannels != null ? (int[])targetActiveChannels.Clone() : Array.Empty<int>();
            channelCount = activeChannels.Length;
            samplingInterval = targetSamplingInterval > 0f ? targetSamplingInterval : 0.0009f;
            writtenFrames = 0;
            csvFlushCounter = 0;
            bufferedFrames = 0;

            if (format == EmgRecordFormat.Csv)
            {
                bool append = File.Exists(path) && new FileInfo(path).Length > 0;
                csvWriter = new StreamWriter(path, append, Encoding.ASCII);
                if (!append)
                {
                    csvWriter.WriteLine(BuildCsvHeader());
                    csvWriter.Flush();
                }
            }
            else
            {
                dataChunkBuffer = new float[Hdf5ChunkFrames * Math.Max(channelCount, 1)];
                timeChunkBuffer = new double[Hdf5ChunkFrames];
                secChunkBuffer = new int[Hdf5ChunkFrames];
                OpenHdf5(path);
            }

            opened = true;
        }
    }

    public void WriteFrame(double timestamp, int sec, float[] frame)
    {
        lock (syncRoot)
        {
            if (!opened || frame == null)
                return;

            if (format == EmgRecordFormat.Csv)
            {
                WriteCsvFrame(timestamp, sec, frame);
                return;
            }

            int baseIndex = bufferedFrames * Math.Max(channelCount, 1);
            timeChunkBuffer[bufferedFrames] = timestamp;
            secChunkBuffer[bufferedFrames] = sec;
            for (int i = 0; i < channelCount; i++)
            {
                int sourceIndex = activeChannels[i] - 1;
                dataChunkBuffer[baseIndex + i] = (sourceIndex >= 0 && sourceIndex < frame.Length) ? frame[sourceIndex] : 0f;
            }

            bufferedFrames++;
            if (bufferedFrames >= Hdf5ChunkFrames)
                FlushHdf5Chunk();
        }
    }

    public void Close()
    {
        lock (syncRoot)
        {
            CloseInternal();
        }
    }

    public void Dispose()
    {
        Close();
    }

    private string BuildCsvHeader()
    {
        StringBuilder header = new StringBuilder("global_t,sec");
        for (int i = 0; i < activeChannels.Length; i++)
            header.Append(',').Append(signalLabel).Append("_ch").Append(activeChannels[i]);
        return header.ToString();
    }

    private void WriteCsvFrame(double timestamp, int sec, float[] frame)
    {
        if (csvWriter == null)
            return;

        StringBuilder line = new StringBuilder(64 + activeChannels.Length * 16);
        line.Append(timestamp).Append(',').Append(sec);
        for (int i = 0; i < activeChannels.Length; i++)
        {
            int sourceIndex = activeChannels[i] - 1;
            float value = (sourceIndex >= 0 && sourceIndex < frame.Length) ? frame[sourceIndex] : 0f;
            line.Append(',').Append(value);
        }

        csvWriter.WriteLine(line.ToString());
        csvFlushCounter++;
        if (csvFlushCounter >= CsvFlushInterval)
        {
            csvWriter.Flush();
            csvFlushCounter = 0;
        }
    }

    private void OpenHdf5(string path)
    {
        h5FileId = H5F.create(path, H5F.ACC_TRUNC);
        if (h5FileId < 0)
            throw new IOException("Failed to create HDF5 file.");

        h5EmgGroupId = H5G.create(h5FileId, "emg");
        h5MetaGroupId = H5G.create(h5FileId, "meta");

        ulong channelDim = (ulong)Math.Max(channelCount, 1);

        long dataSpaceId = H5S.create_simple(2, new ulong[] { 0, channelDim }, new ulong[] { H5S.UNLIMITED, channelDim });
        long dataPropId = H5P.create(H5P.DATASET_CREATE);
        H5P.set_chunk(dataPropId, 2, new ulong[] { (ulong)Hdf5ChunkFrames, channelDim });
        h5DataSetId = H5D.create(h5EmgGroupId, "data", H5T.NATIVE_FLOAT, dataSpaceId, H5P.DEFAULT, dataPropId);
        H5P.close(dataPropId);
        H5S.close(dataSpaceId);

        long timeSpaceId = H5S.create_simple(1, new ulong[] { 0 }, new ulong[] { H5S.UNLIMITED });
        long timePropId = H5P.create(H5P.DATASET_CREATE);
        H5P.set_chunk(timePropId, 1, new ulong[] { (ulong)Hdf5ChunkFrames });
        h5TimeSetId = H5D.create(h5EmgGroupId, "global_t", H5T.IEEE_F64LE, timeSpaceId, H5P.DEFAULT, timePropId);
        H5P.close(timePropId);
        H5S.close(timeSpaceId);

        long secSpaceId = H5S.create_simple(1, new ulong[] { 0 }, new ulong[] { H5S.UNLIMITED });
        long secPropId = H5P.create(H5P.DATASET_CREATE);
        H5P.set_chunk(secPropId, 1, new ulong[] { (ulong)Hdf5ChunkFrames });
        h5SecSetId = H5D.create(h5EmgGroupId, "sec", H5T.NATIVE_INT32, secSpaceId, H5P.DEFAULT, secPropId);
        H5P.close(secPropId);
        H5S.close(secSpaceId);

        WriteIntDataset(h5MetaGroupId, "active_channels", activeChannels);
        WriteDoubleDataset(h5MetaGroupId, "sampling_interval", new[] { (double)samplingInterval });
        WriteDoubleDataset(h5MetaGroupId, "sampling_rate", new[] { 1.0 / samplingInterval });
        WriteStringDataset(h5MetaGroupId, "signal_label", signalLabel);
    }

    private void FlushHdf5Chunk()
    {
        if (bufferedFrames <= 0 || h5DataSetId < 0 || h5TimeSetId < 0 || h5SecSetId < 0)
            return;

        ulong oldFrames = (ulong)writtenFrames;
        ulong newFrames = oldFrames + (ulong)bufferedFrames;
        ulong channels = (ulong)Math.Max(channelCount, 1);

        H5D.set_extent(h5DataSetId, new ulong[] { newFrames, channels });
        long dataFileSpaceId = H5D.get_space(h5DataSetId);
        H5S.select_hyperslab(dataFileSpaceId, H5S.seloper_t.SET, new ulong[] { oldFrames, 0 }, null, new ulong[] { (ulong)bufferedFrames, channels }, null);
        long dataMemSpaceId = H5S.create_simple(2, new ulong[] { (ulong)bufferedFrames, channels }, null);
        WritePinned(h5DataSetId, H5T.NATIVE_FLOAT, dataMemSpaceId, dataFileSpaceId, dataChunkBuffer);
        H5S.close(dataMemSpaceId);
        H5S.close(dataFileSpaceId);

        H5D.set_extent(h5TimeSetId, new ulong[] { newFrames });
        long timeFileSpaceId = H5D.get_space(h5TimeSetId);
        H5S.select_hyperslab(timeFileSpaceId, H5S.seloper_t.SET, new ulong[] { oldFrames }, null, new ulong[] { (ulong)bufferedFrames }, null);
        long timeMemSpaceId = H5S.create_simple(1, new ulong[] { (ulong)bufferedFrames }, null);
        WritePinned(h5TimeSetId, H5T.NATIVE_DOUBLE, timeMemSpaceId, timeFileSpaceId, timeChunkBuffer);
        H5S.close(timeMemSpaceId);
        H5S.close(timeFileSpaceId);

        H5D.set_extent(h5SecSetId, new ulong[] { newFrames });
        long secFileSpaceId = H5D.get_space(h5SecSetId);
        H5S.select_hyperslab(secFileSpaceId, H5S.seloper_t.SET, new ulong[] { oldFrames }, null, new ulong[] { (ulong)bufferedFrames }, null);
        long secMemSpaceId = H5S.create_simple(1, new ulong[] { (ulong)bufferedFrames }, null);
        WritePinned(h5SecSetId, H5T.NATIVE_INT32, secMemSpaceId, secFileSpaceId, secChunkBuffer);
        H5S.close(secMemSpaceId);
        H5S.close(secFileSpaceId);

        writtenFrames += bufferedFrames;
        bufferedFrames = 0;
    }

    private void WriteIntDataset(long parentId, string name, int[] values)
    {
        int[] source = values != null && values.Length > 0 ? values : new[] { 0 };
        long spaceId = H5S.create_simple(1, new ulong[] { (ulong)source.Length }, null);
        long dataSetId = H5D.create(parentId, name, H5T.NATIVE_INT32, spaceId);
        WritePinned(dataSetId, H5T.NATIVE_INT32, H5S.ALL, H5S.ALL, source);
        H5D.close(dataSetId);
        H5S.close(spaceId);
    }

    private void WriteDoubleDataset(long parentId, string name, double[] values)
    {
        double[] source = values != null && values.Length > 0 ? values : new[] { 0.0 };
        long spaceId = H5S.create_simple(1, new ulong[] { (ulong)source.Length }, null);
        long dataSetId = H5D.create(parentId, name, H5T.IEEE_F64LE, spaceId);
        WritePinned(dataSetId, H5T.NATIVE_DOUBLE, H5S.ALL, H5S.ALL, source);
        H5D.close(dataSetId);
        H5S.close(spaceId);
    }

    private void WriteStringDataset(long parentId, string name, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(string.IsNullOrWhiteSpace(value) ? "envelope" : value);
        long spaceId = H5S.create_simple(1, new ulong[] { (ulong)Math.Max(bytes.Length, 1) }, null);
        long dataSetId = H5D.create(parentId, name, H5T.NATIVE_UINT8, spaceId);
        WritePinned(dataSetId, H5T.NATIVE_UINT8, H5S.ALL, H5S.ALL, bytes);
        H5D.close(dataSetId);
        H5S.close(spaceId);
    }

    private void WritePinned<T>(long dataSetId, long typeId, long memSpaceId, long fileSpaceId, T[] buffer) where T : struct
    {
        if (buffer == null || buffer.Length == 0)
            return;

        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            H5D.write(dataSetId, typeId, memSpaceId, fileSpaceId, H5P.DEFAULT, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    private void CloseInternal()
    {
        if (!opened && csvWriter == null && h5FileId < 0)
            return;

        if (format == EmgRecordFormat.Hdf5)
            FlushHdf5Chunk();

        if (csvWriter != null)
        {
            try { csvWriter.Flush(); } catch { }
            try { csvWriter.Dispose(); } catch { }
            csvWriter = null;
        }

        CloseHdf5Handle(ref h5DataSetId, H5D.close);
        CloseHdf5Handle(ref h5TimeSetId, H5D.close);
        CloseHdf5Handle(ref h5SecSetId, H5D.close);
        CloseHdf5Handle(ref h5EmgGroupId, H5G.close);
        CloseHdf5Handle(ref h5MetaGroupId, H5G.close);
        CloseHdf5Handle(ref h5FileId, H5F.close);

        dataChunkBuffer = Array.Empty<float>();
        timeChunkBuffer = Array.Empty<double>();
        secChunkBuffer = Array.Empty<int>();
        activeChannels = Array.Empty<int>();
        channelCount = 0;
        bufferedFrames = 0;
        writtenFrames = 0;
        csvFlushCounter = 0;
        opened = false;
    }

    private void CloseHdf5Handle(ref long id, Func<long, int> closeFunc)
    {
        if (id < 0)
            return;

        try { closeFunc(id); } catch { }
        id = -1;
    }
}
